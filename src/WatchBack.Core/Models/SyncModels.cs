namespace WatchBack.Core.Models;

public enum SyncStatus
{
    Idle,
    Watching,
    Error
}

public record SyncResult(
    SyncStatus Status,
    string? Title,
    MediaContext? Metadata,
    IReadOnlyList<Thought> AllThoughts,
    IReadOnlyList<Thought> TimeMachineThoughts,
    int TimeMachineDays,
    IReadOnlyList<ThoughtResult> SourceResults);
