namespace WatchBack.Infrastructure.Persistence.Entities;

/// <summary>
/// Stores historical sync attempt records for audit trail and debugging.
/// </summary>
public class SyncLogEntity
{
    public int Id { get; init; }

    /// <summary>
    /// Timestamp when sync was initiated
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Sync result status (e.g., "Idle", "Watching", "Error")
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Title of media being watched, if any
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Count of thoughts aggregated in this sync
    /// </summary>
    public int ThoughtCount { get; init; }

    /// <summary>
    /// Error message if sync failed, null if successful
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Duration of sync operation in milliseconds
    /// </summary>
    public long? DurationMs { get; init; }
}