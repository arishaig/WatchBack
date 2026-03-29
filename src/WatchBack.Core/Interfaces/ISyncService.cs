using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// The service that returns a consolidated collection of
/// data from various DataProviders and other sources in
/// a format that is easily parsable by the UI
/// </summary>
public interface ISyncService
{
    /// <summary>
    /// Fetches the current watch state and all Thoughts for the currently playing media,
    /// aggregated across all registered ThoughtProviders.
    /// </summary>
    /// <param name="progress">Optional progress sink; each ThoughtProvider will report ticks as it completes work</param>
    Task<SyncResult> SyncAsync(IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default);
}
