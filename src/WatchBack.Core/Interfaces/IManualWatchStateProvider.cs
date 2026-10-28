using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     Interface for the manual watch state provider.
///     Allows SyncService (Core) to identify and prefer the manual provider
///     without taking a direct dependency on the Infrastructure implementation.
/// </summary>
public interface IManualWatchStateProvider : IWatchStateProvider
{
    /// <summary>
    ///     Sets the shared media context seen by every client that has not chosen its own
    ///     (used by the MCP tools). Pass <c>null</c> to clear.
    /// </summary>
    void SetCurrentContext(MediaContext? context);

    /// <summary>
    ///     Sets the media context for one viewer (see <see cref="Models.WatchViewer" />). A null
    ///     <paramref name="viewerKey" /> targets the shared context. Pass a null <paramref name="context" /> to clear.
    /// </summary>
    void SetCurrentContext(string? viewerKey, MediaContext? context);

    /// <summary>
    ///     Returns the viewer's own context if it has one, otherwise the shared context.
    /// </summary>
    Task<MediaContext?> GetCurrentMediaContextForClientAsync(string? viewerKey, CancellationToken ct = default);
}
