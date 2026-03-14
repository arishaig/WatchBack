using Microsoft.Extensions.DependencyInjection;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Persistence;
using WatchBack.Infrastructure.WatchState;
using WatchBack.Infrastructure.Thoughts;

namespace WatchBack.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWatchBackInfrastructure(
        this IServiceCollection services)
    {
        // Register typed HTTP clients for each provider
        services.AddHttpClient<JellyfinWatchStateProvider>();
        services.AddHttpClient<TraktWatchStateProvider>();
        services.AddHttpClient<TraktThoughtProvider>();
        services.AddHttpClient<RedditThoughtProvider>();
        services.AddHttpClient<BlueskyThoughtProvider>();

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
