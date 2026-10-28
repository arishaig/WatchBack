using System.Collections.Concurrent;

namespace WatchBack.Api;

/// <summary>
///     Limits concurrent <c>SyncAsync</c> calls to one at a time <i>per viewer</i> (see <c>WatchViewer</c>),
///     so several browsers of the same viewer don't pile up while different viewers never wait on each other.
///     Because provider results are cached in <c>IMemoryCache</c>, the second sync through after a
///     cache-warming call is essentially free.
/// </summary>
public sealed class SyncGate : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> factory, CancellationToken ct, string? viewerKey = null)
    {
        SemaphoreSlim semaphore = _semaphores.GetOrAdd(viewerKey ?? "", _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            return await factory();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public void Dispose()
    {
        foreach (SemaphoreSlim semaphore in _semaphores.Values)
        {
            semaphore.Dispose();
        }
    }
}
