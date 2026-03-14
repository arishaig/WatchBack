using Microsoft.Extensions.Configuration;
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
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Add HTTP clients for each provider
        services.AddHttpClient("jellyfin");
        services.AddHttpClient("trakt")
            .ConfigureHttpClient(c => c.DefaultRequestHeaders.Add("trakt-api-version", "2"));
        services.AddHttpClient("bluesky");
        services.AddHttpClient("pullpush");

        // Register watch state providers based on configuration
        var watchProvider = configuration.GetValue<string>("WatchBack:WatchProvider") ?? "jellyfin";

        if (watchProvider.Equals("jellyfin", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IWatchStateProvider, JellyfinWatchStateProvider>();
        }
        else if (watchProvider.Equals("trakt", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IWatchStateProvider, TraktWatchStateProvider>();
        }
        else
        {
            services.AddScoped<IWatchStateProvider, JellyfinWatchStateProvider>();
        }

        // Register thought providers
        services.AddScoped<IThoughtProvider, TraktThoughtProvider>();
        services.AddScoped<IThoughtProvider, RedditThoughtProvider>();
        services.AddScoped<IThoughtProvider, BlueskyThoughtProvider>();

        // Register configuration repository
        services.AddScoped<IConfigurationRepository, ConfigurationRepository>();

        return services;
    }
}
