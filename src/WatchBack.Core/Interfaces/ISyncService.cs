using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

public interface ISyncService
{
    Task<SyncResult> SyncAsync(CancellationToken ct = default);
}
