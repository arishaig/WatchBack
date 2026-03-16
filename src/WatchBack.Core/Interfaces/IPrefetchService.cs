using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// Proactively warms the thought cache for the next episode after a sync completes,
/// so the user doesn't wait for a full fetch when binging multiple episodes in a row.
/// </summary>
public interface IPrefetchService
{
    /// <summary>
    /// Evicts stale prefetch entries from a prior episode and schedules a background
    /// prefetch for the episode(s) most likely to be watched next.
    /// Returns immediately; all work is fire-and-forget.
    /// </summary>
    void SchedulePrefetch(EpisodeContext current);
}
