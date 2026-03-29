using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// The minimal representation of a Thought provider. This could be
/// a source for comments, social media posts, reviews, or something else.
/// </summary>
public interface IThoughtProvider : IDataProvider
{
    /// <summary>
    /// Upper bound on the total tick weight this provider will report via IProgress during a full fetch.
    /// Used to compute the progress bar denominator before fetching begins.
    /// </summary>
    int ExpectedWeight { get; }

    /// <summary>
    /// Returns the cache key this provider uses for the given media context.
    /// Used by PrefetchService to evict stale entries without duplicating key formats.
    /// </summary>
    string GetCacheKey(MediaContext mediaContext);

    /// <summary>
    /// Fetches Thoughts for the given media context. Providers should report progress
    /// via <paramref name="progress"/> as work completes so the UI can update the progress bar.
    /// Returns null if no relevant content was found.
    /// </summary>
    /// <param name="mediaContext">The media currently being watched</param>
    /// <param name="progress">Optional progress sink; implementations should report one or more ticks summing to ExpectedWeight</param>
    Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default);
}
