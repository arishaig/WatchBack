using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Infrastructure.ThoughtProviders;

[JsonSerializable(typeof(PullPushSubmissionsResponseDto))]
[JsonSerializable(typeof(PullPushCommentsResponseDto))]
internal sealed partial class RedditJsonContext : JsonSerializerContext { }

public class RedditThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<RedditOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    ILogger<RedditThoughtProvider> logger)
    : IThoughtProvider
{
    private readonly RedditOptions _options = options.Value;

    public DataProviderMetadata Metadata =>
        new ThoughtProviderMetadata(
            Name: "Reddit",
            Description: "Reddit discussion comments",
            BrandData: new BrandData(
                    Color: "#FF4500",
                    LogoSvg: "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Reddit</title><path d=\"M12 0C5.373 0 0 5.373 0 12c0 3.314 1.343 6.314 3.515 8.485l-2.286 2.286C.775 23.225 1.097 24 1.738 24H12c6.627 0 12-5.373 12-12S18.627 0 12 0Zm4.388 3.199c1.104 0 1.999.895 1.999 1.999 0 1.105-.895 2-1.999 2-.946 0-1.739-.657-1.947-1.539v.002c-1.147.162-2.032 1.15-2.032 2.341v.007c1.776.067 3.4.567 4.686 1.363.473-.363 1.064-.58 1.707-.58 1.547 0 2.802 1.254 2.802 2.802 0 1.117-.655 2.081-1.601 2.531-.088 3.256-3.637 5.876-7.997 5.876-4.361 0-7.905-2.617-7.998-5.87-.954-.447-1.614-1.415-1.614-2.538 0-1.548 1.255-2.802 2.803-2.802.645 0 1.239.218 1.712.585 1.275-.79 2.881-1.291 4.64-1.365v-.01c0-1.663 1.263-3.034 2.88-3.207.188-.911.993-1.595 1.959-1.595Zm-8.085 8.376c-.784 0-1.459.78-1.506 1.797-.047 1.016.64 1.429 1.426 1.429.786 0 1.371-.369 1.418-1.385.047-1.017-.553-1.841-1.338-1.841Zm7.406 0c-.786 0-1.385.824-1.338 1.841.047 1.017.634 1.385 1.418 1.385.785 0 1.473-.413 1.426-1.429-.046-1.017-.721-1.797-1.506-1.797Zm-3.703 4.013c-.974 0-1.907.048-2.77.135-.147.015-.241.168-.183.305.483 1.154 1.622 1.964 2.953 1.964 1.33 0 2.47-.81 2.953-1.964.057-.137-.037-.29-.184-.305-.863-.087-1.795-.135-2.769-.135Z\"/></svg>"
                )
            );

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, CancellationToken ct = default)
    {
        try
        {
            if (mediaContext is not EpisodeContext episode)
            {
                return new ThoughtResult(Source: "Reddit", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
            }

            var cacheKey = $"reddit:thoughts:{mediaContext.Title}:S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
            if (cache.TryGetValue(cacheKey, out ThoughtResult? cached))
            {
                return cached;
            }

            // Build multiple search queries mirroring the Python implementation:
            // 1. Show title + episode code (padded and unpadded)
            // 2. Episode code only within a guessed subreddit (catches threads with no show name in title)
            var sLong = episode.SeasonNumber.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var eLong = episode.EpisodeNumber.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var sn = episode.SeasonNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var en = episode.EpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Derive subreddit name: strip non-alphanumeric from show title (e.g. "Halt and Catch Fire" → "haltandcatchfire")
            var derivedSubreddit = System.Text.RegularExpressions.Regex.Replace(
                mediaContext.Title.ToLowerInvariant(), "[^a-z0-9]", "");

            var searches = new List<(string Title, string? Subreddit)>
            {
                ($"{mediaContext.Title} S{sn}E{en}", null),
                ($"{mediaContext.Title} S{sLong}E{eLong}", null),
                ($"S{sn}E{en}", derivedSubreddit),
                ($"S{sLong}E{eLong}", derivedSubreddit),
            };

            var seenIds = new Dictionary<string, PullPushSubmissionDto>();
            foreach (var (titleQuery, subreddit) in searches)
            {
                var url = $"https://api.pullpush.io/reddit/search/submission/?title={Uri.EscapeDataString(titleQuery)}&size=25&sort_type=score&sort=desc";
                if (subreddit != null)
                    url += $"&subreddit={Uri.EscapeDataString(subreddit)}";

                try
                {
                    var resp = await httpClient.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        logger.LogWarning("PullPush submission search failed: HTTP {Status} for query '{Query}'",
                            (int)resp.StatusCode, titleQuery);
                        continue;
                    }
                    var respContent = await resp.Content.ReadAsStringAsync(ct);
                    var listing = JsonSerializer.Deserialize<PullPushSubmissionsResponseDto>(
                        respContent, RedditJsonContext.Default.PullPushSubmissionsResponseDto);
                    foreach (var sub in listing?.Data ?? [])
                    {
                        if (sub.Id != null && !seenIds.ContainsKey(sub.Id))
                            seenIds[sub.Id] = sub;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "PullPush submission search exception for query '{Query}'", titleQuery);
                }
            }

            // Log raw results before filtering so we can diagnose misses
            foreach (var (id, sub) in seenIds)
            {
                var passes = MatchesEpisode(sub.Title ?? "", episode.SeasonNumber, episode.EpisodeNumber);
                logger.LogInformation("PullPush raw: [{Id}] r/{Sub} \"{Title}\" → {Result}",
                    id, sub.Subreddit, sub.Title, passes ? "KEEP" : "SKIP");
            }

            // Keep only submissions whose title actually references this episode
            var submissions = seenIds.Values
                .Where(s => MatchesEpisode(s.Title ?? "", episode.SeasonNumber, episode.EpisodeNumber))
                .OrderByDescending(s => s.Score ?? 0)
                .ToList();

            logger.LogInformation("PullPush: found {Count} matching submission(s) for '{Title}' S{Season}E{Episode}",
                submissions.Count, mediaContext.Title, sLong, eLong);

            if (submissions.Count == 0)
            {
                return new ThoughtResult(Source: "Reddit", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
            }

            var allThoughts = new List<Thought>();
            string? postTitle = null;
            string? postUrl = null;

            for (int i = 0; i < submissions.Count && i < _options.MaxThreads; i++)
            {
                var submission = submissions[i];
                if (submission.Id == null)
                    continue;

                var thisPostUrl = submission.Permalink != null
                    ? $"https://reddit.com{submission.Permalink}"
                    : $"https://reddit.com/r/{submission.Subreddit}/comments/{submission.Id}/";
                var thisPostTitle = submission.Title;

                // Capture OP selftext for self-posts (not deleted/removed),
                // collapsing runs of 3+ newlines down to 2 (one blank line)
                var selftext = submission.Selftext;
                string? thisPostBody = null;
                if (submission.IsSelf &&
                    !string.IsNullOrWhiteSpace(selftext) &&
                    !selftext.Equals("[deleted]", StringComparison.Ordinal) &&
                    !selftext.Equals("[removed]", StringComparison.Ordinal))
                {
                    thisPostBody = CollapseNewlines(selftext);
                }

                if (postTitle == null)
                {
                    postTitle = thisPostTitle;
                    postUrl = thisPostUrl;
                }

                var threadThoughts = new List<Thought>();

                // Get comments for this submission via PullPush
                var commentsUrl = $"https://api.pullpush.io/reddit/search/comment/?link_id={Uri.EscapeDataString(submission.Id)}&size={_options.MaxComments}&sort_type=score&sort=desc";
                var commentsResponse = await httpClient.GetAsync(commentsUrl, ct);

                if (!commentsResponse.IsSuccessStatusCode)
                {
                    logger.LogWarning("PullPush comment fetch failed: HTTP {Status} for thread {ThreadId}",
                        (int)commentsResponse.StatusCode, submission.Id);
                    continue;
                }

                var commentsContent = await commentsResponse.Content.ReadAsStringAsync(ct);
                var commentsList = JsonSerializer.Deserialize<PullPushCommentsResponseDto>(
                    commentsContent,
                    RedditJsonContext.Default.PullPushCommentsResponseDto);

                var comments = commentsList?.Data ?? [];
                logger.LogInformation("PullPush: thread {ThreadId} ({Title}) returned {Count} comment(s)", submission.Id, submission.Title, comments.Length);
                threadThoughts.AddRange(comments
                    .Select(c => MapCommentToThought(c, thisPostTitle, thisPostUrl, thisPostBody))
                    .Where(t => !IsDeletedOrRemoved(t)));

                allThoughts.AddRange(threadThoughts);
            }

            var treeThoughts = treeBuilder.BuildTree(allThoughts);

            var result = new ThoughtResult(
                Source: "Reddit",
                PostTitle: postTitle,
                PostUrl: postUrl,
                ImageUrl: null,
                Thoughts: treeThoughts,
                NextPageToken: null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reddit/PullPush thought fetch failed");
            return new ThoughtResult(Source: "Reddit", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetAsync("https://api.pullpush.io/reddit/search/comment/?size=0", ct);

            return new ServiceHealth(
                IsHealthy: response.IsSuccessStatusCode,
                Message: response.IsSuccessStatusCode ? "OK" : response.StatusCode.ToString(),
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ServiceHealth(
                IsHealthy: false,
                Message: ex.Message,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    private static Thought MapCommentToThought(PullPushCommentDto data, string? postTitle, string? postUrl, string? postBody)
    {
        var parentId = StripRedditPrefix(data.ParentId);

        // Build per-comment permalink: {postUrl}{commentId}/
        var commentUrl = postUrl != null && data.Id != null
            ? $"{postUrl.TrimEnd('/')}/{Uri.EscapeDataString(data.Id)}/"
            : null;

        return new Thought(
            Id: $"reddit:{data.Id}",
            ParentId: parentId,
            Title: null,
            Content: data.Body ?? "",
            Url: commentUrl,
            Images: [],
            Author: data.Author ?? "Unknown",
            Score: data.Score,
            CreatedAt: data.CreatedUtc.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(data.CreatedUtc.Value)
                : DateTimeOffset.UtcNow,
            Source: "Reddit",
            Replies: [],
            PostTitle: postTitle,
            PostUrl: postUrl,
            PostBody: postBody);
    }

    private static string? StripRedditPrefix(string? redditId)
    {
        if (string.IsNullOrEmpty(redditId))
            return null;

        // t3_ prefix = parent is the submission itself → top-level comment
        if (redditId.StartsWith("t3_", StringComparison.Ordinal))
            return null;

        // t1_ prefix = parent is another comment → re-add "reddit:" to match thought IDs
        if (redditId.StartsWith("t1_", StringComparison.Ordinal))
            return string.Concat("reddit:", redditId.AsSpan(3));

        return redditId;
    }

    private static readonly Regex ExcessiveNewlines = new(@"\n{3,}", RegexOptions.Compiled);

    private static string CollapseNewlines(string text) =>
        ExcessiveNewlines.Replace(text.Trim(), "\n\n");

    private static bool MatchesEpisode(string title, int season, int episode)
    {
        var t = title.ToUpperInvariant();
        var s = season.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sL = season.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        var e = episode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var eL = episode.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

        // Match SxxExx patterns (e.g. S4E6, S04E06, S4E06, S04E6)
        foreach (var code in new[] { $"S{sL}E{eL}", $"S{s}E{eL}", $"S{sL}E{e}", $"S{s}E{e}" })
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(t, $"{code}(?!\\d)"))
                return true;
        }

        // Match NxNN patterns (e.g. 4x06, 4x6)
        if (System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{s}X{eL}(?!\d)") ||
            System.Text.RegularExpressions.Regex.IsMatch(t, $@"\b{s}X{e}(?!\d)"))
            return true;

        // Match "Season N ... Episode N"
        if (System.Text.RegularExpressions.Regex.IsMatch(t, $@"\bSEASON\s+{s}\b.{{0,30}}\bEPISODE\s+{e}\b"))
            return true;

        return false;
    }

    private static bool IsDeletedOrRemoved(Thought thought)
    {
        return thought.Content.Contains("[deleted]", StringComparison.Ordinal) ||
               thought.Content.Contains("[removed]", StringComparison.Ordinal);
    }
}

// PullPush API returns { "data": [...] } — a flat array, not the Reddit native children wrapper.

internal sealed record PullPushCommentDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("created_utc")] long? CreatedUtc,
    [property: JsonPropertyName("parent_id")] string? ParentId);

internal sealed record PullPushSubmissionDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("permalink")] string? Permalink,
    [property: JsonPropertyName("subreddit")] string? Subreddit,
    [property: JsonPropertyName("created_utc")] long? CreatedUtc,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("is_self")] bool IsSelf,
    [property: JsonPropertyName("selftext")] string? Selftext);

internal sealed record PullPushSubmissionsResponseDto(
    [property: JsonPropertyName("data")] PullPushSubmissionDto[]? Data);

internal sealed record PullPushCommentsResponseDto(
    [property: JsonPropertyName("data")] PullPushCommentDto[]? Data);