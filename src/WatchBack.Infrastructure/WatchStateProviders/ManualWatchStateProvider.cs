using System.Globalization;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Resources;

using static WatchBack.Core.Models.ExternalIdType;

namespace WatchBack.Infrastructure.WatchStateProviders;

/// <summary>
/// A watch state provider whose context is set explicitly via the API rather than polled
/// from an external service. Registered as a singleton so the stored context survives
/// across requests for the lifetime of the application.
/// </summary>
public sealed class ManualWatchStateProvider : IManualWatchStateProvider
{
    private MediaContext? _context;

    public DataProviderMetadata Metadata => new WatchStateDataProviderMetadata(
        Name: "Manual",
        Description: UiStrings.ManualWatchStateProvider_Metadata_Manually_set_watch_state,
        BrandData: new BrandData(
            Color: "#64748B",
            LogoSvg:
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Manual</title><path d=\"M3 17.25V21h3.75L17.81 9.94l-3.75-3.75zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75z\"/></svg>"
        )
    )
    { SupportedExternalIds = new HashSet<string> { Imdb, Tmdb, Tvdb }, RequiresManualInput = true };

    /// <summary>
    /// Sets the current media context. Pass <c>null</c> to clear.
    /// </summary>
    public void SetCurrentContext(MediaContext? context)
    {
        Interlocked.Exchange(ref _context, context);
    }

    public Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default) =>
        Task.FromResult(Volatile.Read(ref _context));

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        var message = _context != null
#pragma warning disable CA1863
            ? string.Format(
                CultureInfo.InvariantCulture,
                UiStrings.ManualWatchStateProvide_GetServiceHealthAsync_MediaContext,
                _context.Title)
#pragma warning restore CA1863
            : UiStrings.ManualWatchStateProvide_GetServiceHealthAsync_IdleState;

        return Task.FromResult(new ServiceHealth(
            IsHealthy: true,
            Message: message,
            CheckedAt: DateTimeOffset.UtcNow));
    }
}
