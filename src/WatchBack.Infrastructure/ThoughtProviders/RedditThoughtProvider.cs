using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Resources;

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
    private static readonly ThoughtResult Empty = new(Source: "Reddit", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);

    private readonly RedditOptions _options = options.Value;

    public DataProviderMetadata Metadata => new ThoughtProviderMetadata(
        Name: "Reddit",
        Description: UiStrings.RedditThoughtProvider_Metadata_Comments_from_Reddit,
        BrandData: new BrandData(
            Color: "#FF4500",
            LogoSvg:
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Reddit</title><path d=\"M12 0C5.373 0 0 5.373 0 12c0 3.314 1.343 6.314 3.515 8.485l-2.286 2.286C.775 23.225 1.097 24 1.738 24H12c6.627 0 12-5.373 12-12S18.627 0 12 0Zm4.388 3.199c1.104 0 1.999.895 1.999 1.999 0 1.105-.895 2-1.999 2-.946 0-1.739-.657-1.947-1.539v.002c-1.147.162-2.032 1.15-2.032 2.341v.007c1.776.067 3.4.567 4.686 1.363.473-.363 1.064-.58 1.707-.58 1.547 0 2.802 1.254 2.802 2.802 0 1.117-.655 2.081-1.601 2.531-.088 3.256-3.637 5.876-7.997 5.876-4.361 0-7.905-2.617-7.998-5.87-.954-.447-1.614-1.415-1.614-2.538 0-1.548 1.255-2.802 2.803-2.802.645 0 1.239.218 1.712.585 1.275-.79 2.881-1.291 4.64-1.365v-.01c0-1.663 1.263-3.034 2.88-3.207.188-.911.993-1.595 1.959-1.595Zm-8.085 8.376c-.784 0-1.459.78-1.506 1.797-.047 1.016.64 1.429 1.426 1.429.786 0 1.371-.369 1.418-1.385.047-1.017-.553-1.841-1.338-1.841Zm7.406 0c-.786 0-1.385.824-1.338 1.841.047 1.017.634 1.385 1.418 1.385.785 0 1.473-.413 1.426-1.429-.046-1.017-.721-1.797-1.506-1.797Zm-3.703 4.013c-.974 0-1.907.048-2.77.135-.147.015-.241.168-.183.305.483 1.154 1.622 1.964 2.953 1.964 1.33 0 2.47-.81 2.953-1.964.057-.137-.037-.29-.184-.305-.863-.087-1.795-.135-2.769-.135Z\"/></svg>"
        )
    );

    // Upper bound on search spec count (before deduplication) — used for progress estimation.
    // Sync ticks are reported one-per-spec, so over-estimating is fine; the SSE layer forces
    // the bar to 100% before emitting the final result.
    private const int SearchSpecCount = 8;

    public int ExpectedWeight => (SearchSpecCount + _options.MaxThreads) * 3;

    public string GetCacheKey(MediaContext mediaContext)
    {
        var episode = mediaContext as EpisodeContext;
        return episode != null
            ? $"reddit:thoughts:{mediaContext.Title}:S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}:t{_options.MaxThreads}"
            : $"reddit:thoughts:{mediaContext.Title}:t{_options.MaxThreads}";
    }

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (mediaContext is not EpisodeContext episode)
            {
                return Empty;
            }

            var cacheKey = GetCacheKey(mediaContext);
            if (cache.TryGetValue(cacheKey, out ThoughtResult? cached))
            {
                return cached;
            }

            // Derive subreddit name: strip non-alphanumeric from show title (e.g. "Halt and Catch Fire" → "haltandcatchfire")
            var derivedSubreddit = Regex.Replace(
                mediaContext.Title.ToLowerInvariant(), "[^a-z0-9]", "");

            var specs = BuildSearchSpecs(mediaContext.Title, derivedSubreddit, episode);

            // Run all search specs in parallel — PullPush is the bottleneck, so fan-out wins here
            var specResults = await Task.WhenAll(specs.Select(async spec =>
            {
                var size = spec.BypassFilter ? 10 : 25;
                var url = $"https://api.pullpush.io/reddit/search/submission/?title={Uri.EscapeDataString(spec.Title)}&size={size}&sort_type=score&sort=desc";
                if (spec.Subreddit != null)
                    url += $"&subreddit={Uri.EscapeDataString(spec.Subreddit)}";

                try
                {
                    var resp = await httpClient.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        logger.LogWarning("PullPush submission search failed: HTTP {Status} for query '{Query}'",
                            (int)resp.StatusCode, spec.Title);
                        return (Subs: Array.Empty<PullPushSubmissionDto>(), IsBypass: spec.BypassFilter, Ok: false);
                    }
                    var respContent = await resp.Content.ReadAsStringAsync(ct);
                    var listing = JsonSerializer.Deserialize<PullPushSubmissionsResponseDto>(
                        respContent, RedditJsonContext.Default.PullPushSubmissionsResponseDto);
                    return (Subs: listing?.Data ?? [], IsBypass: spec.BypassFilter, Ok: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "PullPush submission search exception for query '{Query}'", spec.Title);
                    return (Subs: Array.Empty<PullPushSubmissionDto>(), IsBypass: spec.BypassFilter, Ok: false);
                }
                finally
                {
                    progress?.Report(new SyncProgressTick(3, "Reddit"));
                }
            }));

            var anySpecSucceeded = specResults.Any(r => r.Ok);

            // Merge deduplicated results from all specs
            var seenIds = new Dictionary<string, PullPushSubmissionDto>();
            var bypassIds = new HashSet<string>();
            foreach (var (subs, isBypass, _) in specResults)
            {
                foreach (var sub in subs)
                {
                    if (sub.Id == null) continue;
                    seenIds.TryAdd(sub.Id, sub);
                    if (isBypass)
                        bypassIds.Add(sub.Id);
                }
            }

            // Log raw results before filtering so we can diagnose misses
            foreach (var (id, sub) in seenIds)
            {
                var passes = bypassIds.Contains(id) || MatchesEpisode(sub.Title ?? "", episode.SeasonNumber, episode.EpisodeNumber);
                logger.LogInformation("PullPush raw: [{Id}] r/{Sub} \"{Title}\" → {Result}",
                    id, sub.Subreddit, sub.Title, passes ? "KEEP" : "SKIP");
            }

            // Keep submissions whose title matches this episode, or that came from a bypass search
            // (episode-title search, where PullPush already did the scoping)
            var submissions = seenIds.Values
                .Where(s => s.Id != null && (bypassIds.Contains(s.Id!) || MatchesEpisode(s.Title ?? "", episode.SeasonNumber, episode.EpisodeNumber)))
                .OrderByDescending(s => s.Score ?? 0)
                .ToList();

            logger.LogInformation("PullPush: found {Count} matching submission(s) for '{Title}' S{Season:D2}E{Episode:D2}",
                submissions.Count, mediaContext.Title, episode.SeasonNumber, episode.EpisodeNumber);

            if (submissions.Count == 0)
            {
                var emptyResult = Empty;
                // Only cache confirmed archive misses (at least one 2xx from PullPush).
                // Timeout/network failures are transient — don't prevent future retries.
                if (anySpecSucceeded)
                    cache.Set(cacheKey, emptyResult, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
                return emptyResult;
            }

            // Fetch comments for all selected threads in parallel
            var threadResults = await Task.WhenAll(
                submissions.Take(_options.MaxThreads).Select(async submission =>
                {
                    if (submission.Id == null)
                        return Array.Empty<Thought>();

                    var thisPostUrl = submission.Permalink != null
                        ? $"https://reddit.com{submission.Permalink}"
                        : $"https://reddit.com/r/{submission.Subreddit}/comments/{submission.Id}/";

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

                    var commentsUrl = $"https://api.pullpush.io/reddit/search/comment/?link_id={Uri.EscapeDataString(submission.Id)}&size={_options.MaxComments}&sort_type=score&sort=desc";
                    var commentsResponse = await httpClient.GetAsync(commentsUrl, ct);
                    progress?.Report(new SyncProgressTick(3, "Reddit"));

                    if (!commentsResponse.IsSuccessStatusCode)
                    {
                        logger.LogWarning("PullPush comment fetch failed: HTTP {Status} for thread {ThreadId}",
                            (int)commentsResponse.StatusCode, submission.Id);
                        return Array.Empty<Thought>();
                    }

                    var commentsContent = await commentsResponse.Content.ReadAsStringAsync(ct);
                    var commentsList = JsonSerializer.Deserialize<PullPushCommentsResponseDto>(
                        commentsContent,
                        RedditJsonContext.Default.PullPushCommentsResponseDto);

                    var comments = commentsList?.Data ?? [];
                    logger.LogInformation("PullPush: thread {ThreadId} ({Title}) returned {Count} comment(s)",
                        submission.Id, submission.Title, comments.Length);

                    return comments
                        .Select(c => MapCommentToThought(c, submission.Title, thisPostUrl, thisPostBody))
                        .Where(t => !IsDeletedOrRemoved(t))
                        .ToArray();
                }));

            var allThoughts = threadResults.SelectMany(t => t).ToList();
            var treeThoughts = treeBuilder.BuildTree(allThoughts);

            var top = submissions[0];
            var topPostUrl = top.Permalink != null
                ? $"https://reddit.com{top.Permalink}"
                : $"https://reddit.com/r/{top.Subreddit}/comments/{top.Id}/";

            var result = new ThoughtResult(
                Source: "Reddit",
                PostTitle: top.Title,
                PostUrl: topPostUrl,
                ImageUrl: null,
                Thoughts: treeThoughts,
                NextPageToken: null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reddit/PullPush thought fetch failed");
            return Empty;
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await httpClient.GetAsync("https://api.pullpush.io/reddit/search/comment/?size=0", cts.Token);

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

    public string? ConfigSection => "Reddit";

    // Reddit requires no credentials — always operationally configured.
    public bool IsConfigured => true;

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden) =>
    [
        new(Key: "Reddit__MaxThreads",
            Label: UiStrings.ConfigEndpoints_GetConfig_Max_Threads,
            Type: "number",
            Placeholder: "3",
            HasValue: true,
            Value: _options.MaxThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EnvValue: envVal("Reddit__MaxThreads"),
            IsOverridden: isOverridden("Reddit", "MaxThreads")),
        new(Key: "Reddit__MaxComments",
            Label: UiStrings.ConfigEndpoints_GetConfig_Max_Comments,
            Type: "number",
            Placeholder: "250",
            HasValue: true,
            Value: _options.MaxComments.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EnvValue: envVal("Reddit__MaxComments"),
            IsOverridden: isOverridden("Reddit", "MaxComments")),
    ];

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

    private static string CollapseNewlines(string text)
    {
        return ExcessiveNewlines.Replace(text.Trim(), "\n\n");
    }

    // Cache compiled regexes keyed by (season, episode) — small, bounded set in practice
    // Builds the ordered list of PullPush search specs for a given episode.
    // Each spec maps to exactly one API call and one progress tick (weight 3).
    // Specs are deduplicated so that e.g. S10E10 (padded == unpadded) doesn't run twice.
    //
    // Adding a new format: add a new SearchSpec line below.  The MatchesEpisode patterns
    // already recognize all standard formats (SxxExx, NxNN, "Season N Episode N"), so
    // new search formats just need to be added here to be searched and matched.
    private static List<SearchSpec> BuildSearchSpecs(
        string showTitle, string derivedSubreddit, EpisodeContext episode)
    {
        var sn = episode.SeasonNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ss = episode.SeasonNumber.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        var en = episode.EpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ee = episode.EpisodeNumber.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

        List<SearchSpec> specs =
        [
            // Global: show title + episode code — catches cross-subreddit threads (e.g. r/television)
            new($"{showTitle} S{sn}E{en}",    Subreddit: null),
            new($"{showTitle} S{ss}E{ee}",    Subreddit: null),
            new($"{showTitle} {sn}x{ee}",     Subreddit: null),  // NxNN (e.g. "Halt and Catch Fire 3x02")

            // Show subreddit: bare episode code — catches terse titles with no show name
            new($"S{sn}E{en}",               derivedSubreddit),
            new($"S{ss}E{ee}",               derivedSubreddit),
            new($"{sn}x{ee}",                derivedSubreddit),  // NxNN (e.g. "3x02 Joe's Deposition...")
            new($"Season {sn} Episode {en}", derivedSubreddit),  // long form (e.g. "Season 3 Episode 2 Rewatch")
        ];

        // Episode title: PullPush already scopes results, so MatchesEpisode is bypassed for these
        if (!string.IsNullOrWhiteSpace(episode.EpisodeTitle))
            specs.Add(new(episode.EpisodeTitle, derivedSubreddit, BypassFilter: true));

        // Deduplicate: when season/episode >= 10, padded == unpadded (e.g. S10E10 == S10E10)
        return specs
            .DistinctBy(s => (s.Title.ToUpperInvariant(), s.Subreddit?.ToUpperInvariant()))
            .ToList();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Regex[]> s_episodeRegexCache = new();

    private static Regex[] GetEpisodePatterns(int season, int episode)
    {
        return s_episodeRegexCache.GetOrAdd((season, episode), key =>
        {
            var (s, e) = key;
            var sStr = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sL = s.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var eStr = e.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var eL = e.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

            var patterns = new[]
            {
                $"S{sL}E{eL}",
                $"S{sStr}E{eL}",
                $"S{sL}E{eStr}",
                $"S{sStr}E{eStr}"
            }.Select(code => $@"(?<!\d){code}(?!\d)").ToList();

            // SxxExx patterns with lookbehind/lookahead to prevent partial matches

            // NxNN patterns
            patterns.Add($@"\b{sStr}X{eL}(?!\d)");
            patterns.Add($@"\b{sStr}X{eStr}(?!\d)");

            // "Season N ... Episode N"
            patterns.Add($@"\bSEASON\s+{sStr}\b.{{0,30}}\bEPISODE\s+{eStr}\b");

            return patterns.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase)).ToArray();
        });
    }

    private static bool MatchesEpisode(string title, int season, int episode)
    {
        var patterns = GetEpisodePatterns(season, episode);
        return patterns.Any(regex => regex.IsMatch(title));
    }

    private static bool IsDeletedOrRemoved(Thought thought)
    {
        return thought.Content.Contains("[deleted]", StringComparison.Ordinal) ||
               thought.Content.Contains("[removed]", StringComparison.Ordinal);
    }
}

