using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// The service that returns a consolidated collection of
/// data from various DataProviders and other sources in
/// a format that is easily parsable by the UI
/// </summary>
public interface ISyncService
{
    Task<SyncResult> SyncAsync(IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default);
}