using System.Collections.Concurrent;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Infrastructure.Services;

public sealed class PrefetchService(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    ILogger<PrefetchService> logger,
    IHostApplicationLifetime lifetime)
    : IPrefetchService
{
    // Sentinel stored in IMemoryCache so the prefetch state survives the request scope.
    private const string StateKeyPrefix = "watchback:prefetch:state:";

    // Viewers that currently have prefetch state, so eviction can spare entries another viewer still needs.
    private readonly ConcurrentDictionary<string, byte> _viewers = new();

    private static string StateKey(string? viewerKey) => StateKeyPrefix + (viewerKey ?? "");

    // How long to keep prefetch state tracked (governs stale-entry eviction window).
    private static readonly TimeSpan s_stateTtl = TimeSpan.FromHours(4);

    public void SchedulePrefetch(EpisodeContext current, string? viewerKey = null)
    {
        EvictStaleTargets(current, viewerKey);

        // Already prefetched (or in flight) for this episode — don't re-schedule.
        if (cache.TryGetValue(StateKey(viewerKey), out PrefetchState? existing) && existing?.TriggerEpisode == current)
        {
            return;
        }

        EpisodeContext[] targets = BuildTargets(current);
        if (targets.Length == 0)
        {
            return;
        }

        cache.Set(StateKey(viewerKey), new PrefetchState(current, targets), s_stateTtl);
        _viewers[viewerKey ?? ""] = 0;

        CancellationToken ct = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunPrefetchAsync(targets, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Prefetch: unhandled exception in background task");
            }
        }, ct);
    }

    // ── Eviction ─────────────────────────────────────────────────────────────

    private void EvictStaleTargets(EpisodeContext current, string? viewerKey)
    {
        if (!cache.TryGetValue(StateKey(viewerKey), out PrefetchState? state) || state == null)
        {
            return;
        }

        // Still watching the same episode that triggered the last prefetch — nothing to do.
        if (state.TriggerEpisode == current)
        {
            return;
        }

        EpisodeContext[] stale;
        if (!string.Equals(state.TriggerEpisode.Title, current.Title, StringComparison.Ordinal))
        {
            // Different show: all prefetch targets are stale.
            stale = state.Targets;
        }
        else
        {
            // Same show. If the user landed on one of our predictions, keep that cache
            // entry and evict the others. Otherwise evict everything.
            int hit = Array.FindIndex(
                state.Targets,
                t => t.SeasonNumber == current.SeasonNumber && t.EpisodeNumber == current.EpisodeNumber);

            stale = hit >= 0
                ? state.Targets.Where((_, i) => i != hit).ToArray()
                : state.Targets;
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        List<IThoughtProvider> providers = scope.ServiceProvider.GetServices<IThoughtProvider>().ToList();
        EvictAll(stale, providers, ProtectedCacheKeys(viewerKey, providers));
        cache.Remove(StateKey(viewerKey));
        _viewers.TryRemove(viewerKey ?? "", out _);
    }

    // Cache keys another viewer is on or has warmed: evicting these would make them refetch.
    private HashSet<string> ProtectedCacheKeys(string? viewerKey, IReadOnlyList<IThoughtProvider> providers)
    {
        HashSet<string> keys = [];
        foreach (string other in _viewers.Keys.Where(k => k != (viewerKey ?? "")))
        {
            if (!cache.TryGetValue(StateKey(other.Length == 0 ? null : other), out PrefetchState? state) || state is null)
            {
                _viewers.TryRemove(other, out _);
                continue;
            }

            foreach (EpisodeContext ep in state.Targets.Append(state.TriggerEpisode))
            {
                foreach (IThoughtProvider provider in providers)
                {
                    keys.Add(provider.GetCacheKey(ep));
                }
            }
        }

        return keys;
    }

    private void EvictAll(IEnumerable<EpisodeContext> targets, IReadOnlyList<IThoughtProvider> providers,
        HashSet<string> protectedKeys)
    {
        foreach (EpisodeContext target in targets)
        {
            foreach (IThoughtProvider provider in providers)
            {
                string key = provider.GetCacheKey(target);
                if (!protectedKeys.Contains(key))
                {
                    cache.Remove(key);
                }
            }

            logger.LogDebug("Prefetch: evicted stale cache for {Title} S{Season}E{Episode}",
                target.Title, target.SeasonNumber, target.EpisodeNumber);
        }
    }

    // ── Prefetch ──────────────────────────────────────────────────────────────

    private async Task RunPrefetchAsync(EpisodeContext[] targets, CancellationToken ct = default)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        List<IThoughtProvider> providers = scope.ServiceProvider.GetServices<IThoughtProvider>().ToList();

        foreach (EpisodeContext target in targets)
        {
            ct.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Prefetch: warming cache for {Title} S{Season}E{Episode} ({Count} provider(s))",
                target.Title, target.SeasonNumber, target.EpisodeNumber, providers.Count);

            foreach (IThoughtProvider provider in providers)
            {
                try
                {
                    await provider.GetThoughtsAsync(target, null, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogDebug("Prefetch cancelled during shutdown");
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Prefetch: provider {Provider} failed for {Title} S{Season}E{Episode}",
                        provider.Metadata.Name, target.Title, target.SeasonNumber, target.EpisodeNumber);
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EpisodeContext[] BuildTargets(EpisodeContext current)
    {
        List<EpisodeContext> targets = new(2);

        // Next episode in the same season (E+1)
        if (current.EpisodeNumber < short.MaxValue)
        {
            targets.Add(current with
            {
                EpisodeTitle = string.Empty,
                ReleaseDate = null,
                EpisodeNumber = (short)(current.EpisodeNumber + 1)
            });
        }

        // First episode of the next season (S+1 E1) — handles season-finale transitions.
        // If E+1 exists, the S+1 E1 prefetch returns empty and is not cached (saves nothing but costs 4
        // cheap HTTP searches). If we're actually at a season finale, S+1 E1 is already warm.
        if (current.SeasonNumber < short.MaxValue)
        {
            targets.Add(current with
            {
                EpisodeTitle = string.Empty,
                ReleaseDate = null,
                SeasonNumber = (short)(current.SeasonNumber + 1),
                EpisodeNumber = 1
            });
        }

        return targets.ToArray();
    }

    private sealed record PrefetchState(
        EpisodeContext TriggerEpisode,
        EpisodeContext[] Targets);
}