// One PullPush submission search: the title query, an optional subreddit filter, and a flag that
// bypasses MatchesEpisode for results (used for episode-title searches where PullPush already
// scopes results and the title needn't contain an episode code).
internal sealed record SearchSpec(string Title, string? Subreddit, bool BypassFilter = false);

// PullPush API returns { "data": [...] } — a flat array, not the Reddit native children wrapper.

// PullPush sometimes returns created_utc as a float (e.g. 1234567890.0) rather than an integer.
// AllowReadingFromString handles string-encoded numbers but not float-encoded integers, so we
// need a custom converter that accepts integer, float, and string tokens.
internal sealed class UnixTimestampConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var l)) return l;
                if (reader.TryGetDouble(out var d)) return (long)d;
                return null;
            case JsonTokenType.String:
                var s = reader.GetString();
                if (s == null) return null;
                if (long.TryParse(s, out var ls)) return ls;
                if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ds)) return (long)ds;
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

internal sealed record PullPushCommentDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("created_utc"), JsonConverter(typeof(UnixTimestampConverter))] long? CreatedUtc,
    [property: JsonPropertyName("parent_id")] string? ParentId);

internal sealed record PullPushSubmissionDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("permalink")] string? Permalink,
    [property: JsonPropertyName("subreddit")] string? Subreddit,
    [property: JsonPropertyName("created_utc"), JsonConverter(typeof(UnixTimestampConverter))] long? CreatedUtc,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("is_self")] bool IsSelf,
    [property: JsonPropertyName("selftext")] string? Selftext);

internal sealed record PullPushSubmissionsResponseDto(
    [property: JsonPropertyName("data")] PullPushSubmissionDto[]? Data);

internal sealed record PullPushCommentsResponseDto(
    [property: JsonPropertyName("data")] PullPushCommentDto[]? Data);