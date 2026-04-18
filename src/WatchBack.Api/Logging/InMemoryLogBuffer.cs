using System.Threading.Channels;

namespace WatchBack.Api.Logging;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? ExceptionText);

/// <summary>
///     Thread-safe circular buffer that stores recent log entries and broadcasts them to live SSE subscribers.
/// </summary>
public sealed class InMemoryLogBuffer
{
    private const int Capacity = 500;

    private static readonly string[] s_levelOrder = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    private readonly Queue<LogEntry> _entries = new(Capacity + 1);
    private readonly Lock _lock = new();
    private readonly List<ChannelWriter<LogEntry>> _subscribers = [];

    public void Add(LogEntry entry)
    {
        List<ChannelWriter<LogEntry>> subscribers;
        lock (_lock)
        {
            if (_entries.Count >= Capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
            subscribers = [.. _subscribers];
        }

        foreach (ChannelWriter<LogEntry> writer in subscribers)
        {
            writer.TryWrite(entry);
        }
    }

    public IReadOnlyList<LogEntry> GetEntries(string? minLevel = null, int limit = 200)
    {
        lock (_lock)
        {
            IEnumerable<LogEntry> source = _entries;
            if (minLevel != null)
            {
                source = source.Where(e => IsAtOrAbove(e.Level, minLevel));
            }

            return source.TakeLast(limit).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    /// <summary>Subscribe to new log entries. Dispose the returned handle to unsubscribe.</summary>
    public IDisposable Subscribe(out ChannelReader<LogEntry> reader)
    {
        Channel<LogEntry> channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_lock)
        {
            _subscribers.Add(channel.Writer);
        }

        reader = channel.Reader;
        return new Subscription(this, channel.Writer);
    }

    private void Unsubscribe(ChannelWriter<LogEntry> writer)
    {
        lock (_lock)
        {
            _subscribers.Remove(writer);
        }

        writer.TryComplete();
    }

    private static bool IsAtOrAbove(string level, string minLevel)
    {
        int li = Array.IndexOf(s_levelOrder, level);
        int mi = Array.IndexOf(s_levelOrder, minLevel);
        return li >= 0 && li >= mi;
    }

    private sealed class Subscription(InMemoryLogBuffer buffer, ChannelWriter<LogEntry> writer) : IDisposable
    {
        public void Dispose()
        {
            buffer.Unsubscribe(writer);
        }
    }
}

internal sealed class InMemoryLogger(string category, InMemoryLogBuffer buffer) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.None)
        {
            return;
        }

        string level = logLevel switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Information",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Critical",
            _ => logLevel.ToString()
        };

        string cat = category.Contains('.')
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

        buffer.Add(new LogEntry(
            DateTimeOffset.UtcNow,
            level,
            cat,
            formatter(state, exception),
            exception == null ? null : $"{exception.GetType().Name}: {exception.Message}"));
    }
}

[ProviderAlias("InMemory")]
public sealed class InMemoryLoggerProvider(InMemoryLogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new InMemoryLogger(categoryName, buffer);
    }

    public void Dispose() { }
}
