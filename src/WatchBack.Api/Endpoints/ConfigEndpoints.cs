using Microsoft.Extensions.Options;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Options;

namespace WatchBack.Api.Endpoints;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api");

        group.MapGet("/config", GetConfig)
            .WithName("GetConfig");

        group.MapGet("/status", GetStatus)
            .WithName("GetStatus");

        group.MapGet("/test/{service}", TestService)
            .WithName("TestService");
    }

    private static object GetConfig(
        IOptions<JellyfinOptions> jellyfin,
        IOptions<TraktOptions> trakt,
        IOptions<BlueskyOptions> bluesky,
        IOptions<RedditOptions> reddit,
        IOptions<WatchBackOptions> watchback)
    {
        return new
        {
            integrations = new Dictionary<string, object>
            {
                ["jellyfin"] = new
                {
                    name = "Jellyfin",
                    fields = new[]
                    {
                        new { key = "Jellyfin__BaseUrl", label = "Server URL", type = "text", placeholder = "http://jellyfin:8096", hasValue = !string.IsNullOrEmpty(jellyfin.Value.BaseUrl) && jellyfin.Value.BaseUrl != "http://jellyfin:8096" },
                        new { key = "Jellyfin__ApiKey", label = "API Key", type = "password", placeholder = "Required", hasValue = !string.IsNullOrEmpty(jellyfin.Value.ApiKey) }
                    },
                    configured = !string.IsNullOrEmpty(jellyfin.Value.ApiKey)
                },
                ["trakt"] = new
                {
                    name = "Trakt.tv",
                    fields = new[]
                    {
                        new { key = "Trakt__ClientId", label = "Client ID", type = "text", placeholder = "Optional (for comments)", hasValue = !string.IsNullOrEmpty(trakt.Value.ClientId) },
                        new { key = "Trakt__AccessToken", label = "Access Token (OAuth)", type = "password", placeholder = "Optional (for private profile)", hasValue = !string.IsNullOrEmpty(trakt.Value.AccessToken) },
                        new { key = "Trakt__Username", label = "Username", type = "text", placeholder = "Optional (public profile)", hasValue = !string.IsNullOrEmpty(trakt.Value.Username) }
                    },
                    configured = !string.IsNullOrEmpty(trakt.Value.ClientId) || !string.IsNullOrEmpty(trakt.Value.Username)
                },
                ["bluesky"] = new
                {
                    name = "Bluesky",
                    fields = new[]
                    {
                        new { key = "Bluesky__Handle", label = "Handle/Email", type = "text", placeholder = "you.bsky.social", hasValue = !string.IsNullOrEmpty(bluesky.Value.Handle) },
                        new { key = "Bluesky__AppPassword", label = "App Password", type = "password", placeholder = "xxxx-xxxx-xxxx-xxxx", hasValue = !string.IsNullOrEmpty(bluesky.Value.AppPassword) }
                    },
                    configured = !string.IsNullOrEmpty(bluesky.Value.Handle) && !string.IsNullOrEmpty(bluesky.Value.AppPassword)
                }
            },
            preferences = new
            {
                timeMachineDays = watchback.Value.TimeMachineDays,
                watchProvider = watchback.Value.WatchProvider,
                redditMaxComments = reddit.Value.MaxComments
            }
        };
    }

    private static object GetStatus(
        IOptions<JellyfinOptions> jellyfin,
        IOptions<TraktOptions> trakt,
        IOptions<BlueskyOptions> bluesky,
        IOptions<WatchBackOptions> watchback)
    {
        return new
        {
            jellyfinConfigured = !string.IsNullOrEmpty(jellyfin.Value.ApiKey),
            traktConfigured = !string.IsNullOrEmpty(trakt.Value.ClientId) || !string.IsNullOrEmpty(trakt.Value.Username),
            blueskyConfigured = !string.IsNullOrEmpty(bluesky.Value.Handle) && !string.IsNullOrEmpty(bluesky.Value.AppPassword),
            watchProvider = watchback.Value.WatchProvider
        };
    }

    private static async Task<object> TestService(
        string service,
        IServiceProvider sp,
        CancellationToken ct)
    {
        try
        {
            switch (service.ToLowerInvariant())
            {
                case "jellyfin":
                {
                    var provider = sp.GetRequiredService<IWatchStateProvider>();
                    var health = await provider.GetServiceHealthAsync(ct);
                    return new { ok = health.IsHealthy, message = health.IsHealthy ? "Connected" : "Connection failed" };
                }
                case "trakt":
                {
                    var providers = sp.GetServices<IThoughtProvider>();
                    var trakt = providers.FirstOrDefault(p => p.Metadata.Name == "Trakt");
                    if (trakt == null) return new { ok = false, message = "Trakt provider not registered" };
                    var health = await trakt.GetServiceHealthAsync(ct);
                    return new { ok = health.IsHealthy, message = health.IsHealthy ? "Connected" : "Connection failed" };
                }
                case "bluesky":
                {
                    var providers = sp.GetServices<IThoughtProvider>();
                    var bsky = providers.FirstOrDefault(p => p.Metadata.Name == "Bluesky");
                    if (bsky == null) return new { ok = false, message = "Bluesky provider not registered" };
                    var health = await bsky.GetServiceHealthAsync(ct);
                    return new { ok = health.IsHealthy, message = health.IsHealthy ? "Connected" : "Connection failed" };
                }
                case "reddit":
                {
                    var providers = sp.GetServices<IThoughtProvider>();
                    var reddit = providers.FirstOrDefault(p => p.Metadata.Name == "Reddit");
                    if (reddit == null) return new { ok = false, message = "Reddit provider not registered" };
                    var health = await reddit.GetServiceHealthAsync(ct);
                    return new { ok = health.IsHealthy, message = health.IsHealthy ? "Connected" : "Connection failed" };
                }
                default:
                    return new { ok = false, message = $"Unknown service: {service}" };
            }
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }
}
