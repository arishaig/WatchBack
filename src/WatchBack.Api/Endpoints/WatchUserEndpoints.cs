using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Api.Endpoints;

public static class WatchUserEndpoints
{
    public static void MapWatchUserEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/watchstate")
            .WithTags("WatchState");

        group.MapGet("/users", GetUsers)
            .WithName("GetWatchUsers")
            .WithSummary("List users whose playback can be followed")
            .WithDescription(
                "Returns the users of the configured watch state provider (e.g. Jellyfin accounts). Empty when the provider cannot distinguish users, in which case the UI does not prompt for a choice.")
            .Produces<IReadOnlyList<WatchUser>>();
    }

    private static async Task<IReadOnlyList<WatchUser>> GetUsers(
        [FromServices] IEnumerable<IWatchStateProvider> watchStateProviders,
        IOptionsSnapshot<WatchBackOptions> options,
        CancellationToken ct)
    {
        // Same selection rule as SyncService: the configured provider, else the first registered.
        IWatchStateProvider? provider = watchStateProviders
                                            .FirstOrDefault(p => p.Metadata.Name.Equals(options.Value.WatchProvider,
                                                StringComparison.OrdinalIgnoreCase))
                                        ?? watchStateProviders.FirstOrDefault();

        return provider is IWatchUserDirectory directory && provider.IsConfigured
            ? await directory.GetUsersAsync(ct)
            : [];
    }
}
