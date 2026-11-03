using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace WatchBack.Infrastructure.ThoughtProviders;

// PullPush (https://pullpush.io) client helpers. PullPush stopped archiving around May 2025 and is often
// down, so it is only a fallback for older episodes. Every method returns null on failure.
internal static class PullPushSource
{
    private const string BaseUrl = "https://api.pullpush.io";

    public const string HealthUrl = BaseUrl + "/reddit/search/comment/?size=0";

    // One page of a subreddit's posts in a time window, oldest first: the same shape of search as
    // ArcticShiftSource.ListWindowAsync, matched locally by the caller.
    public static async Task<ListingPage> ListWindowAsync(
        HttpClient httpClient, string subreddit, long after, long before, ILogger logger, CancellationToken ct)
    {
        string url =
            $"{BaseUrl}/reddit/search/submission/?subreddit={Uri.EscapeDataString(subreddit)}&after={after}&before={before}&size={ArcticShiftSource.PageSize}&sort=asc&sort_type=created_utc";
        try
        {
            HttpResponseMessage response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PullPush submission search failed: HTTP {Status} for r/{Subreddit}",
                    (int)response.StatusCode, subreddit);
                return new ListingPage(null, false);
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            PullPushSubmissionsResponseDto? listing = JsonSerializer.Deserialize(
                content, RedditJsonContext.Default.PullPushSubmissionsResponseDto);
            return ListingPage.From(listing?.Data ?? []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "PullPush submission search exception for r/{Subreddit}", subreddit);
            return new ListingPage(null, false);
        }
    }

    public static async Task<PullPushCommentDto[]?> GetCommentsAsync(
        HttpClient httpClient, string submissionId, int limit, ILogger logger, CancellationToken ct)
    {
        string url =
            $"{BaseUrl}/reddit/search/comment/?link_id={Uri.EscapeDataString(submissionId)}&size={limit}&sort_type=score&sort=desc";
        try
        {
            HttpResponseMessage response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PullPush comment fetch failed: HTTP {Status} for thread {ThreadId}",
                    (int)response.StatusCode, submissionId);
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            PullPushCommentsResponseDto? comments = JsonSerializer.Deserialize(
                content, RedditJsonContext.Default.PullPushCommentsResponseDto);
            return comments?.Data ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "PullPush comment fetch exception for thread {ThreadId}", submissionId);
            return null;
        }
    }
}
