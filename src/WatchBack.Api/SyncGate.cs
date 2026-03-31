namespace WatchBack.Api;

/// <summary>
///     Limits concurrent <c>SyncAsync</c> calls to one at a time across all SSE
///     clients. Because provider results are cached in <c>IMemoryCache</c>, the
///     second sync through after a cache-warming call is essentially free.
/// </summary>
public sealed class SyncGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> factory, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await factory();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
