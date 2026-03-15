using Microsoft.Extensions.Options;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Core.Services;

public class SyncService : ISyncService
{
    private readonly IEnumerable<IWatchStateProvider> _watchStateProviders;
    private readonly IEnumerable<IThoughtProvider> _thoughtProviders;
    private readonly ITimeMachineFilter _timeMachineFilter;
    private readonly IOptionsSnapshot<WatchBackOptions> _options;

    public SyncService(
        IEnumerable<IWatchStateProvider> watchStateProviders,
        IEnumerable<IThoughtProvider> thoughtProviders,
        ITimeMachineFilter timeMachineFilter,
        IOptionsSnapshot<WatchBackOptions> options)
    {
        _watchStateProviders = watchStateProviders;
        _thoughtProviders = thoughtProviders;
        _timeMachineFilter = timeMachineFilter;
        _options = options;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        try
        {
            // Select the configured watch state provider, falling back to the first registered
            var configured = _options.Value.WatchProvider;
            var watchStateProvider = _watchStateProviders
                .FirstOrDefault(p => p.Metadata?.Name?.Equals(configured, StringComparison.OrdinalIgnoreCase) == true)
                ?? _watchStateProviders.First();

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
                    TimeMachineDays: _options.Value.TimeMachineDays,
                    SourceResults: []);
            }

            // Get thoughts from all providers in parallel
            var thoughtTasks = _thoughtProviders
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
            var timeMachineThoughts = _timeMachineFilter.Apply(
                allThoughts,
                mediaContext.ReleaseDate,
                _options.Value.TimeMachineDays);

            return new SyncResult(
                Status: SyncStatus.Watching,
                Title: mediaContext.Title,
                Metadata: mediaContext,
                AllThoughts: allThoughts,
                TimeMachineThoughts: timeMachineThoughts.ToList(),
                TimeMachineDays: _options.Value.TimeMachineDays,
                SourceResults: sourceResults);
        }
        catch
        {
            return new SyncResult(
                Status: SyncStatus.Error,
                Title: null,
                Metadata: null,
                AllThoughts: [],
                TimeMachineThoughts: [],
                TimeMachineDays: _options.Value.TimeMachineDays,
                SourceResults: []);
        }
    }

}
