using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Core.Tests;

/// <summary>Calls providers directly, so SyncService tests exercise orchestration without caching.</summary>
internal sealed class PassThroughResultCache : IMediaResultCache
{
    public Task<ThoughtResult?> GetThoughtsAsync(IThoughtProvider provider, MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default) =>
        provider.GetThoughtsAsync(mediaContext, progress, ct);

    public Task<IReadOnlyList<MediaRating>> GetRatingsAsync(IRatingsProvider provider, MediaContext mediaContext,
        string imdbId, CancellationToken ct = default) =>
        provider.GetRatingsAsync(imdbId, ct);

    public void Evict(IThoughtProvider provider, MediaContext mediaContext)
    {
    }

    public List<MediaContext> Invalidated { get; } = [];

    public Task<bool> InvalidateAsync(IEnumerable<IThoughtProvider> providers, IEnumerable<IRatingsProvider> ratingsProviders,
        MediaContext mediaContext, CancellationToken ct = default)
    {
        Invalidated.Add(mediaContext);
        return Task.FromResult(true);
    }
}
