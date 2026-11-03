using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;

using Polly;

namespace WatchBack.Infrastructure.Http;

/// <summary>
///     Polly resilience strategy that honours server-imposed rate-limit windows.
///     When a host returns 429 Too Many Requests (or 503 with Retry-After), the window the
///     server asked for is recorded in <see cref="HostBackoffRegistry" />. Requests to that host
///     then short-circuit with a synthetic 429 (no network call) until the window ends,
///     so we never call back sooner than the server asked.
/// </summary>
internal sealed class RateLimitSuppressionStrategy(
    HostBackoffRegistry registry,
    ILogger<RateLimitSuppressionStrategy> logger) : ResilienceStrategy<HttpResponseMessage>
{
    // Used when the server sends 429 without saying how long to wait.
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    // Sanity bound against a garbled header pinning a host for months; far longer than any real limit.
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromHours(24);

    // X-RateLimit-Reset values above this are Unix timestamps rather than seconds-until-reset.
    private const double EpochThreshold = 1_000_000_000;

    protected override async ValueTask<Outcome<HttpResponseMessage>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<HttpResponseMessage>>> callback,
        ResilienceContext context,
        TState state)
    {
        HttpRequestMessage? request = context.GetRequestMessage();
        string host = request?.RequestUri?.Host ?? string.Empty;

        // Without a host we have nothing to key the window by — pass through.
        if (string.IsNullOrEmpty(host))
        {
            return await callback(context, state);
        }

        // Short-circuit during an active rate-limit window — no network call made.
        if (registry.IsSuppressed(host, out DateTimeOffset until))
        {
            logger.LogDebug("Request to {Host} skipped — rate-limit backoff active until {Until:O}.", host, until);
            return Outcome.FromResult(
                new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request });
        }

        Outcome<HttpResponseMessage> outcome = await callback(context, state);

        if (outcome.Result is { } response &&
            (response.StatusCode == HttpStatusCode.TooManyRequests ||
             (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null)))
        {
            DateTimeOffset now = registry.Time.GetUtcNow();
            TimeSpan window = RateLimitWindow(response, now);
            logger.LogWarning(
                "Rate limited ({Status}) by {Host} ({Uri}). Suppressing requests for {Seconds} s.",
                (int)response.StatusCode, host, HttpLogging.RedactSensitiveParams(request?.RequestUri),
                (int)window.TotalSeconds);
            registry.Suppress(host, now + window);
        }

        return outcome;
    }

    internal static TimeSpan RateLimitWindow(HttpResponseMessage response, DateTimeOffset now)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        TimeSpan window = retryAfter?.Delta
                          ?? (retryAfter?.Date is { } date
                              ? date - now
                              : ResetHeaderWindow(response, now) ?? DefaultWindow);

        if (window <= TimeSpan.Zero)
        {
            window = DefaultWindow;
        }

        return window > MaxWindow ? MaxWindow : window;
    }

    // Some APIs (Arctic Shift, Reddit) send X-RateLimit-Reset instead of Retry-After. Most send
    // seconds-until-reset; others (e.g. GitHub-style) send a Unix timestamp. Tell them apart by size.
    private static TimeSpan? ResetHeaderWindow(HttpResponseMessage response, DateTimeOffset now)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? values) ||
            !double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out double value))
        {
            return null;
        }

        return value > EpochThreshold
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000)) - now
            : TimeSpan.FromSeconds(value);
    }
}

/// <summary>
///     Extension method to add <see cref="RateLimitSuppressionStrategy" /> to a resilience pipeline.
/// </summary>
internal static class RateLimitSuppressionExtensions
{
    internal static ResiliencePipelineBuilder<HttpResponseMessage> AddRateLimitSuppression(
        this ResiliencePipelineBuilder<HttpResponseMessage> builder,
        HostBackoffRegistry registry,
        ILogger<RateLimitSuppressionStrategy> logger) =>
        builder.AddStrategy(_ => new RateLimitSuppressionStrategy(registry, logger));
}
