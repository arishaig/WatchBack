using Microsoft.Extensions.Caching.Memory;

namespace WatchBack.Api.Endpoints;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system")
            .WithTags("System")
            .RequireAuthorization();

        group.MapPost("/clear-cache", ClearCache)
            .WithName("ClearCache")
            .WithSummary("Evict all in-memory cache entries");

        group.MapPost("/restart", Restart)
            .WithName("Restart")
            .WithSummary("Gracefully stop the application (expects host to restart it)");
    }

    private static IResult ClearCache(IMemoryCache cache)
    {
        if (cache is MemoryCache mc)
            mc.Compact(1.0);

        return Results.Ok(new { ok = true });
    }

    private static IResult Restart(IHostApplicationLifetime lifetime)
    {
        // Respond before stopping so the client receives the 200.
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            lifetime.StopApplication();
        });

        return Results.Ok(new { ok = true });
    }
}
