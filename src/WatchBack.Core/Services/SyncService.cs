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
    IOptionsSnapshot<WatchBackOptions> options,
    ILogger<SyncService> logger)
    : ISyncService
{
    public async Task<SyncResult> SyncAsync(IProgress<SyncProgressTick>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            // Select the configured watch state provider (used as fallback and for suppression check)
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

            // Start the configured provider fetch concurrently (unless it IS the manual provider)
            Task<MediaContext?> configuredTask = configuredProvider != null && !configuredIsManual
                ? configuredProvider.GetCurrentMediaContextAsync(ct)
                : Task.FromResult<MediaContext?>(null);

            MediaContext? manualContext = manual != null ? await manual.GetCurrentMediaContextAsync(ct) : null;
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
                // No manual override — use the already-started configured provider result
                watchStateProvider = configuredProvider;
                mediaContext = await configuredTask;
            }

            if (watchStateProvider is null)
            {
                logger.LogError("No watch state providers registered");
                return SyncResult.Error(options.Value.TimeMachineDays);
            }

            if (mediaContext is null)
            {
                return SyncResult.Idle(options.Value.TimeMachineDays);
            }

            // Get thoughts and ratings from all providers in parallel
            List<Task<ThoughtResult?>> thoughtTasks = thoughtProviders
                .Select(provider => provider.GetThoughtsAsync(mediaContext, progress, ct))
                .ToList();

            string? imdbId = null;
            mediaContext.ExternalIds?.TryGetValue(Imdb, out imdbId);
            List<IRatingsProvider> ratingsProviderList = ratingsProviders.ToList();
            Task<IReadOnlyList<MediaRating>[]> ratingsTask = !string.IsNullOrEmpty(imdbId)
                ? Task.WhenAll(ratingsProviderList.Select(p => p.GetRatingsAsync(imdbId, ct)))
                : Task.FromResult<IReadOnlyList<MediaRating>[]>([]);

            List<ThoughtResult> sourceResults = (await Task.WhenAll(thoughtTasks))
                .Where(r => r != null)
                .Cast<ThoughtResult>()
                .ToList();

            // Collect top-level thoughts from all providers (replies stay nested inside each thought)
            List<Thought> allThoughts = sourceResults
                .Where(r => r.Thoughts is { Count: > 0 })
                .SelectMany(r => r.Thoughts!)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();

            // Apply time machine filter
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

            // Proactively warm the cache for the next episode(s) in the background.
            if (mediaContext is EpisodeContext episode)
            {
                prefetchService.SchedulePrefetch(episode);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed");
            return SyncResult.Error(options.Value.TimeMachineDays);
        }
    }
}
