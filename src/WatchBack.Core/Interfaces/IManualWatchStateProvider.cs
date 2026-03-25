using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// Interface for the manual watch state provider.
/// Allows SyncService (Core) to identify and prefer the manual provider
/// without taking a direct dependency on the Infrastructure implementation.
/// </summary>
public interface IManualWatchStateProvider : IWatchStateProvider
{
    /// <summary>
    /// Sets the current media context. Pass <c>null</c> to clear.
    /// </summary>
    void SetCurrentContext(MediaContext? context);
}
