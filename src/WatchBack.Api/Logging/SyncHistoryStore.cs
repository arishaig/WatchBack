using WatchBack.Infrastructure.Persistence;
using WatchBack.Infrastructure.Persistence.Entities;

namespace WatchBack.Api.Logging;

public sealed record ProviderSyncRecord(string Source, int ThoughtCount);

public sealed record DiagnosticsStatusResponse(string Version, SyncSnapshot? LastSync);

public sealed record SyncSnapshot(
    DateTimeOffset Timestamp,
    string Status,
    string? Title,
    List<ProviderSyncRecord> Sources);

/// <summary>Singleton that holds the most recent sync result and persists sync logs to the database.</summary>
public sealed partial class SyncHistoryStore(IServiceScopeFactory scopeFactory, ILogger<SyncHistoryStore> logger)
{
    private volatile SyncSnapshot? _latest;

    public void Record(SyncSnapshot snapshot, long? durationMs = null)
    {
        _latest = snapshot;
        _ = PersistAsync(snapshot, durationMs);
    }

    public SyncSnapshot? GetLatest()
    {
        return _latest;
    }

    private async Task PersistAsync(SyncSnapshot snapshot, long? durationMs)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            WatchBackDbContext db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();

            int thoughtCount = snapshot.Sources.Sum(s => s.ThoughtCount);
            SyncLogEntity entity = new()
            {
                Timestamp = snapshot.Timestamp,
                Status = snapshot.Status,
                Title = snapshot.Title,
                ThoughtCount = thoughtCount,
                DurationMs = durationMs
            };

            db.SyncLogs.Add(entity);
            // CancellationToken.None: fire-and-forget persist should not be tied to the
            // request lifetime. Worst case on shutdown the write is abandoned by the runtime.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogPersistFailure(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to persist sync log entry")]
    private static partial void LogPersistFailure(ILogger logger, Exception ex);
}
