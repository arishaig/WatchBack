using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using WatchBack.Api.Auth;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Api.Endpoints;

/// <summary>Who may change settings: the first watch user picked becomes the admin, and admins can add others.</summary>
public static class AccessEndpoints
{
    public static void MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/access")
            .WithTags("Access");

        group.MapGet("/", GetAccess)
            .WithName("GetAccess")
            .WithSummary("Whether the picked watch user may change settings")
            .WithDescription(
                "Reads the watch user from the wb_watchUser cookie. When no admins exist yet and that user is one " +
                "the watch provider knows, they become the first admin. With no admins, everyone may change settings.");

        group.MapPut("/admins", SetAdmins)
            .WithName("SetAdmins")
            .WithSummary("Choose which watch users may change settings")
            .RequireAuthorization(AdminPolicy);
    }

    /// <summary>The authorization policy for anything that changes settings.</summary>
    public const string AdminPolicy = "admin";

    private static async Task<IResult> GetAccess(
        HttpContext ctx,
        AdminStore store,
        [FromServices] IEnumerable<IWatchStateProvider> watchStateProviders,
        IOptionsSnapshot<WatchBackOptions> options,
        CancellationToken ct)
    {
        IReadOnlyList<string> admins = await store.GetAsync(ct);
        string? caller = AdminStore.CallerId(ctx);

        if (admins.Count == 0 && caller is not null)
        {
            // Only a user the provider actually lists can claim it, so a stale or made-up ID can't.
            IReadOnlyList<WatchUser> users = await WatchUserEndpoints.ListAsync(watchStateProviders, options.Value, ct);
            if (users.Any(u => u.Id == caller))
            {
                admins = await store.ClaimIfNoneAsync(caller, ct);
            }
        }

        return Results.Ok(new
        {
            restricted = admins.Count > 0,
            isAdmin = await store.IsAdminAsync(ctx.User, ctx, ct),
            admins
        });
    }

    private static async Task<IResult> SetAdmins(
        HttpContext ctx,
        [FromBody] SetAdminsRequest body,
        AdminStore store,
        [FromServices] IEnumerable<IWatchStateProvider> watchStateProviders,
        IOptionsSnapshot<WatchBackOptions> options,
        CancellationToken ct)
    {
        List<string> ids = (body.Ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return Results.BadRequest(new { ok = false, message = "At least one user must stay an admin." });
        }

        IReadOnlyList<WatchUser> users = await WatchUserEndpoints.ListAsync(watchStateProviders, options.Value, ct);
        List<string> unknown = ids.Where(id => users.All(u => u.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            return Results.BadRequest(new { ok = false, message = "Unknown users: " + string.Join(", ", unknown) });
        }

        // Guard against locking yourself out; another admin can still remove you.
        string? caller = AdminStore.CallerId(ctx);
        if (ctx.User.Identity?.AuthenticationType != "ApiKey" && caller is not null && !ids.Contains(caller))
        {
            return Results.BadRequest(new { ok = false, message = "You can't remove your own admin access." });
        }

        await store.SetAsync(ids, ct);
        return Results.Ok(new { ok = true, admins = ids });
    }

    private sealed record SetAdminsRequest(IReadOnlyList<string>? Ids);
}
