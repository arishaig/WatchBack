namespace WatchBack.Infrastructure.Persistence.Entities;

/// <summary>
/// Stores historical sync attempt records for audit trail and debugging.
/// </summary>
public class SyncLogEntity
{
    public int Id { get; set; }

    /// <summary>
    /// Timestamp when sync was initiated
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Sync result status (e.g., "Idle", "Watching", "Error")
    /// </summary>
    public required string Status { get; set; }

    /// <summary>
    /// Title of media being watched, if any
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Count of thoughts aggregated in this sync
    /// </summary>
    public int ThoughtCount { get; set; }

    /// <summary>
    /// Error message if sync failed, null if successful
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Duration of sync operation in milliseconds
    /// </summary>
    public long? DurationMs { get; set; }
}
