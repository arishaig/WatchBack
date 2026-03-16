namespace WatchBack.Core.Interfaces;

/// <summary>
/// Marker interface for the manual watch state provider.
/// Allows SyncService (Core) to identify and prefer the manual provider
/// without taking a direct dependency on the Infrastructure implementation.
/// </summary>
public interface IManualWatchStateProvider : IWatchStateProvider { }
