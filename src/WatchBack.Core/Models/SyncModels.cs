namespace WatchBack.Core.Models;

public enum SyncStatus
{
    Idle,
    Watching,
    Error
}

/// <summary>
/// A consolidated collection of data from various
/// DataProviders and other sources in a format that
/// is easily parsable by the UI
/// </summary>
/// <param name="Status">One of the possible statuses as enumerated in the SyncStatus enum</param>
/// <param name="Title">The title of the media currently being watched, or null if idle</param>
/// <param name="Metadata">The full media context including episode details, or null if idle</param>
/// <param name="AllThoughts">All Thoughts retrieved from all ThoughtProviders for the current media</param>
/// <param name="TimeMachineThoughts">Thoughts filtered to only include those within the Time Machine window</param>
/// <param name="TimeMachineDays">The number of days Time Machine is configured to filter relative to the air date</param>
/// <param name="SourceResults">The aggregated results from each ThoughtProvider, including post metadata and Thought collections</param>
public record SyncResult(
    SyncStatus Status,
    string? Title,
    MediaContext? Metadata,
    IReadOnlyList<Thought> AllThoughts,
    IReadOnlyList<Thought> TimeMachineThoughts,
    int TimeMachineDays,
    IReadOnlyList<ThoughtResult> SourceResults);