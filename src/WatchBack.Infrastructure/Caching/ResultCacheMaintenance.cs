using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using WatchBack.Infrastructure.Http;

namespace WatchBack.Infrastructure.Caching;

/// <summary>
///     Keeps the persisted result cache tidy and carries host backoff windows across restarts:
///     restores unexpired windows at startup, saves each new one, and prunes expired rows every few hours.
/// </summary>
public sealed class ResultCacheMaintenance(
    ResultCacheStore store,
    HostBackoffRegistry backoff,
    TimeProvider time,
    ILogger<ResultCacheMaintenance> logger) : BackgroundService
{
    private const string BackoffKind = "backoff";
    private const string BackoffKeyPrefix = "backoff:";
    private static readonly TimeSpan s_pruneInterval = TimeSpan.FromHours(6);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Restore before any request can be made, so a restart never calls a host early.
        IReadOnlyList<KeyValuePair<string, DateTimeOffset>> windows = await store.GetLiveAsync(BackoffKind, cancellationToken);
        backoff.Restore(windows.Select(w =>
            new KeyValuePair<string, DateTimeOffset>(w.Key[BackoffKeyPrefix.Length..], w.Value)));
        if (windows.Count > 0)
        {
            logger.LogInformation("Restored {Count} rate-limit backoff window(s) from before the restart", windows.Count);
        }

        backoff.BackoffSet += OnBackoffSet;
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        backoff.BackoffSet -= OnBackoffSet;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(s_pruneInterval, time);
        do
        {
            int removed = await store.PruneAsync(stoppingToken);
            if (removed > 0)
            {
                logger.LogDebug("Result cache: pruned {Count} expired entr(ies)", removed);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void OnBackoffSet(string host, DateTimeOffset until) =>
        _ = store.SetAsync(BackoffKeyPrefix + host, BackoffKind, "", until);
}
