using System.Collections.Concurrent;
using System.Globalization;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Resources;

using static WatchBack.Core.Models.ExternalIdType;

namespace WatchBack.Infrastructure.WatchStateProviders;

/// <summary>
///     A watch state provider whose context is set explicitly via the API rather than polled
///     from an external service. Registered as a singleton so the stored context survives
///     across requests for the lifetime of the application.
/// </summary>
public sealed class ManualWatchStateProvider : IManualWatchStateProvider
{
    // Lock-free by design: reference assignment is atomic in .NET on all supported
    // architectures, so no lock is needed for correctness. Interlocked.Exchange ensures
    // the write is visible to other threads (full memory fence), and Volatile.Read
    // ensures reads always see the most recently written value without tearing or
    // stale-cache issues. A lock would be heavier and is unnecessary here.
    private MediaContext? _context;

    // Per-browser contexts, keyed by the client id the browser generated. Bounded so an
    // endless stream of fresh ids cannot grow memory without limit; the stalest entry goes first.
    private const int MaxClients = 200;
    private readonly ConcurrentDictionary<string, (MediaContext Context, long Stamp)> _clientContexts = new();
    private long _stamp;

    public DataProviderMetadata Metadata => new WatchStateDataProviderMetadata(
        "Manual",
        UiStrings.ManualWatchStateProvider_Metadata_Manually_set_watch_state,
        BrandData: new BrandData(
            "#64748B",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Manual</title><path d=\"M3 17.25V21h3.75L17.81 9.94l-3.75-3.75zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75z\"/></svg>"
        )
    )
    { SupportedExternalIds = new HashSet<string> { Imdb, Tmdb, Tvdb }, RequiresManualInput = true };

    /// <summary>
    ///     Sets the current media context. Pass <c>null</c> to clear.
    /// </summary>
    public void SetCurrentContext(MediaContext? context)
    {
        Interlocked.Exchange(ref _context, context);
    }

    public void SetCurrentContext(string? viewerKey, MediaContext? context)
    {
        string? id = ValidKey(viewerKey);
        if (id is null)
        {
            SetCurrentContext(context);
            return;
        }

        if (context is null)
        {
            _clientContexts.TryRemove(id, out _);
            return;
        }

        _clientContexts[id] = (context, Interlocked.Increment(ref _stamp));
        EvictStalest();
    }

    public Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default)
    {
        return Task.FromResult(Volatile.Read(ref _context));
    }

    public Task<MediaContext?> GetCurrentMediaContextForClientAsync(string? viewerKey, CancellationToken ct = default)
    {
        string? id = ValidKey(viewerKey);
        return id is not null && _clientContexts.TryGetValue(id, out (MediaContext Context, long Stamp) entry)
            ? Task.FromResult<MediaContext?>(entry.Context)
            : GetCurrentMediaContextAsync(ct);
    }

    // Keys come from WatchViewer.Key, so they are already validated; this only guards direct callers.
    private static string? ValidKey(string? viewerKey)
    {
        return viewerKey is { Length: > 0 and <= 80 } ? viewerKey : null;
    }

    private void EvictStalest()
    {
        while (_clientContexts.Count > MaxClients)
        {
            string? oldest = _clientContexts.MinBy(kv => kv.Value.Stamp).Key;
            if (oldest is null || !_clientContexts.TryRemove(oldest, out _))
            {
                return;
            }
        }
    }

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        MediaContext? context = Volatile.Read(ref _context);
        string? message = context != null
#pragma warning disable CA1863
            ? string.Format(
                CultureInfo.InvariantCulture,
                UiStrings.ManualWatchStateProvide_GetServiceHealthAsync_MediaContext,
                context.Title)
#pragma warning restore CA1863
            : UiStrings.ManualWatchStateProvide_GetServiceHealthAsync_IdleState;

        return Task.FromResult(new ServiceHealth(
            true,
            message,
            DateTimeOffset.UtcNow));
    }
}
