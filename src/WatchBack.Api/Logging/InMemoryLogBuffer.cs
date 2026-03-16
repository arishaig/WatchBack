using System.Threading.Channels;

using Microsoft.Extensions.Logging;

namespace WatchBack.Api.Logging;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? ExceptionText);

/// <summary>
/// Thread-safe circular buffer that stores recent log entries and broadcasts them to live SSE subscribers.
/// </summary>
public sealed class InMemoryLogBuffer
{
    private const int Capacity = 500;

    private readonly Queue<LogEntry> _entries = new(Capacity + 1);
    private readonly object _lock = new();
    private readonly List<ChannelWriter<LogEntry>> _subscribers = [];

    public void Add(LogEntry entry)
    {
        List<ChannelWriter<LogEntry>> subscribers;
        lock (_lock)
        {
            if (_entries.Count >= Capacity)
                _entries.Dequeue();
            _entries.Enqueue(entry);
            subscribers = [.._subscribers];
        }

        foreach (var writer in subscribers)
            writer.TryWrite(entry);
    }

    public IReadOnlyList<LogEntry> GetEntries(string? minLevel = null, int limit = 200)
    {
        lock (_lock)
        {
            IEnumerable<LogEntry> source = _entries;
            if (minLevel != null)
                source = source.Where(e => IsAtOrAbove(e.Level, minLevel));
            return source.TakeLast(limit).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }

    /// <summary>Subscribe to new log entries. Dispose the returned handle to unsubscribe.</summary>
    public IDisposable Subscribe(out ChannelReader<LogEntry> reader)
    {
        var channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_lock)
            _subscribers.Add(channel.Writer);
        reader = channel.Reader;
        return new Subscription(this, channel.Writer);
    }

    private void Unsubscribe(ChannelWriter<LogEntry> writer)
    {
        lock (_lock)
            _subscribers.Remove(writer);
        writer.TryComplete();
    }

    private sealed class Subscription(InMemoryLogBuffer buffer, ChannelWriter<LogEntry> writer) : IDisposable
    {
        public void Dispose() => buffer.Unsubscribe(writer);
    }

    private static readonly string[] LevelOrder = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    private static bool IsAtOrAbove(string level, string minLevel)
    {
        var li = Array.IndexOf(LevelOrder, level);
        var mi = Array.IndexOf(LevelOrder, minLevel);
        return li >= 0 && li >= mi;
    }
}

internal sealed class InMemoryLogger(string category, InMemoryLogBuffer buffer) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.None) return;

        var level = logLevel switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Information",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Critical",
            _ => logLevel.ToString()
        };

        // Shorten "WatchBack.Infrastructure.Http.ResilientHttpHandler" → "ResilientHttpHandler"
        var cat = category.Contains('.')
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

        buffer.Add(new LogEntry(
            Timestamp: DateTimeOffset.UtcNow,
            Level: level,
            Category: cat,
            Message: formatter(state, exception),
            ExceptionText: exception == null ? null : $"{exception.GetType().Name}: {exception.Message}"));
    }
}

[ProviderAlias("InMemory")]
public sealed class InMemoryLoggerProvider(InMemoryLogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, buffer);

    public void Dispose() { }
}
