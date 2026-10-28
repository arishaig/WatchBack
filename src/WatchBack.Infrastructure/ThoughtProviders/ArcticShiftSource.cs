using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace WatchBack.Infrastructure.ThoughtProviders;

// Which Reddit archive(s) returned a submission. Flags because both can find the same thread.
[Flags]
internal enum RedditSource
{
    None = 0,
    PullPush = 1,
    ArcticShift = 2
}

// Arctic Shift (https://github.com/ArthurHeitmann/arctic_shift/tree/master/api) client helpers.
// Submissions reuse the PullPush DTOs — the field names are identical to Reddit's own.
// Every method returns null on failure so callers can tell "request failed" from "no results".
internal static class ArcticShiftSource
{
    private const string BaseUrl = "https://arctic-shift.photon-reddit.com";
    private const int PageSize = 100;
    private const int MaxPages = 3;

    // Arctic Shift rate limits are "calculated dynamically based on server load", and the heavy
    // queries can take seconds. Prefetch runs concurrently, so cap in-flight requests process-wide.
    private static readonly SemaphoreSlim s_gate = new(2, 2);

    // Time-window listing of a subreddit's posts, oldest first. Keyword (title=) search requires a
    // subreddit and times out on large ones, so the window is fetched and matched locally instead.
    // A 100-post page may not cover the window on a busy subreddit, so it pages forward.
    public static async Task<PullPushSubmissionDto[]?> SearchWindowAsync(
        HttpClient httpClient, string subreddit, DateTimeOffset after, DateTimeOffset before,
        ILogger logger, CancellationToken ct)
    {
        List<PullPushSubmissionDto> all = [];
        long cursor = after.ToUnixTimeSeconds();
        long end = before.ToUnixTimeSeconds();

        for (int page = 0; page < MaxPages; page++)
        {
            string url =
                $"{BaseUrl}/api/posts/search?subreddit={Uri.EscapeDataString(subreddit)}&after={cursor}&before={end}&limit={PageSize}&sort=asc";
            PullPushSubmissionDto[]? batch = await GetSubmissionsAsync(httpClient, url, logger, ct);
            if (batch is null)
            {
                // A later page failing still leaves earlier pages usable.
                return page == 0 ? null : [.. all];
            }

            all.AddRange(batch);
            if (batch.Length < PageSize || batch[^1].CreatedUtc is not { } last)
            {
                break;
            }

            cursor = last;
        }

        return [.. all];
    }

    // Keyword search within one subreddit. Used when the release date is unknown, so no window exists.
    public static Task<PullPushSubmissionDto[]?> SearchTitleAsync(
        HttpClient httpClient, string subreddit, string title, DateTimeOffset? after,
        ILogger logger, CancellationToken ct)
    {
        string url =
            $"{BaseUrl}/api/posts/search?subreddit={Uri.EscapeDataString(subreddit)}&title={Uri.EscapeDataString(title)}&limit={PageSize}&sort=desc";
        if (after.HasValue)
        {
            url += $"&after={after.Value.ToUnixTimeSeconds()}";
        }

        return GetSubmissionsAsync(httpClient, url, logger, ct);
    }

    // Full comment tree for one submission, flattened. Unlike /comments/search it isn't capped at 100.
    public static async Task<PullPushCommentDto[]?> GetCommentsAsync(
        HttpClient httpClient, string submissionId, int limit, ILogger logger, CancellationToken ct)
    {
        string url = $"{BaseUrl}/api/comments/tree?link_id=t3_{Uri.EscapeDataString(submissionId)}&limit={limit}";

        await s_gate.WaitAsync(ct);
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
        finally
        {
            s_gate.Release();
        }
    }

    private static async Task<PullPushSubmissionDto[]?> GetSubmissionsAsync(
        HttpClient httpClient, string url, ILogger logger, CancellationToken ct)
    {
        await s_gate.WaitAsync(ct);
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
        finally
        {
            s_gate.Release();
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
