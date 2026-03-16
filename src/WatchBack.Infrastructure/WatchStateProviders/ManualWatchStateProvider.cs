using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using static WatchBack.Core.Models.ExternalIdType;

namespace WatchBack.Infrastructure.WatchStateProviders;

/// <summary>
/// A watch state provider whose context is set explicitly via the API rather than polled
/// from an external service. Registered as a singleton so the stored context survives
/// across requests for the lifetime of the application.
/// </summary>
public class ManualWatchStateProvider : IManualWatchStateProvider
{
    private volatile MediaContext? _context;

    public DataProviderMetadata Metadata =>
        new WatchStateDataProviderMetadata(
            Name: "Manual",
            Description: "Manually set watch state",
            BrandData: new BrandData(
                Color: "#64748B",
                LogoSvg: "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Manual</title><path d=\"M3 17.25V21h3.75L17.81 9.94l-3.75-3.75zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75z\"/></svg>"
            )
        )
        {
            SupportedExternalIds = new HashSet<string> { Imdb, Tmdb, Tvdb }
        };

    /// <summary>
    /// Sets the current media context. Pass <c>null</c> to clear.
    /// </summary>
    public void SetCurrentContext(MediaContext? context)
    {
        _context = context;
    }

    public Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default)
        => Task.FromResult(_context);

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        var message = _context != null
            ? $"Watching: {_context.Title}"
            : "Idle — no context set";

        return Task.FromResult(new ServiceHealth(
            IsHealthy: true,
            Message: message,
            CheckedAt: DateTimeOffset.UtcNow));
    }
}
