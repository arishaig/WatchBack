using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Polly;
using Polly.Retry;
using Polly.Timeout;

using WatchBack.Core.Interfaces;
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
        services.AddWatchBackClient<JellyfinWatchStateProvider>();
        services.AddWatchBackClient<TraktWatchStateProvider>();
        services.AddWatchBackClient<TraktThoughtProvider>();
        services.AddWatchBackClient<RedditThoughtProvider>(TimeSpan.FromSeconds(30));
        services.AddWatchBackClient<BlueskyThoughtProvider>();
        services.AddWatchBackClient<LemmyThoughtProvider>();

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

    private static void AddWatchBackClient<T>(this IServiceCollection services, TimeSpan? timeout = null)
        where T : class
    {
        services.AddHttpClient<T>()
            .AddResilienceHandler("watchback", (p, ctx) =>
                ConfigureResiliencePipeline(p, ctx, timeout ?? TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    ///     Configures the standard WatchBack Polly resilience pipeline:
    ///     <list type="number">
    ///         <item>Rate-limit suppression — short-circuits all requests to a host during a 429 Retry-After window</item>
    ///         <item>Retry — up to 3 times with exponential backoff + jitter on 5xx and transient errors (GET/HEAD/OPTIONS only)</item>
    ///         <item>Timeout — per-attempt deadline</item>
    ///     </list>
    /// </summary>
    private static void ConfigureResiliencePipeline(
        ResiliencePipelineBuilder<HttpResponseMessage> pipeline,
        ResilienceHandlerContext ctx,
        TimeSpan attemptTimeout)
    {
        IServiceProvider sp = ctx.ServiceProvider;
        IMemoryCache cache = sp.GetRequiredService<IMemoryCache>();
        ILogger<RateLimitSuppressionStrategy> logger =
            sp.GetRequiredService<ILogger<RateLimitSuppressionStrategy>>();

        HttpRetryStrategyOptions retryOptions = new()
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            // Handle 5xx and transient network errors only — 429 is owned by RateLimitSuppressionStrategy
            ShouldHandle = args => ValueTask.FromResult(
                args.Outcome.Exception is HttpRequestException or TimeoutRejectedException ||
                args.Outcome.Result?.StatusCode is HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                    or HttpStatusCode.RequestTimeout)
        };
        // POST/PATCH/PUT/DELETE/CONNECT are not idempotent and must not be retried
        retryOptions.DisableForUnsafeHttpMethods();

        pipeline
            .AddRateLimitSuppression(cache, logger)  // outermost — short-circuits 429 before retry sees it
            .AddRetry(retryOptions)
            .AddTimeout(attemptTimeout);
    }
}
