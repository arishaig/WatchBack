using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Infrastructure.Services;

public class PrefetchService(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    ILogger<PrefetchService> logger,
    IHostApplicationLifetime lifetime)
    : IPrefetchService
{
    // Sentinel stored in IMemoryCache so the prefetch state survives the request scope.
    private const string StateKey = "watchback:prefetch:state";

    // How long to keep prefetch state tracked (governs stale-entry eviction window).
    private static readonly TimeSpan StateTtl = TimeSpan.FromHours(4);

    private sealed record PrefetchState(
        EpisodeContext TriggerEpisode,
        EpisodeContext[] Targets);

    public void SchedulePrefetch(EpisodeContext current)
    {
        EvictStaleTargets(current);

        var targets = BuildTargets(current);
        if (targets.Length == 0)
            return;

        cache.Set(StateKey, new PrefetchState(current, targets), StateTtl);

        var ct = lifetime.ApplicationStopping;
        _ = Task.Run(async () => await RunPrefetchAsync(targets, ct), ct);
    }

    // ── Eviction ─────────────────────────────────────────────────────────────

    private void EvictStaleTargets(EpisodeContext current)
    {
        if (!cache.TryGetValue(StateKey, out PrefetchState? state) || state == null)
            return;

        // Still watching the same episode that triggered the last prefetch — nothing to do.
        if (state.TriggerEpisode == current)
            return;

        if (!string.Equals(state.TriggerEpisode.Title, current.Title, StringComparison.Ordinal))
        {
            // Different show: all prefetch targets are stale.
            EvictAll(state.Targets);
        }
        else
        {
            // Same show. If the user landed on one of our predictions, keep that cache
            // entry and evict the others. Otherwise evict everything.
            var hit = Array.FindIndex(
                state.Targets,
                t => t.SeasonNumber == current.SeasonNumber && t.EpisodeNumber == current.EpisodeNumber);

            var stale = hit >= 0
                ? state.Targets.Where((_, i) => i != hit).ToArray()
                : state.Targets;

            EvictAll(stale);
        }

        cache.Remove(StateKey);
    }

    private void EvictAll(IEnumerable<EpisodeContext> targets)
    {
        foreach (var target in targets)
        {
            foreach (var key in ThoughtCacheKeys(target))
                cache.Remove(key);

            logger.LogDebug("Prefetch: evicted stale cache for {Title} S{Season}E{Episode}",
                target.Title, target.SeasonNumber, target.EpisodeNumber);
        }
    }

    // ── Prefetch ──────────────────────────────────────────────────────────────

    private async Task RunPrefetchAsync(EpisodeContext[] targets, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var providers = scope.ServiceProvider.GetServices<IThoughtProvider>().ToList();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Prefetch: warming cache for {Title} S{Season}E{Episode} ({Count} provider(s))",
                target.Title, target.SeasonNumber, target.EpisodeNumber, providers.Count);

            foreach (var provider in providers)
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
        var targets = new List<EpisodeContext>(2);

        // Next episode in the same season (E+1)
        if (current.EpisodeNumber < short.MaxValue)
        {
            targets.Add(current with
            {
                EpisodeTitle = string.Empty,
                ReleaseDate = null,
                EpisodeNumber = (short)(current.EpisodeNumber + 1),
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
                EpisodeNumber = 1,
            });
        }

        return targets.ToArray();
    }

    /// <summary>
    /// Cache keys used by each thought provider for a given episode.
    /// Must stay in sync with the key formats in the individual providers.
    /// </summary>
    private static IEnumerable<string> ThoughtCacheKeys(EpisodeContext ep)
    {
        // RedditThoughtProvider: reddit:thoughts:{Title}:S{S:D2}E{E:D2}
        yield return $"reddit:thoughts:{ep.Title}:S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}";

        // TraktThoughtProvider: trakt:thoughts:{Title}:s{S}e{E}  (unpadded)
        yield return $"trakt:thoughts:{ep.Title}:s{ep.SeasonNumber}e{ep.EpisodeNumber}";

        // BlueskyThoughtProvider: bluesky:thoughts:{Title}:S{S:D2}E{E:D2}
        yield return $"bluesky:thoughts:{ep.Title}:S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}";
    }
}
