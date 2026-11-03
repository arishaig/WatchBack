using Microsoft.Extensions.Caching.Memory;

namespace WatchBack.Infrastructure.Caching;

/// <summary>A remembered lookup answer. A null <see cref="Value" /> means "looked, found nothing".</summary>
public readonly record struct LookupAnswer(string? Value);

/// <summary>
///     Remembers small lookup answers (an ID mapping, "none found") in memory and in SQLite, so a
///     lookup that rarely changes is made once rather than once per process or per day.
/// </summary>
public interface IDurableLookupCache
{
    Task<LookupAnswer?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string kind, string? value, TimeSpan lifetime, CancellationToken ct = default);
}

public sealed class DurableLookupCache(ResultCacheStore store, IMemoryCache cache, TimeProvider time)
    : IDurableLookupCache
{
    private static readonly TimeSpan s_memoryLifetime = TimeSpan.FromHours(6);

    public async Task<LookupAnswer?> GetAsync(string key, CancellationToken ct = default)
    {
        if (cache.TryGetValue(key, out LookupAnswer remembered))
        {
            return remembered;
        }

        ResultCacheStore.Entry? stored = await store.GetAsync(key, ct);
        if (stored is null)
        {
            return null;
        }

        LookupAnswer answer = new(stored.Payload.Length == 0 ? null : stored.Payload);
        cache.Set(key, answer, Shorter(stored.ExpiresAt - time.GetUtcNow()));
        return answer;
    }

    public async Task SetAsync(string key, string kind, string? value, TimeSpan lifetime,
        CancellationToken ct = default)
    {
        cache.Set(key, new LookupAnswer(value), Shorter(lifetime));
        await store.SetAsync(key, kind, value ?? "", time.GetUtcNow() + lifetime, ct);
    }

    private static TimeSpan Shorter(TimeSpan lifetime) =>
        lifetime > TimeSpan.Zero && lifetime < s_memoryLifetime ? lifetime : s_memoryLifetime;
}
