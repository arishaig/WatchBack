using System.Text.Json;

using WatchBack.Api.Logging;
using WatchBack.Api.Serialization;

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
}
