using System.Net;
using System.Threading.RateLimiting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Polly;
using Polly.Retry;
using Polly.Timeout;

using WatchBack.Core.Interfaces;
using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.Http;
using WatchBack.Infrastructure.Omdb;
using WatchBack.Infrastructure.ThoughtProviders;
using WatchBack.Infrastructure.WatchStateProviders;

using Microsoft.Extensions.Http.Resilience;

namespace WatchBack.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWatchBackInfrastructure(
        this IServiceCollection services)
    {
        // DelegatingHandlers must be transient — they cannot be shared across HttpClients.
        services.AddTransient<RequestLoggingHandler>();
        services.AddTransient<UserAgentHandler>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<HostBackoffRegistry>();
        services.AddSingleton<ResultCacheStore>();
        services.AddSingleton<IDurableLookupCache, DurableLookupCache>();
        services.AddSingleton<ISubredditResolver, ArcticShiftSubredditResolver>();
        services.AddSingleton<IMediaResultCache, MediaResultCache>();
        services.AddHostedService<ResultCacheMaintenance>();

        // Jellyfin is the user's own server, polled for watch state: no breaker, so playback is
        // picked up again as soon as it comes back. PullPush, Arctic Shift and Lemmy instances are
        // volunteer-run, so they are also throttled per host.
        services.AddWatchBackClient<JellyfinWatchStateProvider>(policy: ClientPolicy.None);
        services.AddWatchBackClient<TraktWatchStateProvider>();
        services.AddWatchBackClient<TraktThoughtProvider>();
        services.AddWatchBackClient<RedditThoughtProvider>(TimeSpan.FromSeconds(30), ClientPolicy.Volunteer);
        services.AddWatchBackClient<BlueskyThoughtProvider>();
        services.AddWatchBackClient<LemmyThoughtProvider>(policy: ClientPolicy.Volunteer);

        // ManualWatchStateProvider is a singleton so it can hold state across requests.
        // Registered as both IManualWatchStateProvider (for SyncService priority check)
        // and IWatchStateProvider (for health checks / provider enumeration).
        services.AddSingleton<ManualWatchStateProvider>();
        services.AddSingleton<IManualWatchStateProvider>(sp => sp.GetRequiredService<ManualWatchStateProvider>());

        // Register all watch state providers — active one is selected at runtime from config
        services.AddScoped<IWatchStateProvider>(sp => sp.GetRequiredService<JellyfinWatchStateProvider>());
        services.AddScoped<IWatchStateProvider>(sp => sp.GetRequiredService<TraktWatchStateProvider>());
        services.AddScoped<IWatchStateProvider>(sp => sp.GetRequiredService<ManualWatchStateProvider>());

        // Register thought providers
        services.AddScoped<IThoughtProvider>(sp => sp.GetRequiredService<TraktThoughtProvider>());
        services.AddScoped<IThoughtProvider>(sp => sp.GetRequiredService<RedditThoughtProvider>());
        services.AddScoped<IThoughtProvider>(sp => sp.GetRequiredService<BlueskyThoughtProvider>());
        services.AddScoped<IThoughtProvider>(sp => sp.GetRequiredService<LemmyThoughtProvider>());

        // Register media search and ratings providers — OMDb implements both interfaces.
        // AddHttpClient registers the typed client as transient; the scoped forwarding
        // registrations ensure the HttpClient handler pipeline rotates correctly.
        services.AddWatchBackClient<OmdbMediaSearchProvider>();
        services.AddScoped<IMediaSearchProvider>(sp => sp.GetRequiredService<OmdbMediaSearchProvider>());
        services.AddScoped<IRatingsProvider>(sp => sp.GetRequiredService<OmdbMediaSearchProvider>());

        return services;
    }

    /// <summary>How much protection a client's upstream gets beyond 429 handling and retries.</summary>
    internal enum ClientPolicy
    {
        /// <summary>Rate-limit suppression, retry and timeout only.</summary>
        None,

        /// <summary>Adds a per-host circuit breaker.</summary>
        Breaker,

        /// <summary>Adds the breaker plus per-host concurrency of 1 and about 1 request/second.</summary>
        Volunteer
    }

    // Throttled requests queue rather than fail; a lookup can therefore take a while, so the
    // overall HttpClient deadline (default 100 s, and it includes queueing) is raised for them.
    private static readonly TimeSpan s_volunteerClientTimeout = TimeSpan.FromMinutes(5);

    private static void AddWatchBackClient<T>(this IServiceCollection services, TimeSpan? timeout = null,
        ClientPolicy policy = ClientPolicy.Breaker)
        where T : class
    {
        // RequestLoggingHandler is registered FIRST so it sits outside the resilience pipeline
        // — each log entry then represents the end-to-end wall time including retries, waits,
        // and short-circuits. Individual retry/timeout events log separately from inside the pipeline.
        // SelectPipelineByAuthority gives each host its own pipeline instance, so breaker and throttle
        // state are per host: a client that talks to two hosts (Reddit: PullPush and Arctic Shift)
        // doesn't let one host's outage block the other.
        services.AddHttpClient<T>(client =>
            {
                if (policy == ClientPolicy.Volunteer)
                {
                    client.Timeout = s_volunteerClientTimeout;
                }
            })
            .AddHttpMessageHandler<UserAgentHandler>()
            .AddHttpMessageHandler<RequestLoggingHandler>()
            .AddResilienceHandler("watchback", (p, ctx) =>
                ConfigureResiliencePipeline(p, ctx, timeout ?? TimeSpan.FromSeconds(10), policy))
            .SelectPipelineByAuthority();
    }

    /// <summary>
    ///     Configures the standard WatchBack Polly resilience pipeline:
    ///     <list type="number">
    ///         <item>
    ///             Throttle (<see cref="ClientPolicy.Volunteer" /> only) — one request in flight and ~1 request/second
    ///             per host, queued. Outermost, so a queued request checks the backoff window and breaker only
    ///             once it's its turn: a 429 or an outage seen meanwhile stops the whole queue.
    ///         </item>
    ///         <item>Rate-limit suppression — short-circuits all requests to a host during a 429 Retry-After window</item>
    ///         <item>Circuit breaker (unless <see cref="ClientPolicy.None" />) — stops calling a host that keeps failing; open = synthetic 503</item>
    ///         <item>Retry — up to 3 times with exponential backoff + jitter on 5xx and transient errors (GET/HEAD/OPTIONS only), holding the throttle slot</item>
    ///         <item>Timeout — per-attempt deadline, so time spent queued in the throttle doesn't count</item>
    ///     </list>
    /// </summary>
    private static void ConfigureResiliencePipeline(
        ResiliencePipelineBuilder<HttpResponseMessage> pipeline,
        ResilienceHandlerContext ctx,
        TimeSpan attemptTimeout,
        ClientPolicy policy)
    {
        IServiceProvider sp = ctx.ServiceProvider;
        HostBackoffRegistry backoff = sp.GetRequiredService<HostBackoffRegistry>();
        ILogger<RateLimitSuppressionStrategy> rateLimitLogger =
            sp.GetRequiredService<ILogger<RateLimitSuppressionStrategy>>();
        // Shared category for retry + timeout diagnostics — sits under the same
        // "WatchBack.Infrastructure.Http" prefix as the rest of the HTTP stack so
        // a single appsettings entry toggles all request-pipeline logging.
        ILogger resilienceLogger = sp.GetRequiredService<ILoggerFactory>()
            .CreateLogger("WatchBack.Infrastructure.Http.Resilience");

        HttpRetryStrategyOptions retryOptions = new()
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            // Handle 5xx and transient network errors only — 429, and 503 with Retry-After, are owned by
            // RateLimitSuppressionStrategy: retrying here would either call back before the server asked
            // or sit out a long Retry-After inside the request.
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is HttpRequestException or TimeoutRejectedException ||
                args.Outcome.Result is { } response &&
                (response.StatusCode is HttpStatusCode.InternalServerError
                     or HttpStatusCode.BadGateway
                     or HttpStatusCode.GatewayTimeout
                     or HttpStatusCode.RequestTimeout ||
                 (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is null))),
            OnRetry = args =>
            {
                if (!resilienceLogger.IsEnabled(LogLevel.Debug))
                {
                    return default;
                }

                string uri = HttpLogging.RedactSensitiveParams(
                    args.Outcome.Result?.RequestMessage?.RequestUri);
                string reason = args.Outcome.Exception?.GetType().Name
                                ?? ((int?)args.Outcome.Result?.StatusCode)?.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture)
                                ?? "unknown";
                resilienceLogger.LogDebug(
                    "Retry {Attempt} in {Delay}ms for {Uri} ({Reason})",
                    args.AttemptNumber + 1, (int)args.RetryDelay.TotalMilliseconds, uri, reason);
                return default;
            }
        };
        // POST/PATCH/PUT/DELETE/CONNECT are not idempotent and must not be retried
        retryOptions.DisableForUnsafeHttpMethods();

        TimeoutStrategyOptions timeoutOptions = new()
        {
            Timeout = attemptTimeout,
            OnTimeout = args =>
            {
                if (!resilienceLogger.IsEnabled(LogLevel.Debug))
                {
                    return default;
                }

                string uri = HttpLogging.RedactSensitiveParams(
                    args.Context.GetRequestMessage()?.RequestUri);
                resilienceLogger.LogDebug(
                    "Timeout after {Seconds}s for {Uri}",
                    (int)args.Timeout.TotalSeconds, uri);
                return default;
            }
        };

        if (policy == ClientPolicy.Volunteer)
        {
            pipeline.AddRateLimiter(BuildVolunteerThrottle());
        }

        pipeline.AddRateLimitSuppression(backoff, rateLimitLogger); // short-circuits 429 before retry sees it

        if (policy != ClientPolicy.None)
        {
            pipeline
                .AddStrategy(_ => new BrokenCircuitResponseStrategy())
                .AddCircuitBreaker(BuildCircuitBreakerOptions(resilienceLogger));
        }

        pipeline
            .AddRetry(retryOptions)
            .AddTimeout(timeoutOptions);
    }

    // Counts the final outcome of each request (after retries). Opens once half of at least
    // 4 requests in five minutes failed, and stays open longer each time the half-open probe fails.
    // The window is long because a struggling host fails slowly: PullPush behind Cloudflare answers
    // 522 after ~20 s, and throttled to one request at a time that's only three failures a minute.
    internal static HttpCircuitBreakerStrategyOptions BuildCircuitBreakerOptions(ILogger logger) => new()
    {
        FailureRatio = 0.5,
        MinimumThroughput = 4,
        SamplingDuration = TimeSpan.FromMinutes(5),
        BreakDurationGenerator = args => ValueTask.FromResult(BreakDuration(args.HalfOpenAttempts)),
        // 429 is owned by RateLimitSuppressionStrategy; only outages count here: any 5xx (including
        // Cloudflare's 520–524), 408, timeouts and transport errors.
        ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is HttpRequestException or TimeoutRejectedException ||
            args.Outcome.Result is { } response &&
            ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)),
        OnOpened = args =>
        {
            logger.LogWarning("Circuit opened for {Uri} for {Seconds} s after repeated failures",
                HttpLogging.RedactSensitiveParams(args.Outcome.Result?.RequestMessage?.RequestUri),
                (int)args.BreakDuration.TotalSeconds);
            return default;
        }
    };

    // 30 s, 1 min, 2 min … capped at 30 min.
    internal static TimeSpan BreakDuration(int halfOpenAttempts) =>
        TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Min(halfOpenAttempts, 10)), 1800));

    // One request in flight per host, and at most about one started per second. Requests queue
    // (they are never rejected) so a lookup slows down instead of failing.
    internal static RateLimiter BuildVolunteerThrottle() => RateLimiter.CreateChained(
        new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }),
        new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        }));
}
