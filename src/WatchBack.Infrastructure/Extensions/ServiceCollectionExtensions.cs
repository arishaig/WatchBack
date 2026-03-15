using Microsoft.Extensions.DependencyInjection;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Http;
using WatchBack.Infrastructure.Persistence;
using WatchBack.Infrastructure.ThoughtProviders;
using WatchBack.Infrastructure.WatchStateProviders;

namespace WatchBack.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWatchBackInfrastructure(
        this IServiceCollection services)
    {
        services.AddTransient<ResilientHttpHandler>();

        // Register typed HTTP clients for each provider — all share the resilience handler.
        // 10-second timeout keeps sync fast when a provider is misconfigured or unreachable.
        static void ConfigureClient(HttpClient c) => c.Timeout = TimeSpan.FromSeconds(10);

        services.AddHttpClient<JellyfinWatchStateProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddHttpClient<TraktWatchStateProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddHttpClient<TraktThoughtProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddHttpClient<RedditThoughtProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();
        services.AddHttpClient<BlueskyThoughtProvider>().ConfigureHttpClient(ConfigureClient).AddHttpMessageHandler<ResilientHttpHandler>();

        // Register all watch state providers — active one is selected at runtime from config
        services.AddScoped<IWatchStateProvider>(sp => sp.GetRequiredService<JellyfinWatchStateProvider>());
        services.AddScoped<IWatchStateProvider>(sp => sp.GetRequiredService<TraktWatchStateProvider>());

        // Register thought providers
        services.AddScoped<IThoughtProvider>(sp => sp.GetRequiredService<TraktThoughtProvider>());
        services.AddScoped<IThoughtProvider>(sp => sp.GetRequiredService<RedditThoughtProvider>());
        services.AddScoped<IThoughtProvider>(sp => sp.GetRequiredService<BlueskyThoughtProvider>());

        // Register configuration repository
        services.AddScoped<IConfigurationRepository, ConfigurationRepository>();

        return services;
    }
}