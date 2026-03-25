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
    public async Task<SyncResult> SyncAsync(IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        try
        {
            // Select the configured watch state provider (used as fallback and for suppression check)
            var configuredName = options.Value.WatchProvider;
            var configuredProvider = watchStateProviders
                .FirstOrDefault(p => p.Metadata.Name.Equals(configuredName, StringComparison.OrdinalIgnoreCase))
                ?? watchStateProviders.FirstOrDefault();

            // Manual provider takes priority when it has an active context.
            // Kick off the configured provider's check early so it runs concurrently
            // with the manual check — avoids a wasted round-trip on the fallback path.
            IWatchStateProvider? watchStateProvider = null;
            MediaContext? mediaContext = null;
            IWatchStateProvider? suppressedProvider = null;
            MediaContext? suppressedContext = null;

            var manual = manualWatchStateProviders.FirstOrDefault();
            var configuredIsManual = manual != null && configuredProvider == (IWatchStateProvider)manual;

            // Start the configured provider fetch concurrently (unless it IS the manual provider)
            var configuredTask = configuredProvider != null && !configuredIsManual
                ? configuredProvider.GetCurrentMediaContextAsync(ct)
                : Task.FromResult<MediaContext?>(null);

            var manualContext = manual != null ? await manual.GetCurrentMediaContextAsync(ct) : null;
            if (manualContext is not null)
            {
                watchStateProvider = manual;
                mediaContext = manualContext;
                var configuredContext = await configuredTask;
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
                return new SyncResult(
                    Status: SyncStatus.Error,
                    Title: null,
                    Metadata: null,
                    AllThoughts: [],
                    TimeMachineThoughts: [],
                    TimeMachineDays: options.Value.TimeMachineDays,
                    SourceResults: []);
            }

            if (mediaContext is null)
            {
                return new SyncResult(
                    Status: SyncStatus.Idle,
                    Title: null,
                    Metadata: null,
                    AllThoughts: [],
                    TimeMachineThoughts: [],
                    TimeMachineDays: options.Value.TimeMachineDays,
                    SourceResults: []);
            }

            // Get thoughts and ratings from all providers in parallel
            var thoughtTasks = thoughtProviders
                .Select(provider => provider.GetThoughtsAsync(mediaContext, progress, ct))
                .ToList();

            string? imdbId = null;
            mediaContext.ExternalIds?.TryGetValue(Imdb, out imdbId);
            var ratingsProviderList = ratingsProviders.ToList();
            var ratingsTask = !string.IsNullOrEmpty(imdbId)
                ? Task.WhenAll(ratingsProviderList.Select(p => p.GetRatingsAsync(imdbId, ct)))
                : Task.FromResult<IReadOnlyList<MediaRating>[]>([]);

            var sourceResults = (await Task.WhenAll(thoughtTasks))
                .Where(r => r != null)
                .Cast<ThoughtResult>()
                .ToList();

            // Collect top-level thoughts from all providers (replies stay nested inside each thought)
            var allThoughts = sourceResults
                .Where(r => r.Thoughts is { Count: > 0 })
                .SelectMany(r => r.Thoughts!)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();

            // Apply time machine filter
            var timeMachineThoughts = timeMachineFilter.Apply(
                allThoughts,
                mediaContext.ReleaseDate,
                options.Value.TimeMachineDays);

            var ratingsArrays = await ratingsTask;
            var ratings = ratingsArrays.SelectMany(r => r).ToList();
            // Attribute ratings to the first provider that returned results
            var ratingsProviderName = ratingsProviderList
                .Zip(ratingsArrays, (p, r) => (Provider: p, Results: r))
                .FirstOrDefault(x => x.Results.Count > 0)
                .Provider?.Metadata.Name;

            var result = new SyncResult(
                Status: SyncStatus.Watching,
                Title: mediaContext.Title,
                Metadata: mediaContext,
                AllThoughts: allThoughts,
                TimeMachineThoughts: timeMachineThoughts.ToList(),
                TimeMachineDays: options.Value.TimeMachineDays,
                SourceResults: sourceResults,
                WatchProvider: watchStateProvider.Metadata.Name,
                SuppressedProvider: suppressedProvider?.Metadata.Name,
                SuppressedTitle: suppressedContext?.Title,
                Ratings: ratings.Count > 0 ? ratings : null,
                RatingsProvider: ratingsProviderName);

            // Proactively warm the cache for the next episode(s) in the background.
            if (mediaContext is EpisodeContext episode)
                prefetchService.SchedulePrefetch(episode);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed");
            return new SyncResult(
                Status: SyncStatus.Error,
                Title: null,
                Metadata: null,
                AllThoughts: [],
                TimeMachineThoughts: [],
                TimeMachineDays: options.Value.TimeMachineDays,
                SourceResults: []);
        }
    }

}