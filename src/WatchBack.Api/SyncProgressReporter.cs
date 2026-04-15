using System.Text.Json;

using WatchBack.Api.Serialization;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Api;

/// <summary>
///     Accumulates <see cref="SyncProgressTick" />s from the sync pipeline into
///     segmented progress-bar events (one segment per provider) suitable for
///     writing to an SSE stream.
/// </summary>
public sealed class SyncProgressReporter
{
    private readonly IReadOnlyDictionary<string, (string Color, int Total)> _providerMeta;
    private readonly int _totalWeight;
    private readonly Dictionary<string, int> _providerCompleted = new();
    private int _completed;

    public SyncProgressReporter(IEnumerable<IThoughtProvider> providers)
    {
        _providerMeta = providers.ToDictionary(
            p => p.Metadata.Name,
            p => (Color: p.Metadata.BrandData?.Color ?? "var(--wb-accent)", Total: p.ExpectedWeight));
        _totalWeight = _providerMeta.Values.Sum(v => v.Total);
    }

    public int TotalWeight => _totalWeight;

    public int Completed => _completed;

    /// <summary>Emits the initial 0% event so the UI renders the bar before any ticks arrive.</summary>
    public string BuildInitialEvent() => BuildEvent(0, []);

    public string OnTick(SyncProgressTick tick)
    {
        _completed += tick.Weight;
        _providerCompleted[tick.Provider] = _providerCompleted.GetValueOrDefault(tick.Provider) + tick.Weight;
        return BuildEvent(Math.Min(_completed, _totalWeight), BuildSegments(saturated: false));
    }

    /// <summary>
    ///     Returns a final 100% event if providers reported fewer ticks than expected
    ///     (e.g. cache hits), or <c>null</c> if the bar is already full.
    /// </summary>
    public string? BuildFinalEventIfIncomplete()
    {
        if (_completed >= _totalWeight)
        {
            return null;
        }

        return BuildEvent(_totalWeight, BuildSegments(saturated: true));
    }

    /// <summary>
    ///     Exponential backoff for the SSE sync loop on consecutive provider faults:
    ///     5 s → 10 s → 20 s → 40 s → capped at 60 s.
    /// </summary>
    public static int ComputeErrorBackoffMs(int consecutiveErrors)
    {
        if (consecutiveErrors <= 0)
        {
            return 0;
        }

        return (int)Math.Min(5000 * Math.Pow(2, consecutiveErrors - 1), 60_000);
    }

    private ProgressSegment[] BuildSegments(bool saturated)
    {
        return _providerMeta
            .OrderBy(kv => kv.Value.Total)
            .Select(kv => new ProgressSegment(
                kv.Key, kv.Value.Color,
                saturated ? kv.Value.Total : Math.Min(_providerCompleted.GetValueOrDefault(kv.Key), kv.Value.Total),
                kv.Value.Total))
            .ToArray();
    }

    private string BuildEvent(int completed, ProgressSegment[] segments)
    {
        ProgressEvent evt = new(completed, _totalWeight, segments.Length > 0 ? segments : null);
        string json = JsonSerializer.Serialize(evt, WatchBackJsonContext.Default.ProgressEvent);
        return $"data: {json}\n\n";
    }
}

public sealed record ProgressSegment(string Provider, string Color, int Completed, int Total);

public sealed record ProgressEvent(int Completed, int Total, ProgressSegment[]? Providers);