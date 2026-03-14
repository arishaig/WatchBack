using Microsoft.Extensions.Options;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Core.Services;

public class SyncService : ISyncService
{
    private readonly IWatchStateProvider _watchStateProvider;
    private readonly IEnumerable<IThoughtProvider> _thoughtProviders;
    private readonly ITimeMachineFilter _timeMachineFilter;
    private readonly WatchBackOptions _options;

    public SyncService(
        IWatchStateProvider watchStateProvider,
        IEnumerable<IThoughtProvider> thoughtProviders,
        ITimeMachineFilter timeMachineFilter,
        IOptions<WatchBackOptions> options)
    {
        _watchStateProvider = watchStateProvider;
        _thoughtProviders = thoughtProviders;
        _timeMachineFilter = timeMachineFilter;
        _options = options.Value;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        try
        {
            // Get current watch state
            var mediaContext = await _watchStateProvider.GetCurrentMediaContextAsync(ct);
            
            if (mediaContext == null)
            {
                return new SyncResult(
                    Status: SyncStatus.Idle,
                    Title: null,
                    Metadata: null,
                    AllThoughts: [],
                    TimeMachineThoughts: [],
                    TimeMachineDays: _options.TimeMachineDays,
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

            // Collect all thoughts from all providers
            var allThoughts = sourceResults
                .Where(r => r.Thoughts != null && r.Thoughts.Count > 0)
                .SelectMany(r => r.Thoughts)
                .OrderByDescending(t => t.CreatedAt)
                .ToList();

            // Apply time machine filter
            var timeMachineThoughts = _timeMachineFilter.Apply(
                allThoughts,
                mediaContext.ReleaseDate,
                _options.TimeMachineDays);

            return new SyncResult(
                Status: SyncStatus.Watching,
                Title: mediaContext.Title,
                Metadata: mediaContext,
                AllThoughts: allThoughts,
                TimeMachineThoughts: timeMachineThoughts.ToList(),
                TimeMachineDays: _options.TimeMachineDays,
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
                TimeMachineDays: _options.TimeMachineDays,
                SourceResults: []);
        }
    }
}
