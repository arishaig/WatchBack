using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using WatchBack.Api.Logging;
using WatchBack.Api.Serialization;
using WatchBack.Infrastructure.Persistence;

namespace WatchBack.Api.Endpoints;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diagnostics")
            .WithTags("Diagnostics");

        group.MapGet("/logs", GetLogs)
            .WithName("GetLogs")
            .WithSummary("Get recent application log entries");

        group.MapGet("/logs/stream", StreamLogs)
            .WithName("StreamLogs")
            .WithSummary("Stream live log entries via Server-Sent Events");

        group.MapGet("/status", GetStatus)
            .WithName("GetDiagnosticsStatus")
            .WithSummary("Get last sync result summary");

        group.MapDelete("/logs", ClearLogs)
            .WithName("ClearLogs")
            .WithSummary("Clear the in-memory log buffer");

        group.MapGet("/sync-history", GetSyncHistory)
            .WithName("GetSyncHistory")
            .WithSummary("Get historical sync log entries from the database");

        group.MapDelete("/sync-history", ClearSyncHistory)
            .WithName("ClearSyncHistory")
            .WithSummary("Clear all persisted sync log entries");
    }

    private static IResult GetLogs(InMemoryLogBuffer buffer, string? level = null, int limit = 200)
    {
        var entries = buffer.GetEntries(level, Math.Clamp(limit, 1, 500));
        return Results.Ok(entries);
    }

    private static async Task StreamLogs(
        InMemoryLogBuffer buffer,
        HttpContext context,
        CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        using var sub = buffer.Subscribe(out var reader);
        try
        {
            await foreach (var entry in reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.Serialize(entry, WatchBackJsonContext.Default.LogEntry);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static IResult GetStatus(SyncHistoryStore store) =>
        Results.Ok(new DiagnosticsStatusResponse(ThisAssembly.Info.InformationalVersion, store.GetLatest()));

    private static IResult ClearLogs(InMemoryLogBuffer buffer)
    {
        buffer.Clear();
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> GetSyncHistory(
        WatchBackDbContext db,
        int limit = 50,
        CancellationToken ct = default)
    {
        var entries = await db.SyncLogs
            .OrderByDescending(e => e.Timestamp)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(e => new
            {
                e.Timestamp,
                e.Status,
                e.Title,
                e.ThoughtCount,
                e.ErrorMessage,
                e.DurationMs
            })
            .ToListAsync(ct);
        return Results.Ok(entries);
    }

    private static async Task<IResult> ClearSyncHistory(
        WatchBackDbContext db,
        CancellationToken ct = default)
    {
        await db.SyncLogs.ExecuteDeleteAsync(ct);
        return Results.Ok(new { ok = true });
    }
}
