using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using WatchBack.Infrastructure.Persistence;
using WatchBack.Infrastructure.Persistence.Entities;

namespace WatchBack.Infrastructure.Caching;

/// <summary>
///     SQLite-backed storage for <see cref="MediaResultCache" /> entries and host backoff windows,
///     so an old episode costs no upstream calls after a restart and a long Retry-After survives one.
///     Storage failures are logged and swallowed: the cache degrades to memory-only, it never breaks a sync.
/// </summary>
public sealed class ResultCacheStore(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<ResultCacheStore> logger) : IDisposable
{
    // Production runs SQLite in DELETE journal mode (NFS), where concurrent writers hit "database is locked".
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public sealed record Entry(string Payload, DateTimeOffset ExpiresAt);

    public void Dispose() => _writeGate.Dispose();

    public async Task<Entry?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            WatchBackDbContext db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
            long now = time.GetUtcNow().ToUnixTimeMilliseconds();
            CachedResultEntity? row = await db.CachedResults.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Key == key && e.ExpiresAtUnixMs > now, ct);
            return row is null
                ? null
                : new Entry(row.Payload, DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresAtUnixMs));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Result cache: read failed for {Key}", key);
            return null;
        }
    }

    public async Task SetAsync(string key, string kind, string payload, DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            WatchBackDbContext db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
            CachedResultEntity? row = await db.CachedResults.FirstOrDefaultAsync(e => e.Key == key, ct);
            if (row is null)
            {
                row = new CachedResultEntity { Key = key, Kind = kind, Payload = payload };
                db.CachedResults.Add(row);
            }

            row.Kind = kind;
            row.Payload = payload;
            row.FetchedAtUnixMs = time.GetUtcNow().ToUnixTimeMilliseconds();
            row.ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Result cache: write failed for {Key}", key);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        string[] keyList = keys.ToArray();
        await _writeGate.WaitAsync(ct);
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            WatchBackDbContext db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
            await db.CachedResults.Where(e => keyList.Contains(e.Key)).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Result cache: delete failed");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>All unexpired rows of <paramref name="kind" />, e.g. backoff windows to restore at startup.</summary>
    public async Task<IReadOnlyList<KeyValuePair<string, DateTimeOffset>>> GetLiveAsync(string kind,
        CancellationToken ct = default)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            WatchBackDbContext db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
            long now = time.GetUtcNow().ToUnixTimeMilliseconds();
            List<CachedResultEntity> rows = await db.CachedResults.AsNoTracking()
                .Where(e => e.Kind == kind && e.ExpiresAtUnixMs > now)
                .ToListAsync(ct);
            return rows
                .Select(r => new KeyValuePair<string, DateTimeOffset>(r.Key,
                    DateTimeOffset.FromUnixTimeMilliseconds(r.ExpiresAtUnixMs)))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Result cache: read of {Kind} rows failed", kind);
            return [];
        }
    }

    /// <summary>Deletes expired rows. Returns how many were removed.</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            WatchBackDbContext db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
            long now = time.GetUtcNow().ToUnixTimeMilliseconds();
            return await db.CachedResults.Where(e => e.ExpiresAtUnixMs <= now).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Result cache: prune failed");
            return 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
