using FluentAssertions;

using WatchBack.Api;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class SyncTriggerTests
{
    private readonly SyncTrigger _trigger = new();

    [Fact]
    public async Task WaitAsync_CompletesAfterSignal()
    {
        Task wait = _trigger.WaitAsync(CancellationToken.None);

        wait.IsCompleted.Should().BeFalse();

        _trigger.Signal();

        await wait; // should complete promptly
        wait.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Signal_ReplacesCompletionSource_SubsequentWaitDoesNotReturnImmediately()
    {
        // First signal completes any pending wait
        _trigger.Signal();

        // A new wait obtained AFTER the signal should block — Signal atomically
        // replaces the TCS, so the old completed task is gone.
        Task secondWait = _trigger.WaitAsync(CancellationToken.None);

        // Give it a moment; it should still be pending
        await Task.Delay(50);
        secondWait.IsCompleted.Should().BeFalse();

        // Signal again to unblock
        _trigger.Signal();
        await secondWait;
    }

    [Fact]
    public async Task WaitAsync_ThrowsOperationCancelledOnCancellation()
    {
        using CancellationTokenSource cts = new();

        Task wait = _trigger.WaitAsync(cts.Token);

        cts.Cancel();

        Func<Task> act = async () => await wait;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MultipleSignals_EachWakeUpNewWaiter()
    {
        // First cycle
        Task wait1 = _trigger.WaitAsync(CancellationToken.None);
        _trigger.Signal();
        await wait1;

        // Second cycle — shows Signal re-arms correctly
        Task wait2 = _trigger.WaitAsync(CancellationToken.None);
        _trigger.Signal();
        await wait2;

        wait1.IsCompletedSuccessfully.Should().BeTrue();
        wait2.IsCompletedSuccessfully.Should().BeTrue();
    }
}