using Microsoft.Extensions.Caching.Memory;

using WatchBack.Core.Models;

namespace WatchBack.Infrastructure.ThoughtProviders;

/// <summary>
///     Limits sync progress reporting to a provider's first fetch for a given media item.
///     The frontend shows the loading bar whenever it sees intermediate progress, so
///     background retries (after a failure) and TTL refreshes must stay silent: the bar is
///     meant for the initial bulk content load only.
/// </summary>
internal static class InitialLoadProgress
{
    private static readonly TimeSpan s_markerTtl = TimeSpan.FromHours(24);

    /// <summary>
    ///     Returns <paramref name="progress" /> the first time <paramref name="cacheKey" /> is fetched
    ///     and <c>null</c> on every later fetch (retry or refresh) within the marker's lifetime.
    /// </summary>
    /// <summary>The marker for <paramref name="cacheKey" />; removing it makes the next fetch report progress again.</summary>
    public static string MarkerKey(string cacheKey) => $"{cacheKey}:initial-load";

    public static IProgress<SyncProgressTick>? ForFetch(
        IMemoryCache cache, string cacheKey, IProgress<SyncProgressTick>? progress)
    {
        string markerKey = MarkerKey(cacheKey);
        if (cache.TryGetValue(markerKey, out _))
        {
            return null;
        }

        cache.Set(markerKey, true, s_markerTtl);
        return progress;
    }
}
