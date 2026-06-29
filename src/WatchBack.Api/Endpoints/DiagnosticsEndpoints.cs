using System.Text.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Serilog.Events;

using WatchBack.Api.Logging;
using WatchBack.Api.Serialization;
using WatchBack.Infrastructure.Persistence;

namespace WatchBack.Api.Endpoints;

public sealed record LogFileConfig(string Directory);

internal sealed record SyncLogEntry(
    DateTimeOffset Timestamp,
    string Status,
    string? Title,
    int ThoughtCount,
    string? ErrorMessage,
    long? DurationMs);

public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/diagnostics")
            .WithTags("Diagnostics");

        group.MapPost("/client-log", PostClientLog)
            .WithName("PostClientLog")
            .WithSummary("Ingest a batch of UI-originated log entries into the log buffer")
            .AllowAnonymous()
            .RequireRateLimiting("client-log");

        group.MapGet("/logs", GetLogs)
            .WithName("GetLogs")
            .WithSummary("Get recent application log entries");

        group.MapGet("/logs/stream", StreamLogs)
            .WithName("StreamLogs")
            .WithSummary("Stream live log entries via Server-Sent Events");

        group.MapGet("/status", GetStatus)
            .WithName("GetDiagnosticsStatus")
            .WithSummary("Get last sync result summary");

        group.MapGet("/logs/raw", GetRawLogs)
            .WithName("GetRawLogs")
            .WithSummary("Get today's raw log file content");

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
        IReadOnlyList<LogEntry> entries = buffer.GetEntries(level, Math.Clamp(limit, 1, 500));
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

        using IDisposable sub = buffer.Subscribe(out ChannelReader<LogEntry> reader);
        try
        {
            await foreach (LogEntry entry in reader.ReadAllAsync(ct))
            {
                string json = JsonSerializer.Serialize(entry, WatchBackJsonContext.Default.LogEntry);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static IResult GetStatus(SyncHistoryStore store)
    {
        return Results.Ok(new DiagnosticsStatusResponse(ThisAssembly.Info.InformationalVersion, store.GetLatest()));
    }

    private static async Task<IResult> GetRawLogs(LogFileConfig logConfig, CancellationToken ct)
    {
        string? path = Directory.GetFiles(logConfig.Directory, "watchback????????.log")
            .OrderDescending()
            .FirstOrDefault();

        if (path is null)
            return Results.Text(string.Empty, "text/plain");

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return Results.Text(await reader.ReadToEndAsync(ct), "text/plain");
    }

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
        List<SyncLogEntry> entries = await db.SyncLogs
            .OrderByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(e => new SyncLogEntry(e.Timestamp, e.Status, e.Title, e.ThoughtCount, e.ErrorMessage, e.DurationMs))
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

    private sealed record ClientLogEntry(string? Event, string? Message, string? Level, string? Data);
    private const int MaxClientBatch = 50;

    private static IResult PostClientLog(
        [FromBody] ClientLogEntry[] entries,
        InMemoryLogBuffer buffer,
        Serilog.ILogger fileLogger)
    {
        foreach (ClientLogEntry entry in entries.Take(MaxClientBatch))
        {
            string level = entry.Level switch
            {
                "Information" or "Warning" or "Error" => entry.Level,
                _ => "Debug"
            };
            LogEventLevel serilogLevel = level switch
            {
                "Information" => LogEventLevel.Information,
                "Warning" => LogEventLevel.Warning,
                "Error" => LogEventLevel.Error,
                _ => LogEventLevel.Debug
            };
            string ev = Sanitize(entry.Event);
            string msg = Sanitize(entry.Message);
            string fullMessage = string.IsNullOrEmpty(entry.Data)
                ? $"[{ev}] {msg}"
                : $"[{ev}] {msg} | {entry.Data}";

            buffer.Add(new LogEntry(DateTimeOffset.UtcNow, level, "UI", fullMessage, null));
            fileLogger.Write(serilogLevel, "[UI] [{UiEvent}] {UiMessage}", ev, msg);
        }
        return Results.Ok();
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string s = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 1000 ? s[..1000] : s;
    }
}
