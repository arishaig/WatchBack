using FluentAssertions;

using WatchBack.Api;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class SyncGateTests : IDisposable
{
    private readonly SyncGate _gate = new();

    public void Dispose() => _gate.Dispose();

    [Fact]
    public async Task ExecuteAsync_RunsFactoryAndReturnsResult()
    {
        int result = await _gate.ExecuteAsync(() => Task.FromResult(42), CancellationToken.None);

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteAsync_SecondCallBlocksUntilFirstCompletes()
    {
        TaskCompletionSource firstStarted = new();
        TaskCompletionSource firstRelease = new();
        List<string> order = [];

        Task<string> first = _gate.ExecuteAsync(async () =>
        {
            firstStarted.SetResult();
            await firstRelease.Task;
            order.Add("first");
            return "first";
        }, CancellationToken.None);

        await firstStarted.Task; // first is now inside the gate

        Task<string> second = _gate.ExecuteAsync(() =>
        {
            order.Add("second");
            return Task.FromResult("second");
        }, CancellationToken.None);

        // Second should not have run yet while first holds the semaphore
        second.IsCompleted.Should().BeFalse();

        firstRelease.SetResult();
        await Task.WhenAll(first, second);

        order.Should().Equal("first", "second");
    }

    [Fact]
    public async Task ExecuteAsync_CancelledTokenCancelsWait()
    {
        TaskCompletionSource hold = new();

        // Hold the gate open
        Task<int> holder = _gate.ExecuteAsync(async () =>
        {
            await hold.Task;
            return 0;
        }, CancellationToken.None);

        using CancellationTokenSource cts = new();
        Task<int> waiter = _gate.ExecuteAsync(
            () => Task.FromResult(1),
            cts.Token);

        cts.Cancel();

        Func<Task> act = async () => await waiter;
        await act.Should().ThrowAsync<OperationCanceledException>();

        hold.SetResult();
        await holder;
    }
}