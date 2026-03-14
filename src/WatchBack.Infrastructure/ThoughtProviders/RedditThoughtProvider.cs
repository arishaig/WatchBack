using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Infrastructure.Thoughts;

[JsonSerializable(typeof(RedditSubmissionsListingDto))]
[JsonSerializable(typeof(RedditCommentsListingDto))]
internal sealed partial class RedditJsonContext : JsonSerializerContext { }

public class RedditThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<RedditOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder)
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

            // Search for submissions
            var query = $"{mediaContext.Title} S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
            var submissionsUrl = $"https://api.pushshift.io/reddit/search/submission?title={Uri.EscapeDataString(query)}&size={_options.MaxThreads}";
            var submissionsResponse = await httpClient.GetAsync(submissionsUrl, ct);

            if (!submissionsResponse.IsSuccessStatusCode)
            {
                return new ThoughtResult(Source: "Reddit", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
            }

            var submissionsContent = await submissionsResponse.Content.ReadAsStringAsync(ct);
            var submissionsList = JsonSerializer.Deserialize<RedditSubmissionsListingDto>(
                submissionsContent,
                RedditJsonContext.Default.RedditSubmissionsListingDto);

            var submissions = submissionsList?.Data?.Children ?? [];
            if (submissions.Length == 0)
            {
                return new ThoughtResult(Source: "Reddit", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
            }

            var allThoughts = new List<Thought>();
            string? postTitle = null;
            string? postUrl = null;

            for (int i = 0; i < submissions.Length && i < _options.MaxThreads; i++)
            {
                var submission = submissions[i];
                if (submission.Data?.Id == null)
                    continue;

                if (postTitle == null)
                {
                    postTitle = submission.Data.Title;
                    postUrl = $"https://reddit.com{submission.Data.Permalink}";
                }

                var threadThoughts = new List<Thought>();

                // Prepend the OP selftext as a root thought for self-posts
                var selftext = submission.Data.Selftext;
                if (submission.Data.IsSelf &&
                    !string.IsNullOrWhiteSpace(selftext) &&
                    !selftext.Equals("[deleted]", StringComparison.Ordinal) &&
                    !selftext.Equals("[removed]", StringComparison.Ordinal))
                {
                    threadThoughts.Add(new Thought(
                        Id: $"reddit:{submission.Data.Id}",
                        ParentId: null,
                        Title: submission.Data.Title,
                        Content: selftext,
                        Url: $"https://reddit.com{submission.Data.Permalink}",
                        Images: [],
                        Author: submission.Data.Author ?? "Unknown",
                        Score: submission.Data.Score,
                        CreatedAt: submission.Data.CreatedUtc.HasValue
                            ? DateTimeOffset.FromUnixTimeSeconds(submission.Data.CreatedUtc.Value)
                            : DateTimeOffset.UtcNow,
                        Source: "Reddit",
                        Replies: []));
                }

                // Get comments for this submission
                var commentsUrl = $"https://api.pushshift.io/reddit/search/comment?link_id={Uri.EscapeDataString(submission.Data.Id)}&size={_options.MaxComments}";
                var commentsResponse = await httpClient.GetAsync(commentsUrl, ct);

                if (!commentsResponse.IsSuccessStatusCode)
                    continue;

                var commentsContent = await commentsResponse.Content.ReadAsStringAsync(ct);
                var commentsList = JsonSerializer.Deserialize<RedditCommentsListingDto>(
                    commentsContent,
                    RedditJsonContext.Default.RedditCommentsListingDto);

                var comments = commentsList?.Data?.Children ?? [];
                threadThoughts.AddRange(comments
                    .Select(c => MapCommentToThought(c.Data))
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
        catch
        {
            return new ThoughtResult(Source: "Reddit", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetAsync("https://api.pushshift.io/reddit/search/comment?size=0", ct);

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

    private static Thought MapCommentToThought(RedditCommentDataDto? data)
    {
        var parentId = StripRedditPrefix(data?.ParentId);

        return new Thought(
            Id: $"reddit:{data?.Id}",
            ParentId: parentId,
            Title: null,
            Content: data?.Body ?? "",
            Url: null,
            Images: [],
            Author: data?.Author ?? "Unknown",
            Score: data?.Score,
            CreatedAt: data?.CreatedUtc.HasValue == true
                ? DateTimeOffset.FromUnixTimeSeconds(data.CreatedUtc.Value)
                : DateTimeOffset.UtcNow,
            Source: "Reddit",
            Replies: []);
    }

    private static string? StripRedditPrefix(string? redditId)
    {
        if (string.IsNullOrEmpty(redditId))
            return null;

        // Strip type prefix (t1_ = comment, t3_ = submission) and re-add "reddit:"
        // so parentIds match the "reddit:{id}" format used for thought IDs.
        if (redditId.StartsWith("t1_", StringComparison.Ordinal) ||
            redditId.StartsWith("t3_", StringComparison.Ordinal))
        {
            return "reddit:" + redditId.Substring(3);
        }

        return redditId;
    }

    private static bool IsDeletedOrRemoved(Thought thought)
    {
        return thought.Content.Contains("[deleted]", StringComparison.Ordinal) ||
               thought.Content.Contains("[removed]", StringComparison.Ordinal);
    }
}

internal sealed record RedditCommentDataDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("created_utc")] long? CreatedUtc,
    [property: JsonPropertyName("parent_id")] string? ParentId);

internal sealed record RedditSubmissionDataDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("permalink")] string? Permalink,
    [property: JsonPropertyName("created_utc")] long? CreatedUtc,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("is_self")] bool IsSelf,
    [property: JsonPropertyName("selftext")] string? Selftext);

internal sealed record RedditSubmissionChildDto(
    [property: JsonPropertyName("data")] RedditSubmissionDataDto? Data);

internal sealed record RedditSubmissionsListingDataDto(
    [property: JsonPropertyName("children")] RedditSubmissionChildDto[]? Children);

internal sealed record RedditSubmissionsListingDto(
    [property: JsonPropertyName("data")] RedditSubmissionsListingDataDto? Data);

internal sealed record RedditCommentChildDto(
    [property: JsonPropertyName("data")] RedditCommentDataDto? Data);

internal sealed record RedditCommentsListingDataDto(
    [property: JsonPropertyName("children")] RedditCommentChildDto[]? Children);

internal sealed record RedditCommentsListingDto(
    [property: JsonPropertyName("data")] RedditCommentsListingDataDto? Data);
