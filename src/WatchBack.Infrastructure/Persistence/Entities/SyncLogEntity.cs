namespace WatchBack.Infrastructure.Persistence.Entities;

/// <summary>
/// Stores historical sync attempt records for audit trail and debugging.
/// </summary>
public class SyncLogEntity
{
    public int Id { get; init; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>One of "Idle", "Watching", or "Error".</summary>
    public required string Status { get; init; }

    public string? Title { get; init; }

    public int ThoughtCount { get; init; }

    public string? ErrorMessage { get; init; }

    public long? DurationMs { get; init; }
}
