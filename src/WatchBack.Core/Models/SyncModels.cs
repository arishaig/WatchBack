namespace WatchBack.Core.Models;

/// <summary>
/// Represents the current state of a sync operation
/// </summary>
public enum SyncStatus
{
    /// <summary>No media is currently being watched</summary>
    Idle,
    /// <summary>Media is currently being watched and Thoughts have been retrieved</summary>
    Watching,
    /// <summary>An error occurred during the sync</summary>
    Error
}

/// <summary>
/// Emitted by each ThoughtProvider as work completes, so the UI can
/// display a progress bar while fetching Thoughts.
/// </summary>
/// <param name="Weight">The amount of work completed, relative to the provider's ExpectedWeight</param>
/// <param name="Provider">The name of the provider reporting progress</param>
public record SyncProgressTick(int Weight, string Provider);

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
/// <param name="WatchProvider">Name of the watch state provider that supplied the current media context</param>
/// <param name="SuppressedProvider">Name of the configured provider that has active context but was overridden by the manual provider</param>
/// <param name="SuppressedTitle">Title the suppressed provider would have shown</param>
public record SyncResult(
    SyncStatus Status,
    string? Title,
    MediaContext? Metadata,
    IReadOnlyList<Thought> AllThoughts,
    IReadOnlyList<Thought> TimeMachineThoughts,
    int TimeMachineDays,
    IReadOnlyList<ThoughtResult> SourceResults,
    string? WatchProvider = null,
    string? SuppressedProvider = null,
    string? SuppressedTitle = null,
    IReadOnlyList<MediaRating>? Ratings = null,
    string? RatingsProvider = null);