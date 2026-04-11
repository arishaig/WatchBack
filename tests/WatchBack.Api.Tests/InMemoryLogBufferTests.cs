using FluentAssertions;

using WatchBack.Api.Logging;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class InMemoryLogBufferTests
{
    private static LogEntry MakeEntry(string level = "Information", string message = "test") =>
        new(DateTimeOffset.UtcNow, level, "TestCategory", message, null);

    // ── Capacity eviction ────────────────────────────────────────────────────

    [Fact]
    public void Add_EvictsOldestEntryWhenAtCapacity()
    {
        InMemoryLogBuffer buffer = new();

        for (int i = 0; i < 500; i++)
        {
            buffer.Add(MakeEntry(message: $"msg-{i}"));
        }

        // Use limit > capacity so we get all stored entries, not just the default 200
        buffer.GetEntries(limit: 500).Count.Should().Be(500);
        buffer.GetEntries(limit: 500)[0].Message.Should().Be("msg-0");

        // Adding one more should evict msg-0
        buffer.Add(MakeEntry(message: "msg-500"));

        IReadOnlyList<LogEntry> entries = buffer.GetEntries(limit: 500);
        entries.Count.Should().Be(500);
        entries[0].Message.Should().Be("msg-1");
        entries[^1].Message.Should().Be("msg-500");
    }

    // ── Level filtering ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Warning", new[] { "Warning", "Error", "Critical" })]
    [InlineData("Error", new[] { "Error", "Critical" })]
    [InlineData("Information", new[] { "Information", "Warning", "Error", "Critical" })]
    public void GetEntries_FiltersAtOrAboveMinLevel(string minLevel, string[] expected)
    {
        InMemoryLogBuffer buffer = new();
        foreach (string level in new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" })
        {
            buffer.Add(MakeEntry(level));
        }

        IReadOnlyList<LogEntry> entries = buffer.GetEntries(minLevel);

        entries.Select(e => e.Level).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void GetEntries_ReturnsAllEntriesWhenMinLevelIsNull()
    {
        InMemoryLogBuffer buffer = new();
        buffer.Add(MakeEntry("Trace"));
        buffer.Add(MakeEntry("Critical"));

        buffer.GetEntries(minLevel: null).Count.Should().Be(2);
    }

    // ── Subscriber lifecycle ─────────────────────────────────────────────────

    [Fact]
    public async Task Subscribe_DeliversNewEntriesToSubscriber()
    {
        InMemoryLogBuffer buffer = new();
        using IDisposable sub = buffer.Subscribe(out System.Threading.Channels.ChannelReader<LogEntry> reader);

        LogEntry entry = MakeEntry("Warning", "hello");
        buffer.Add(entry);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        LogEntry received = await reader.ReadAsync(cts.Token);

        received.Message.Should().Be("hello");
        received.Level.Should().Be("Warning");
    }

    [Fact]
    public async Task Subscribe_StopsDeliveryAfterSubscriptionDisposed()
    {
        InMemoryLogBuffer buffer = new();
        IDisposable sub = buffer.Subscribe(out System.Threading.Channels.ChannelReader<LogEntry> reader);

        // Dispose the subscription — channel should be completed
        sub.Dispose();

        buffer.Add(MakeEntry(message: "after-dispose"));

        // Channel should be completed (reader.Completion is done)
        Func<Task> act = async () => await reader.ReadAsync(CancellationToken.None);
        await act.Should().ThrowAsync<System.Threading.Channels.ChannelClosedException>();
    }

    [Fact]
    public async Task Subscribe_MultipleSubscribersEachReceiveEntries()
    {
        InMemoryLogBuffer buffer = new();
        using IDisposable sub1 = buffer.Subscribe(out System.Threading.Channels.ChannelReader<LogEntry> reader1);
        using IDisposable sub2 = buffer.Subscribe(out System.Threading.Channels.ChannelReader<LogEntry> reader2);

        buffer.Add(MakeEntry(message: "broadcast"));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        LogEntry r1 = await reader1.ReadAsync(cts.Token);
        LogEntry r2 = await reader2.ReadAsync(cts.Token);

        r1.Message.Should().Be("broadcast");
        r2.Message.Should().Be("broadcast");
    }

    // ── Clear ────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        InMemoryLogBuffer buffer = new();
        buffer.Add(MakeEntry());
        buffer.Add(MakeEntry());

        buffer.Clear();

        buffer.GetEntries().Should().BeEmpty();
    }
}