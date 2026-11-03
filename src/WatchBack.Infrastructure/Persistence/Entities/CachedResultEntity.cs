namespace WatchBack.Infrastructure.Persistence.Entities;

/// <summary>
///     A provider result (or a host's rate-limit window) kept across restarts.
///     Times are Unix milliseconds so SQLite can compare them in queries.
/// </summary>
public class CachedResultEntity
{
    /// <summary>Cache key, unique across kinds.</summary>
    public required string Key { get; set; }

    /// <summary>What <see cref="Payload" /> holds: <c>thoughts</c>, <c>ratings</c> or <c>backoff</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>JSON-serialised result; empty for a backoff window.</summary>
    public required string Payload { get; set; }

    public long FetchedAtUnixMs { get; set; }

    public long ExpiresAtUnixMs { get; set; }
}
