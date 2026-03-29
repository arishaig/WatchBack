using Microsoft.Extensions.DependencyInjection;

using WatchBack.Core.Interfaces;
using WatchBack.Infrastructure.Http;
using WatchBack.Infrastructure.Omdb;
using WatchBack.Infrastructure.ThoughtProviders;
using WatchBack.Infrastructure.WatchStateProviders;

namespace WatchBack.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWatchBackInfrastructure(
        this IServiceCollection services)
    {
        services.AddTransient<ResilientHttpHandler>();

        services.AddHttpClient<JellyfinWatchStateProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddHttpClient<TraktWatchStateProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddHttpClient<TraktThoughtProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddHttpClient<RedditThoughtProvider>().ConfigureHttpClient(ConfigureRedditClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddHttpClient<BlueskyThoughtProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();

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

        // Register media search and ratings providers — OMDb implements both interfaces.
        // AddHttpClient registers the typed client as transient; the scoped forwarding
        // registrations ensure the HttpClient handler pipeline rotates correctly.
        services.AddHttpClient<OmdbMediaSearchProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddScoped<IMediaSearchProvider>(sp => sp.GetRequiredService<OmdbMediaSearchProvider>());
        services.AddScoped<IRatingsProvider>(sp => sp.GetRequiredService<OmdbMediaSearchProvider>());

        return services;

        // PullPush is a community API that can be slow under load — give it more headroom.
        static void ConfigureRedditClient(HttpClient c)
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        }

        // Register typed HTTP clients for each provider — all share the resilience handler.
        // 10-second timeout keeps sync fast when a provider is misconfigured or unreachable.
        static void ConfigureClient(HttpClient c)
        {
            c.Timeout = TimeSpan.FromSeconds(10);
        }
    }
}
