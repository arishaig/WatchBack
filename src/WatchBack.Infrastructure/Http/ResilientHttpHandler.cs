using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace WatchBack.Infrastructure.Http;

/// <summary>
/// Pipeline handler applied to every typed HttpClient in the application.
///
/// • 429 Too Many Requests: records the Retry-After window in IMemoryCache keyed by
///   host. Subsequent requests to that host short-circuit immediately (no network call)
///   and return a synthetic 429 until the window expires. Capped at 5 minutes to guard
///   against a misbehaving server pinning us indefinitely.
///
/// • 5xx transient responses and network errors: up to 3 retries with exponential
///   backoff (~1 s → ~2 s → ~4 s) and ±25 % jitter, capped at 30 s.
/// </summary>
public sealed class ResilientHttpHandler(
    IMemoryCache cache,
    ILogger<ResilientHttpHandler> logger) : DelegatingHandler
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRateLimitWindow = TimeSpan.FromMinutes(5);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? string.Empty;
        var rateLimitKey = $"ratelimit:{host}";

        // Short-circuit during an active rate-limit window — no network call made.
        if (cache.TryGetValue(rateLimitKey, out _))
        {
            logger.LogDebug("Request to {Host} skipped — rate-limit backoff active.", host);
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
        }

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var window = RateLimitWindow(response);
                    logger.LogWarning(
                        "Rate limited (429) by {Host}. Suppressing requests for {Seconds} s.",
                        host, (int)window.TotalSeconds);
                    cache.Set(rateLimitKey, true, window);
                    return response;
                }

                if (IsTransientStatus(response.StatusCode) && attempt < MaxRetries)
                {
                    var delay = ExponentialDelay(attempt);
                    logger.LogWarning(
                        "HTTP {Status} from {Host} (attempt {Attempt}/{Max}). Retrying in {Ms} ms.",
                        (int)response.StatusCode, host, attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    request = CloneRequest(request);
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (IsTransientException(ex, cancellationToken) && attempt < MaxRetries)
            {
                response?.Dispose();
                var delay = ExponentialDelay(attempt);
                logger.LogWarning(ex,
                    "Transient error from {Host} (attempt {Attempt}/{Max}). Retrying in {Ms} ms.",
                    host, attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
                request = CloneRequest(request);
            }
        }
    }

    private static TimeSpan RateLimitWindow(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var window = retryAfter?.Delta
            ?? (retryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(60));

        if (window <= TimeSpan.Zero) window = TimeSpan.FromSeconds(60);
        return window > MaxRateLimitWindow ? MaxRateLimitWindow : window;
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.InternalServerError
               or HttpStatusCode.BadGateway
               or HttpStatusCode.ServiceUnavailable
               or HttpStatusCode.GatewayTimeout;

    private static bool IsTransientException(Exception ex, CancellationToken ct) =>
        // Timeouts (HttpClient.Timeout elapsed → inner TimeoutException) are not retried:
        // they indicate a misconfigured or unreachable host, not a transient blip.
        ex.InnerException is not TimeoutException
        && (ex is HttpRequestException
            || (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested));

    /// <summary>~1 s → ~2 s → ~4 s with ±25 % jitter, capped at MaxBackoffDelay.</summary>
    private static TimeSpan ExponentialDelay(int attempt)
    {
        var baseMs = Math.Pow(2, attempt) * 1000;
        var jitter = (Random.Shared.NextDouble() - 0.5) * 0.5 * baseMs;
        return TimeSpan.FromMilliseconds(Math.Min(baseMs + jitter, MaxBackoffDelay.TotalMilliseconds));
    }

    /// <summary>Clones a request for retry — HttpRequestMessage is single-use after SendAsync.</summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var (key, values) in original.Headers)
            clone.Headers.TryAddWithoutValidation(key, values);
        return clone;
    }
}
