using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace WatchBack.Infrastructure.ThoughtProviders;

// Arctic Shift (https://github.com/ArthurHeitmann/arctic_shift/tree/master/api) client helpers.
// Submissions reuse the PullPush DTOs — the field names are identical to Reddit's own.
// Every method returns null on failure so callers can tell "request failed" from "no results".
internal static class ArcticShiftSource
{
    private const string BaseUrl = "https://arctic-shift.photon-reddit.com";
    public const int PageSize = 100;

    // Only these fields are selectable on posts; naming any other one (permalink, is_self, stickied)
    // makes the API reject the whole request. The permalink is rebuilt from subreddit and id.
    public const string PostFields = "id,title,subreddit,created_utc,score,num_comments,selftext,link_flair_text,author";

    public const string SubredditFields = "display_name,subscribers,public_description";

    public const string HealthUrl = BaseUrl + "/api/posts/search?subreddit=announcements&limit=1&fields=id";

    // Arctic Shift rate limits are "calculated dynamically based on server load", and the heavy
    // queries can take seconds. In-flight requests are capped per host by the Reddit client's
    // resilience pipeline (see ServiceCollectionExtensions), which also covers prefetch.

    // One page of a subreddit's posts in a time window, oldest first. Title keyword search times out on
    // active subreddits, so the window is listed and matched locally instead: one request usually covers
    // an episode's discussion. Full means the page was full, so later posts in the window were not seen;
    // the next page starts after the last post's created_utc.
    public static async Task<ListingPage> ListWindowAsync(
        HttpClient httpClient, string subreddit, long after, long before, ILogger logger, CancellationToken ct)
    {
        string url =
            $"{BaseUrl}/api/posts/search?subreddit={Uri.EscapeDataString(subreddit)}&after={after}&before={before}&limit={PageSize}&sort=asc&fields={PostFields}";
        return ListingPage.From(await GetSubmissionsAsync(httpClient, url, logger, ct));
    }

    // The newest page of a subreddit's posts, for when the air date is unknown (prefetch predicts the next
    // episode, which has none yet). The next episode's thread, if it exists, is recent.
    public static async Task<ListingPage> ListRecentAsync(
        HttpClient httpClient, string subreddit, ILogger logger, CancellationToken ct)
    {
        string url =
            $"{BaseUrl}/api/posts/search?subreddit={Uri.EscapeDataString(subreddit)}&limit={PageSize}&sort=desc&fields={PostFields}";
        return ListingPage.From(await GetSubmissionsAsync(httpClient, url, logger, ct));
    }

    // Title keyword search in a time window. Needed for subreddits too busy to list (r/movies, r/television)
    // but slow there and often answered with a timeout error, so callers treat it as best effort.
    public static async Task<ListingPage> SearchTitleAsync(
        HttpClient httpClient, string subreddit, string title, long after, long before, ILogger logger,
        CancellationToken ct)
    {
        string url =
            $"{BaseUrl}/api/posts/search?subreddit={Uri.EscapeDataString(subreddit)}&title={Uri.EscapeDataString(title)}&after={after}&before={before}&limit={PageSize}&fields={PostFields}";
        return ListingPage.From(await GetSubmissionsAsync(httpClient, url, logger, ct));
    }

    // Subreddits whose name starts with prefix, most subscribers first.
    public static async Task<ArcticShiftSubredditDto[]?> SearchSubredditsAsync(
        HttpClient httpClient, string prefix, ILogger logger, CancellationToken ct)
    {
        string url =
            $"{BaseUrl}/api/subreddits/search?subreddit_prefix={Uri.EscapeDataString(prefix)}&limit=25&fields={SubredditFields}";
        try
        {
            HttpResponseMessage response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Arctic Shift subreddit search failed: HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            ArcticShiftSubredditsResponseDto? listing = JsonSerializer.Deserialize(
                content, RedditJsonContext.Default.ArcticShiftSubredditsResponseDto);
            if (listing?.Data is null || listing.Error is not null)
            {
                logger.LogWarning("Arctic Shift subreddit search returned an error body: {Error}", listing?.Error);
                return null;
            }

            return listing.Data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Arctic Shift subreddit search exception");
            return null;
        }
    }

