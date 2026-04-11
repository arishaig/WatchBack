using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace WatchBack.Infrastructure.Http;

/// <summary>
///     Pipeline handler applied to every typed HttpClient in the application.
///     • 429 Too Many Requests: records the Retry-After window in IMemoryCache keyed by
///     host. Subsequent requests to that host short-circuit immediately (no network call)
///     and return a synthetic 429 until the window expires. Capped at 5 minutes to guard
///     against a misbehaving server pinning us indefinitely.
///     • 5xx transient responses and network errors: up to 3 retries with exponential
///     backoff (~1 s → ~2 s → ~4 s) and ±25 % jitter, capped at 30 s.
/// </summary>
public sealed class ResilientHttpHandler(
    IMemoryCache cache,
    ILogger<ResilientHttpHandler> logger) : DelegatingHandler
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan s_maxBackoffDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_maxRateLimitWindow = TimeSpan.FromMinutes(5);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string host = request.RequestUri?.Host ?? string.Empty;
        string rateLimitKey = $"ratelimit:{host}";

        // Short-circuit during an active rate-limit window — no network call made.
        if (cache.TryGetValue(rateLimitKey, out _))
        {
            logger.LogDebug("Request to {Host} skipped — rate-limit backoff active.", host);
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
        }

        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    TimeSpan window = RateLimitWindow(response);
                    logger.LogWarning(
                        "Rate limited (429) by {Host} ({Uri}). Suppressing requests for {Seconds} s.",
                        host, RedactSensitiveParams(request.RequestUri), (int)window.TotalSeconds);
                    cache.Set(rateLimitKey, true, window);
                    return response;
                }

                if (IsTransientStatus(response.StatusCode) && attempt < MaxRetries && IsIdempotent(request.Method))
                {
                    TimeSpan delay = ExponentialDelay(attempt);
                    logger.LogWarning(
                        "HTTP {Status} from {Host} ({Uri}) (attempt {Attempt}/{Max}). Retrying in {Ms} ms.",
                        (int)response.StatusCode, host, RedactSensitiveParams(request.RequestUri), attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    request = CloneRequest(request);
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (IsTransientException(ex, cancellationToken) && attempt < MaxRetries &&
                                       IsIdempotent(request.Method))
            {
                response?.Dispose();
                TimeSpan delay = ExponentialDelay(attempt);
                logger.LogWarning(ex,
                    "Transient error from {Host} ({Uri}) (attempt {Attempt}/{Max}). Retrying in {Ms} ms.",
                    host, RedactSensitiveParams(request.RequestUri), attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
                request = CloneRequest(request);
            }
        }
    }

    private static TimeSpan RateLimitWindow(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        TimeSpan window = retryAfter?.Delta
                          ?? (retryAfter?.Date is { } date
                              ? date - DateTimeOffset.UtcNow
                              : TimeSpan.FromSeconds(60));

        if (window <= TimeSpan.Zero)
        {
            window = TimeSpan.FromSeconds(60);
        }

        return window > s_maxRateLimitWindow ? s_maxRateLimitWindow : window;
    }

    private static bool IsTransientStatus(HttpStatusCode status)
    {
        return status is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static bool IsTransientException(Exception ex, CancellationToken ct)
    {
        // Timeouts (HttpClient.Timeout elapsed → inner TimeoutException) are not retried:
        // they indicate a misconfigured or unreachable host, not a transient blip.
        // SSL/certificate errors are also not transient — retrying adds latency for no gain.
        return ex.InnerException is not TimeoutException and not AuthenticationException
               && (ex is HttpRequestException
                   || (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested));
    }

    /// <summary>~1 s → ~2 s → ~4 s with ±25 % jitter, capped at s_maxBackoffDelay.</summary>
    private static TimeSpan ExponentialDelay(int attempt)
    {
        double baseMs = Math.Pow(2, attempt) * 1000;
        double jitter = (Random.Shared.NextDouble() - 0.5) * 0.5 * baseMs;
        return TimeSpan.FromMilliseconds(Math.Min(baseMs + jitter, s_maxBackoffDelay.TotalMilliseconds));
    }

    /// <summary>Only idempotent methods are safe to retry — POST/PATCH bodies cannot be reliably cloned.</summary>
    private static bool IsIdempotent(HttpMethod method)
    {
        return method == HttpMethod.Get || method == HttpMethod.Head ||
               method == HttpMethod.Options || method == HttpMethod.Delete;
    }

    /// <summary>Clones a request for retry — HttpRequestMessage is single-use after SendAsync.</summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        // Content bodies cannot be cloned: the underlying stream is consumed by the first send.
        // This is safe because IsIdempotent only permits GET, HEAD, OPTIONS, and DELETE — the
        // last of which may technically carry a body, but none of the application's providers do.
        System.Diagnostics.Debug.Assert(
            original.Content == null,
            "CloneRequest: request body was present but is not preserved across retries.");

        HttpRequestMessage clone = new(original.Method, original.RequestUri);
        foreach ((string key, IEnumerable<string> values) in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(key, values);
        }

        return clone;
    }

    /// <summary>
    ///     Returns a log-safe URI string with the values of sensitive query parameters
    ///     (apikey, api_key, key, token, secret) replaced by <c>***</c>.
    /// </summary>
    internal static string RedactSensitiveParams(Uri? uri)
    {
        if (uri == null)
        {
            return "(no uri)";
        }

        string query = uri.Query;
        if (string.IsNullOrEmpty(query))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }

        System.Text.StringBuilder sb = new(uri.GetLeftPart(UriPartial.Path));
        sb.Append('?');

        bool first = true;
        foreach (string part in query.TrimStart('?').Split('&'))
        {
            int eq = part.IndexOf('=');
            string paramName = eq >= 0 ? part[..eq] : part;
            string paramValue = eq >= 0 ? part[(eq + 1)..] : string.Empty;

            if (!first)
            {
                sb.Append('&');
            }

            first = false;

            if (IsSensitiveParam(paramName))
            {
                sb.Append(paramName).Append("=***");
            }
            else
            {
                sb.Append(part);
            }
        }

        return sb.ToString();
    }

    private static bool IsSensitiveParam(string name)
    {
        return name.Equals("apikey", StringComparison.OrdinalIgnoreCase)
               || name.Equals("api_key", StringComparison.OrdinalIgnoreCase)
               || name.Equals("key", StringComparison.OrdinalIgnoreCase)
               || name.Equals("token", StringComparison.OrdinalIgnoreCase)
               || name.Equals("secret", StringComparison.OrdinalIgnoreCase);
    }
}
