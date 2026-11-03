using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     The fetch-once layer in front of thought and ratings providers. Results are cached by air
///     date (old media for months, fresh episodes for hours), persisted across restarts, and
///     fetched at most once at a time per provider and media item however many viewers poll.
/// </summary>
public interface IMediaResultCache
{
    /// <summary>
    ///     Returns <paramref name="provider" />'s cached thoughts for <paramref name="mediaContext" />,
    ///     fetching them first if there is no live entry. Only the caller whose request starts a fetch
    ///     receives its progress ticks; cache hits and callers that join a fetch already running report none.
    /// </summary>
    Task<ThoughtResult?> GetThoughtsAsync(IThoughtProvider provider, MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default);

    /// <summary>Cached ratings for <paramref name="imdbId" />, fetched on a miss. Aged by <paramref name="mediaContext" />'s air date.</summary>
    Task<IReadOnlyList<MediaRating>> GetRatingsAsync(IRatingsProvider provider, MediaContext mediaContext,
        string imdbId, CancellationToken ct = default);

    /// <summary>Drops in-memory entries for <paramref name="mediaContext" /> (e.g. unused prefetch predictions).</summary>
    void Evict(IThoughtProvider provider, MediaContext mediaContext);

    /// <summary>
    ///     Forgets everything cached for <paramref name="mediaContext" />, in memory and on disk, so the
    ///     next lookup goes upstream. Backs the manual refresh. Returns false, doing nothing, when the
    ///     same media was invalidated within the last minute.
    /// </summary>
    Task<bool> InvalidateAsync(IEnumerable<IThoughtProvider> providers, IEnumerable<IRatingsProvider> ratingsProviders,
        MediaContext mediaContext, CancellationToken ct = default);
}