    // Full comment tree for one submission, flattened. Unlike /comments/search it isn't capped at 100.
    public static async Task<PullPushCommentDto[]?> GetCommentsAsync(
        HttpClient httpClient, string submissionId, int limit, ILogger logger, CancellationToken ct)
    {
        string url = $"{BaseUrl}/api/comments/tree?link_id=t3_{Uri.EscapeDataString(submissionId)}&limit={limit}";

        try
        {
            HttpResponseMessage response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Arctic Shift comment fetch failed: HTTP {Status} for thread {ThreadId}",
                    (int)response.StatusCode, submissionId);
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            ArcticShiftCommentTreeResponseDto? tree = JsonSerializer.Deserialize(
                content, RedditJsonContext.Default.ArcticShiftCommentTreeResponseDto);
            if (tree?.Data is null || tree.Error is not null)
            {
                logger.LogWarning("Arctic Shift comment fetch returned an error body for thread {ThreadId}: {Error}",
                    submissionId, tree?.Error);
                return null;
            }

            List<PullPushCommentDto> flat = [];
            Flatten(tree.Data, flat);
            return [.. flat];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Arctic Shift comment fetch exception for thread {ThreadId}", submissionId);
            return null;
        }
    }

    private static async Task<PullPushSubmissionDto[]?> GetSubmissionsAsync(
        HttpClient httpClient, string url, ILogger logger, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Arctic Shift submission search failed: HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            ArcticShiftSubmissionsResponseDto? listing = JsonSerializer.Deserialize(
                content, RedditJsonContext.Default.ArcticShiftSubmissionsResponseDto);

            // Timeouts arrive as {"data":null,"error":"Timeout. Maybe slow down a bit"} — that is a
            // failure, not an empty result, or a transient timeout would be cached as "no threads".
            if (listing?.Data is null || listing.Error is not null)
            {
                logger.LogWarning("Arctic Shift submission search returned an error body: {Error}", listing?.Error);
                return null;
            }

            return listing.Data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Arctic Shift submission search exception");
            return null;
        }
    }

    // The tree is Reddit-listing shaped: nodes of kind "t1" (a comment, with nested "replies") and
    // "more" (a collapsed stub with no body). Only comments are kept; parent_id preserves the structure.
    private static void Flatten(IEnumerable<ArcticShiftCommentNodeDto> nodes, List<PullPushCommentDto> output)
    {
        foreach (ArcticShiftCommentNodeDto node in nodes)
        {
            if (node.Kind != "t1" || node.Data is null)
            {
                continue;
            }

            ArcticShiftCommentDataDto data = node.Data;
            output.Add(new PullPushCommentDto(data.Id, data.Body, data.Score, data.Author, data.CreatedUtc,
                data.ParentId));

            // "replies" is a listing object when present and an empty string when there are none.
            if (data.Replies.ValueKind == JsonValueKind.Object &&
                data.Replies.TryGetProperty("data", out JsonElement listing) &&
                listing.TryGetProperty("children", out JsonElement children) &&
                children.ValueKind == JsonValueKind.Array)
            {
                ArcticShiftCommentNodeDto[]? replies = children.Deserialize(
                    RedditJsonContext.Default.ArcticShiftCommentNodeDtoArray);
                if (replies is not null)
                {
                    Flatten(replies, output);
                }
            }
        }
    }
}

// One page of submissions: Posts is null when the request failed.
internal readonly record struct ListingPage(PullPushSubmissionDto[]? Posts, bool Full)
{
    public static ListingPage From(PullPushSubmissionDto[]? posts) =>
        new(posts, posts is { Length: >= ArcticShiftSource.PageSize });

    // created_utc of the newest post on an ascending page: where the next page starts.
    public long? LastCreated => Posts is { Length: > 0 } posts ? posts[^1].CreatedUtc : null;
}

internal sealed record ArcticShiftSubredditsResponseDto(
    [property: JsonPropertyName("data")] ArcticShiftSubredditDto[]? Data,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record ArcticShiftSubredditDto(
    [property: JsonPropertyName("display_name")]
    string? DisplayName,
    [property: JsonPropertyName("subscribers")]
    long? Subscribers,
    [property: JsonPropertyName("public_description")]
    string? PublicDescription);

internal sealed record ArcticShiftSubmissionsResponseDto(
    [property: JsonPropertyName("data")] PullPushSubmissionDto[]? Data,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record ArcticShiftCommentTreeResponseDto(
    [property: JsonPropertyName("data")] ArcticShiftCommentNodeDto[]? Data,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record ArcticShiftCommentNodeDto(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("data")] ArcticShiftCommentDataDto? Data);

internal sealed record ArcticShiftCommentDataDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("created_utc")]
    [property: JsonConverter(typeof(UnixTimestampConverter))]
    long? CreatedUtc,
    [property: JsonPropertyName("parent_id")]
    string? ParentId,
    [property: JsonPropertyName("replies")]
    JsonElement Replies = default);
