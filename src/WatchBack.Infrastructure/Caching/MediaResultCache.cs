using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using static WatchBack.Core.Models.ExternalIdType;

namespace WatchBack.Infrastructure.Caching;

/// <summary>
///     Fetch-once cache in front of thought and ratings providers (see <see cref="IMediaResultCache" />).
///     <list type="bullet">
///         <item>Memory first, then SQLite, then upstream.</item>
///         <item>One fetch at a time per key: concurrent viewers, polls and prefetch all await the same one.</item>
///         <item>
///             The shared fetch runs in its own DI scope and is cancelled only by shutdown, so a viewer
///             disconnecting mid-fetch neither cancels it for the others nor disposes its HttpClient.
///         </item>
///         <item>Lifetimes come from <see cref="CacheTtlPolicy" />; failures stay in memory for a minute and are never persisted.</item>
///         <item>
///             Lookups without an air date (prefetch predictions) are cached apart from dated ones and never
///             persisted: an undated search is weaker, so its miss must not stand in for a real lookup.
///         </item>
///     </list>
/// </summary>
public sealed class MediaResultCache(
    IMemoryCache memory,
    ResultCacheStore store,
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<MediaResultCache> logger,
    IHostApplicationLifetime? lifetime = null) : IMediaResultCache
{
    private const string ThoughtsKind = "thoughts";
    private const string RatingsKind = "ratings";

    // Empty ratings can't be told apart from a failed OMDb call, so they are neither persisted nor kept long.
    private static readonly TimeSpan s_emptyRatingsTtl = TimeSpan.FromMinutes(10);

    // A manual refresh refetches from every provider; repeated presses within this window reuse the first.
    private static readonly TimeSpan s_refreshCooldown = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Task> _inFlight = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRefresh = new();

    private sealed record MemoryEntry<T>(T Value, DateTimeOffset ExpiresAt);

    // ── Thoughts ─────────────────────────────────────────────────────────────

    public async Task<ThoughtResult?> GetThoughtsAsync(IThoughtProvider provider, MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        // An unconfigured provider makes no request, and caching its empty answer would hide results
        // for days after the user configures it.
        if (!provider.IsConfigured)
        {
            return await provider.GetThoughtsAsync(mediaContext, progress, ct);
        }

        string key = ThoughtKey(provider, mediaContext);
        if (TryGetMemory(key, out ThoughtResult? hit))
        {
            return hit;
        }

        return await SingleFlightAsync(key, fetchCt => LoadThoughtsAsync(provider, mediaContext, key, progress, fetchCt), ct);
    }

    private async Task<ThoughtResult?> LoadThoughtsAsync(IThoughtProvider provider, MediaContext mediaContext,
        string key, IProgress<SyncProgressTick>? progress, CancellationToken ct)
    {
        // A fetch that finished between the caller's memory check and joining may have just filled it.
        if (TryGetMemory(key, out ThoughtResult? justFetched))
        {
            return justFetched;
        }

        if (mediaContext.ReleaseDate is not null && await store.GetAsync(key, ct) is { } row)
        {
            try
            {
                ThoughtResult? stored = JsonSerializer.Deserialize(row.Payload, CacheJsonContext.Default.ThoughtResult);
                SetMemory(key, stored, row.ExpiresAt);
                return stored;
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Result cache: discarding unreadable entry {Key}", key);
            }
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IThoughtProvider target = InScope(scope, provider);
        ThoughtResult? result = await target.GetThoughtsAsync(mediaContext, progress, ct);

        DateTimeOffset now = time.GetUtcNow();
        if (result is { IsTransientFailure: true })
        {
            SetMemory(key, result, now + CacheTtlPolicy.Failure);
            return result;
        }

        bool found = result?.Thoughts is { Count: > 0 };
        TimeSpan ttl = found
            ? CacheTtlPolicy.ForHit(mediaContext.ReleaseDate, now, provider.MaxCacheAge)
            : CacheTtlPolicy.ForMiss(mediaContext.ReleaseDate, now, provider.MaxCacheAge);
        SetMemory(key, result, now + ttl);

        if (mediaContext.ReleaseDate is not null)
        {
            await store.SetAsync(key, ThoughtsKind,
                JsonSerializer.Serialize(result, CacheJsonContext.Default.ThoughtResult), now + ttl, ct);
        }

        return result;
    }

    // ── Ratings ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MediaRating>> GetRatingsAsync(IRatingsProvider provider, MediaContext mediaContext,
        string imdbId, CancellationToken ct = default)
    {
        if (!provider.IsConfigured)
        {
            return await provider.GetRatingsAsync(imdbId, ct);
        }

        string key = RatingsKey(provider, imdbId);
        if (TryGetMemory(key, out IReadOnlyList<MediaRating>? hit) && hit is not null)
        {
            return hit;
        }

        return await SingleFlightAsync(key, fetchCt => LoadRatingsAsync(provider, mediaContext, imdbId, key, fetchCt), ct);
    }

    private async Task<IReadOnlyList<MediaRating>> LoadRatingsAsync(IRatingsProvider provider,
        MediaContext mediaContext, string imdbId, string key, CancellationToken ct)
    {
        if (TryGetMemory(key, out IReadOnlyList<MediaRating>? justFetched) && justFetched is not null)
        {
            return justFetched;
        }

        if (await store.GetAsync(key, ct) is { } row)
        {
            try
            {
                List<MediaRating>? stored = JsonSerializer.Deserialize(row.Payload, CacheJsonContext.Default.ListMediaRating);
                if (stored is { Count: > 0 })
                {
                    SetMemory<IReadOnlyList<MediaRating>>(key, stored, row.ExpiresAt);
                    return stored;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Result cache: discarding unreadable entry {Key}", key);
            }
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IRatingsProvider target = InScope(scope, provider);
        IReadOnlyList<MediaRating> ratings = await target.GetRatingsAsync(imdbId, ct);

        DateTimeOffset now = time.GetUtcNow();
        if (ratings.Count == 0)
        {
            SetMemory(key, ratings, now + s_emptyRatingsTtl);
            return ratings;
        }

        DateTimeOffset expiresAt = now + CacheTtlPolicy.ForHit(mediaContext.ReleaseDate, now);
        SetMemory(key, ratings, expiresAt);
        if (mediaContext.ReleaseDate is not null)
        {
            await store.SetAsync(key, RatingsKind,
                JsonSerializer.Serialize(ratings.ToList(), CacheJsonContext.Default.ListMediaRating), expiresAt, ct);
        }

        return ratings;
    }

    // ── Eviction ─────────────────────────────────────────────────────────────

    public void Evict(IThoughtProvider provider, MediaContext mediaContext)
    {
        memory.Remove(provider.GetCacheKey(mediaContext));
        memory.Remove(ThoughtKey(provider, mediaContext));
    }

    public async Task<bool> InvalidateAsync(IEnumerable<IThoughtProvider> providers,
        IEnumerable<IRatingsProvider> ratingsProviders, MediaContext mediaContext, CancellationToken ct = default)
    {
        DateTimeOffset now = time.GetUtcNow();
        string refreshKey = mediaContext is EpisodeContext e
            ? $"{e.Title}|{e.SeasonNumber}|{e.EpisodeNumber}|{e.ReleaseDate:O}"
            : $"{mediaContext.Title}|{mediaContext.ReleaseDate:O}";
        if (_lastRefresh.TryGetValue(refreshKey, out DateTimeOffset last) && now - last < s_refreshCooldown)
        {
            return false;
        }

        _lastRefresh[refreshKey] = now;
        foreach ((string key, DateTimeOffset at) in _lastRefresh)
        {
            if (now - at >= s_refreshCooldown)
            {
                _lastRefresh.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, at));
            }
        }

        List<string> keys = [];
        foreach (IThoughtProvider provider in providers)
        {
            Evict(provider, mediaContext);
            keys.Add(ThoughtKey(provider, mediaContext));
        }

        if (mediaContext.ExternalIds?.TryGetValue(Imdb, out string? imdbId) == true && !string.IsNullOrEmpty(imdbId))
        {
            foreach (IRatingsProvider provider in ratingsProviders)
            {
                string key = RatingsKey(provider, imdbId);
                memory.Remove(key);
                keys.Add(key);
            }
        }

        await store.RemoveAsync(keys, ct);
        logger.LogInformation("Result cache: cleared {Count} entr(ies) for {Title} on request", keys.Count,
            mediaContext.Title);
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // The provider's own key already names the provider and the media item. Undated lookups are
    // weaker (see class remarks), so they never share an entry with dated ones.
    internal static string ThoughtKey(IThoughtProvider provider, MediaContext mediaContext) =>
        $"results:{provider.GetCacheKey(mediaContext)}:{(mediaContext.ReleaseDate is null ? "undated" : "dated")}";

    private static string RatingsKey(IRatingsProvider provider, string imdbId) =>
        $"results:ratings:{provider.Metadata.Name}:{imdbId}";

    // The same provider resolved in the fetch's own scope, so the caller's scope ending (a viewer
    // disconnecting) can't dispose its HttpClient mid-fetch. Falls back to the caller's instance when it
    // is itself registered as-is (a singleton) or isn't registered at all.
    private static TProvider InScope<TProvider>(AsyncServiceScope scope, TProvider provider)
        where TProvider : class, IDataProvider
    {
        TProvider[] registered = scope.ServiceProvider.GetServices<TProvider>().ToArray();
        return registered.Contains(provider)
            ? provider
            : registered.FirstOrDefault(p => p.GetType() == provider.GetType()) ?? provider;
    }

    private bool TryGetMemory<T>(string key, out T? value)
    {
        if (memory.TryGetValue(key, out MemoryEntry<T>? entry) && entry is not null &&
            entry.ExpiresAt > time.GetUtcNow())
        {
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    private void SetMemory<T>(string key, T value, DateTimeOffset expiresAt)
    {
        TimeSpan ttl = expiresAt - time.GetUtcNow();
        if (ttl > TimeSpan.Zero)
        {
            memory.Set(key, new MemoryEntry<T>(value, expiresAt), ttl);
        }
    }

    private async Task<T> SingleFlightAsync<T>(string key, Func<CancellationToken, Task<T>> load,
        CancellationToken ct)
    {
        TaskCompletionSource<T> mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task shared = _inFlight.GetOrAdd(key, mine.Task);
        if (shared == mine.Task)
        {
            _ = RunSharedAsync(key, mine, load);
        }

        // Each caller waits with its own token; leaving early doesn't cancel the fetch for anyone else.
        return await ((Task<T>)shared).WaitAsync(ct);
    }

    private async Task RunSharedAsync<T>(string key, TaskCompletionSource<T> tcs,
        Func<CancellationToken, Task<T>> load)
    {
        try
        {
            tcs.SetResult(await load(lifetime?.ApplicationStopping ?? CancellationToken.None));
        }
        catch (OperationCanceledException ex)
        {
            tcs.SetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task>(key, tcs.Task));
        }
    }
}
