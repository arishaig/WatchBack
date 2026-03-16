namespace WatchBack.Api.Logging;

public sealed record ProviderSyncRecord(string Source, int ThoughtCount);

public sealed record DiagnosticsStatusResponse(string Version, SyncSnapshot? LastSync);

public sealed record SyncSnapshot(
    DateTimeOffset Timestamp,
    string Status,
    string? Title,
    List<ProviderSyncRecord> Sources);

/// <summary>Singleton that holds the most recent sync result for the diagnostics panel.</summary>
public sealed class SyncHistoryStore
{
    private volatile SyncSnapshot? _latest;

    public void Record(SyncSnapshot snapshot) => _latest = snapshot;

    public SyncSnapshot? GetLatest() => _latest;
}
