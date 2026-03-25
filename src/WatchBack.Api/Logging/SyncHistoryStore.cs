using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
public sealed class SyncHistoryStore(IServiceScopeFactory scopeFactory, ILogger<SyncHistoryStore> logger)
{
    private volatile SyncSnapshot? _latest;

    public void Record(SyncSnapshot snapshot, long? durationMs = null)
    {
        _latest = snapshot;
        _ = PersistAsync(snapshot, durationMs);
    }

    public SyncSnapshot? GetLatest() => _latest;

    private async Task PersistAsync(SyncSnapshot snapshot, long? durationMs)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();

            var thoughtCount = snapshot.Sources.Sum(s => s.ThoughtCount);
            var entity = new SyncLogEntity
            {
                Timestamp = snapshot.Timestamp,
                Status = snapshot.Status,
                Title = snapshot.Title,
                ThoughtCount = thoughtCount,
                DurationMs = durationMs
            };

            db.SyncLogs.Add(entity);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
#pragma warning disable CA1848
            logger.LogWarning(ex, "Failed to persist sync log entry");
#pragma warning restore CA1848
        }
    }
}
