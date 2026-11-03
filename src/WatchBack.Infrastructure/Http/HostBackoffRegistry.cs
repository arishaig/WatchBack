using System.Collections.Concurrent;

namespace WatchBack.Infrastructure.Http;

/// <summary>
///     Process-wide record of hosts that told us to back off (HTTP 429), and until when.
///     Shared by every typed client so one provider's 429 also stops another provider
///     calling the same host.
/// </summary>
public sealed class HostBackoffRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);

    public TimeProvider Time => time;

    /// <summary>Raised after a host's backoff window is set or extended.</summary>
    public event Action<string, DateTimeOffset>? BackoffSet;

    /// <summary>Returns true while <paramref name="host" /> is inside a backoff window.</summary>
    public bool IsSuppressed(string host, out DateTimeOffset until)
    {
        if (_until.TryGetValue(host, out until) && until > time.GetUtcNow())
        {
            return true;
        }

        _until.TryRemove(new KeyValuePair<string, DateTimeOffset>(host, until));
        until = default;
        return false;
    }

    /// <summary>
    ///     Suppresses <paramref name="host" /> until <paramref name="until" />. Never shortens
    ///     an existing window: the server's longest request wins.
    /// </summary>
    public void Suppress(string host, DateTimeOffset until)
    {
        DateTimeOffset effective = _until.AddOrUpdate(host, until, (_, existing) => existing > until ? existing : until);
        BackoffSet?.Invoke(host, effective);
    }

    /// <summary>Restores windows saved before a restart, ignoring any that already expired.</summary>
    public void Restore(IEnumerable<KeyValuePair<string, DateTimeOffset>> windows)
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach ((string host, DateTimeOffset until) in windows)
        {
            if (until > now)
            {
                _until.AddOrUpdate(host, until, (_, existing) => existing > until ? existing : until);
            }
        }
    }
}
