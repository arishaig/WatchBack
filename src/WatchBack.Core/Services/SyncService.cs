using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Core.Services;

public class SyncService(
    IEnumerable<IWatchStateProvider> watchStateProviders,
    IEnumerable<IThoughtProvider> thoughtProviders,
    ITimeMachineFilter timeMachineFilter,
    IPrefetchService prefetchService,
    IOptionsSnapshot<WatchBackOptions> options,
    ILogger<SyncService> logger)
    : ISyncService
{
    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        try
        {
            // Select the configured watch state provider, falling back to the first registered
            var configured = options.Value.WatchProvider;
            var watchStateProvider = watchStateProviders
                .FirstOrDefault(p => p.Metadata.Name.Equals(configured, StringComparison.OrdinalIgnoreCase))
                ?? watchStateProviders.FirstOrDefault();

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
                .Select(provider => provider.GetThoughtsAsync(mediaContext, ct))
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
                SourceResults: sourceResults);

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