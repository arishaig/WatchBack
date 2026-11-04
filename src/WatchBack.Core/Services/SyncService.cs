using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

using static WatchBack.Core.Models.ExternalIdType;

namespace WatchBack.Core.Services;

public class SyncService(
    IEnumerable<IWatchStateProvider> watchStateProviders,
    IEnumerable<IManualWatchStateProvider> manualWatchStateProviders,
    IEnumerable<IThoughtProvider> thoughtProviders,
    IEnumerable<IRatingsProvider> ratingsProviders,
    ITimeMachineFilter timeMachineFilter,
    IPrefetchService prefetchService,
    IMediaResultCache resultCache,
    IOptionsSnapshot<WatchBackOptions> options,
    ILogger<SyncService> logger)
    : ISyncService
{
    public async Task<SyncResult> SyncAsync(IProgress<SyncProgressTick>? progress = null,
        string? watchUserId = null, string? clientId = null, CancellationToken ct = default)
    {
        try
        {
            string? viewerKey = WatchViewer.Key(watchUserId, clientId);
            (IWatchStateProvider? watchStateProvider, MediaContext? mediaContext,
                IWatchStateProvider? suppressedProvider, MediaContext? suppressedContext) =
                await ResolveCurrentAsync(watchUserId, viewerKey, ct);

            if (watchStateProvider is null)
            {
                logger.LogError("No watch state providers registered");
                return SyncResult.Error(options.Value.TimeMachineDays);
            }

            if (mediaContext is null)
            {
                return SyncResult.Idle(options.Value.TimeMachineDays);
            }

            IThoughtProvider[] activeProviders = ActiveProviders();

            string? imdbId = null;
            mediaContext.ExternalIds?.TryGetValue(Imdb, out imdbId);
            HashSet<string> disabled = DisabledSections();
            List<IRatingsProvider> ratingsProviderList = ratingsProviders
                .Where(p => p.ConfigSection is null || !disabled.Contains(p.ConfigSection))
                .ToList();
            Task<IReadOnlyList<MediaRating>[]> ratingsTask = !string.IsNullOrEmpty(imdbId)
                ? Task.WhenAll(ratingsProviderList.Select(p => resultCache.GetRatingsAsync(p, mediaContext, imdbId, ct)))
                : Task.FromResult<IReadOnlyList<MediaRating>[]>([]);

            ThoughtResult?[] rawResults = await Task.WhenAll(
                activeProviders.Select(p => resultCache.GetThoughtsAsync(p, mediaContext, progress, ct)));

            List<ThoughtResult> sourceResults = activeProviders
                .Zip(rawResults)
                .Where(pair => pair.Second != null)
                .Select(pair => NormalizeThoughts(pair.First, pair.Second!))
                .ToList();

            // Replies stay nested inside each thought — we only flatten the top level.
            List<Thought> allThoughts = sourceResults
                .Where(r => r.Thoughts is { Count: > 0 })
                .SelectMany(r => r.Thoughts!)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();

            IReadOnlyList<Thought> timeMachineThoughts = timeMachineFilter.Apply(
                allThoughts,
                mediaContext.ReleaseDate,
                options.Value.TimeMachineDays);

            IReadOnlyList<MediaRating>[] ratingsArrays = await ratingsTask;
            List<MediaRating> ratings = ratingsArrays.SelectMany(r => r).ToList();
            // Attribute ratings to the first provider that returned results
            string? ratingsProviderName = ratingsProviderList
                .Zip(ratingsArrays, (p, r) => (Provider: p, Results: r))
                .FirstOrDefault(x => x.Results.Count > 0)
                .Provider?.Metadata.Name;

            SyncResult result = new(
                SyncStatus.Watching,
                mediaContext.Title,
                mediaContext,
                allThoughts,
                timeMachineThoughts.ToList(),
                options.Value.TimeMachineDays,
                sourceResults,
                watchStateProvider.Metadata.Name,
                suppressedProvider?.Metadata.Name,
                suppressedContext?.Title,
                ratings.Count > 0 ? ratings : null,
                ratingsProviderName);

            if (mediaContext is EpisodeContext episode)
            {
                prefetchService.SchedulePrefetch(episode, viewerKey);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed");
            return SyncResult.Error(options.Value.TimeMachineDays);
        }
    }

    // Which provider's playback wins, and what it is playing. A manually chosen item takes priority;
    // the configured provider's playback is then reported as suppressed.
    private async Task<(IWatchStateProvider? Provider, MediaContext? Context, IWatchStateProvider? SuppressedProvider,
        MediaContext? SuppressedContext)> ResolveCurrentAsync(string? watchUserId, string? viewerKey, CancellationToken ct)
    {
        string configuredName = options.Value.WatchProvider;
        IWatchStateProvider? configuredProvider = watchStateProviders
                                                      .FirstOrDefault(p => p.Metadata.Name.Equals(configuredName,
                                                          StringComparison.OrdinalIgnoreCase))
                                                  ?? watchStateProviders.FirstOrDefault();

        // Manual provider takes priority when it has an active context.
        // Kick off the configured provider's check early so it runs concurrently
        // with the manual check — avoids a wasted round-trip on the fallback path.
        IWatchStateProvider? watchStateProvider;
        MediaContext? mediaContext;
        IWatchStateProvider? suppressedProvider = null;
        MediaContext? suppressedContext = null;

        IManualWatchStateProvider? manual = manualWatchStateProviders.FirstOrDefault();
        bool configuredIsManual = manual != null && configuredProvider == manual;

        Task<MediaContext?> configuredTask = configuredProvider != null && !configuredIsManual
            ? string.IsNullOrEmpty(watchUserId)
                ? configuredProvider.GetCurrentMediaContextAsync(ct)
                : configuredProvider.GetCurrentMediaContextAsync(watchUserId, ct)
            : Task.FromResult<MediaContext?>(null);

        MediaContext? manualContext = manual != null ? await manual.GetCurrentMediaContextForClientAsync(viewerKey, ct) : null;
        if (manualContext is not null)
        {
            watchStateProvider = manual;
            mediaContext = manualContext;
            MediaContext? configuredContext = await configuredTask;
            if (configuredContext is not null)
            {
                suppressedProvider = configuredProvider;
                suppressedContext = configuredContext;
            }
        }
        else
        {
            watchStateProvider = configuredProvider;
            mediaContext = await configuredTask;
        }

        return (watchStateProvider, mediaContext, suppressedProvider, suppressedContext);
    }

    public async Task<bool> RefreshCurrentAsync(string? watchUserId = null, string? clientId = null,
        CancellationToken ct = default)
    {
        try
        {
            (_, MediaContext? mediaContext, _, _) =
                await ResolveCurrentAsync(watchUserId, WatchViewer.Key(watchUserId, clientId), ct);
            return mediaContext is not null &&
                   await resultCache.InvalidateAsync(ActiveProviders(), ratingsProviders, mediaContext, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Like SyncAsync: a failing watch-state provider must not turn the Sync button into a 500.
            logger.LogError(ex, "Manual refresh failed");
            return false;
        }
    }

    private HashSet<string> DisabledSections() =>
        options.Value.DisabledProviders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IThoughtProvider[] ActiveProviders()
    {
        HashSet<string> disabledSet = DisabledSections();

        return thoughtProviders
            .Where(p => p.ConfigSection is null || !disabledSet.Contains(p.ConfigSection))
            .ToArray();
    }

    private static ThoughtResult NormalizeThoughts(IThoughtProvider provider, ThoughtResult result)
    {
        if (provider is not IContentNormalizer normalizer || result.Thoughts is not { Count: > 0 })
        {
            return result;
        }

        return result with
        {
            Thoughts = result.Thoughts
                .Select(t => t with
                {
                    Content = normalizer.NormalizeContent(t.Content),
                    PostBody = t.PostBody != null ? normalizer.NormalizeContent(t.PostBody) : null
                })
                .ToList()
        };
    }
}
