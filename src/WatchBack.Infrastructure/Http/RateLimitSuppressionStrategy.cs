using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Polly;

namespace WatchBack.Infrastructure.Http;

/// <summary>
///     Polly resilience strategy that tracks server-imposed rate-limit windows.
///     When a provider returns 429 Too Many Requests, the Retry-After window is cached
///     keyed by host. Subsequent requests to that host short-circuit immediately
///     (no network call) and return a synthetic 429 until the window expires.
///     Capped at 5 minutes to guard against a misbehaving server pinning us indefinitely.
/// </summary>
internal sealed class RateLimitSuppressionStrategy(
    IMemoryCache cache,
    ILogger<RateLimitSuppressionStrategy> logger) : ResilienceStrategy<HttpResponseMessage>
{
    private static readonly TimeSpan s_maxWindow = TimeSpan.FromMinutes(5);

    protected override async ValueTask<Outcome<HttpResponseMessage>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<HttpResponseMessage>>> callback,
        ResilienceContext context,
        TState state)
    {
        HttpRequestMessage? request = context.GetRequestMessage();
        string host = request?.RequestUri?.Host ?? string.Empty;
        string cacheKey = $"ratelimit:{host}";

        // Short-circuit during an active rate-limit window — no network call made.
        if (!string.IsNullOrEmpty(host) && cache.TryGetValue(cacheKey, out _))
        {
            logger.LogDebug("Request to {Host} skipped — rate-limit backoff active.", host);
            return Outcome.FromResult(
                new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request });
        }

        Outcome<HttpResponseMessage> outcome = await callback(context, state);

        if (outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TimeSpan window = RateLimitWindow(outcome.Result);
            logger.LogWarning(
                "Rate limited (429) by {Host} ({Uri}). Suppressing requests for {Seconds} s.",
                host, HttpLogging.RedactSensitiveParams(request?.RequestUri), (int)window.TotalSeconds);
            cache.Set(cacheKey, true, window);
        }

        return outcome;
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

        return window > s_maxWindow ? s_maxWindow : window;
    }
}

/// <summary>
///     Extension method to add <see cref="RateLimitSuppressionStrategy" /> to a resilience pipeline.
/// </summary>
internal static class RateLimitSuppressionExtensions
{
    internal static ResiliencePipelineBuilder<HttpResponseMessage> AddRateLimitSuppression(
        this ResiliencePipelineBuilder<HttpResponseMessage> builder,
        IMemoryCache cache,
        ILogger<RateLimitSuppressionStrategy> logger) =>
        builder.AddStrategy(_ => new RateLimitSuppressionStrategy(cache, logger));
}
