using System.Collections.Concurrent;

namespace WatchBack.Api;

/// <summary>
///     Allows the manual-sync endpoint to wake the SSE polling loop early,
///     so a button press skips the 5-second wait and shows progress immediately.
///     Signals are scoped to a viewer (see <c>WatchViewer</c>) so one person pressing Sync does not
///     restart everyone else's cycle; a signal without a key wakes every waiter.
/// </summary>
public sealed class SyncTrigger
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _byViewer = new();

    private volatile TaskCompletionSource _everyone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Wake the SSE loops of <paramref name="viewerKey" />, or all loops when it is null.
    /// </summary>
    public void Signal(string? viewerKey = null)
    {
        if (viewerKey is null)
        {
            Interlocked.Exchange(
                    ref _everyone,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
            return;
        }

        if (_byViewer.TryRemove(viewerKey, out TaskCompletionSource? pending))
        {
            pending.TrySetResult();
        }
    }

    /// <summary>
    ///     Completes when <see cref="Signal" /> is called or <paramref name="ct" /> is cancelled.
    /// </summary>
    public Task WaitAsync(CancellationToken ct)
    {
        return WaitAsync(null, ct);
    }

    /// <summary>
    ///     Completes when this viewer, or everyone, is signalled, or <paramref name="ct" /> is cancelled.
    /// </summary>
    public Task WaitAsync(string? viewerKey, CancellationToken ct)
    {
        Task everyone = _everyone.Task;
        if (viewerKey is null)
        {
            return everyone.WaitAsync(ct);
        }

        Task mine = _byViewer.GetOrAdd(viewerKey,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        return Task.WhenAny(everyone, mine).WaitAsync(ct);
    }
}
