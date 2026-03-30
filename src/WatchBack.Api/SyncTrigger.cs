namespace WatchBack.Api;

/// <summary>
///     Allows the manual-sync endpoint to wake the SSE polling loop early,
///     so a button press skips the 5-second wait and shows progress immediately.
/// </summary>
public sealed class SyncTrigger
{
    private volatile TaskCompletionSource _pending =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Wake any SSE loop currently waiting on <see cref="WaitAsync" />.</summary>
    public void Signal()
    {
        Interlocked.Exchange(
                ref _pending,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();
    }

    /// <summary>
    ///     Completes when <see cref="Signal" /> is called or <paramref name="ct" /> is cancelled.
    /// </summary>
    public Task WaitAsync(CancellationToken ct)
    {
        return _pending.Task.WaitAsync(ct);
    }
}
