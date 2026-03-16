using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Core.Services;

public class SyncService(
    IEnumerable<IWatchStateProvider> watchStateProviders,
    IEnumerable<IManualWatchStateProvider> manualWatchStateProviders,
    IEnumerable<IThoughtProvider> thoughtProviders,
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
            // While checking, also kick off the configured provider's check in parallel
            // so we can report if it's also active (suppressed by the manual override).
            IWatchStateProvider? watchStateProvider = null;
            IWatchStateProvider? suppressedProvider = null;
            MediaContext? suppressedContext = null;

            foreach (var manual in manualWatchStateProviders)
            {
                // Run both checks concurrently when there is a non-manual configured provider
                Task<MediaContext?> configuredTask = configuredProvider != null && configuredProvider != (IWatchStateProvider)manual
                    ? configuredProvider.GetCurrentMediaContextAsync(ct)
                    : Task.FromResult<MediaContext?>(null);

                var manualContext = await manual.GetCurrentMediaContextAsync(ct);
                if (manualContext != null)
                {
                    watchStateProvider = manual;
                    var configuredContext = await configuredTask;
                    if (configuredContext != null)
                    {
                        suppressedProvider = configuredProvider;
                        suppressedContext = configuredContext;
                    }
                    break;
                }
            }

            if (watchStateProvider == null)
                watchStateProvider = configuredProvider;

            if (watchStateProvider == null)
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

            // Get current watch state
            var mediaContext = await watchStateProvider.GetCurrentMediaContextAsync(ct);

            if (mediaContext == null)
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

            // Get thoughts from all providers in parallel
            var thoughtTasks = thoughtProviders
                .Select(provider => provider.GetThoughtsAsync(mediaContext, progress, ct))
                .ToList();

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
                SuppressedTitle: suppressedContext?.Title);

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