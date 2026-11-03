namespace WatchBack.Infrastructure.Caching;

/// <summary>
///     How long a provider result stays fresh, by how long ago the media aired. Discussion of an
///     old episode has stopped changing; a new one's scores and comment counts settle over ~48 h.
/// </summary>
public static class CacheTtlPolicy
{
    private static readonly TimeSpan s_settled = TimeSpan.FromDays(30);
    private static readonly TimeSpan s_fresh = TimeSpan.FromDays(2);

    /// <summary>Lifetime for a result that found something.</summary>
    public static TimeSpan ForHit(DateTimeOffset? releaseDate, DateTimeOffset now, TimeSpan? providerCap = null) =>
        Cap(Age(releaseDate, now) switch
        {
            null => TimeSpan.FromHours(2),
            { } age when age > s_settled => TimeSpan.FromDays(365), // effectively permanent; manual refresh exists
            { } age when age > s_fresh => TimeSpan.FromHours(24),
            _ => TimeSpan.FromHours(2)
        }, providerCap);

    /// <summary>Lifetime for a confirmed miss (the source answered, with nothing).</summary>
    public static TimeSpan ForMiss(DateTimeOffset? releaseDate, DateTimeOffset now, TimeSpan? providerCap = null) =>
        Cap(Age(releaseDate, now) switch
        {
            null => TimeSpan.FromHours(1),
            { } age when age > s_settled => TimeSpan.FromDays(3),
            { } age when age > s_fresh => TimeSpan.FromHours(6),
            _ => TimeSpan.FromHours(1)
        }, providerCap);

    /// <summary>Lifetime for a failed lookup. Kept in memory only, so the poll loop doesn't retry every cycle.</summary>
    public static readonly TimeSpan Failure = TimeSpan.FromSeconds(60);

    private static TimeSpan? Age(DateTimeOffset? releaseDate, DateTimeOffset now) =>
        releaseDate is { } date ? now - date : null;

    private static TimeSpan Cap(TimeSpan ttl, TimeSpan? cap) => cap is { } c && c < ttl ? c : ttl;
}
