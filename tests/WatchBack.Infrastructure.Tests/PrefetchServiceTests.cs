using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Infrastructure.Services;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class PrefetchServiceTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IThoughtProvider _provider = Substitute.For<IThoughtProvider>();
    private readonly IHostApplicationLifetime _lifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PrefetchService _service;

    public PrefetchServiceTests()
    {
        _provider.Metadata.Returns(new DataProviderMetadata("Test", "Test", BrandData: new BrandData("", "")));
        _provider.GetCacheKey(Arg.Any<MediaContext>()).Returns(ci =>
        {
            // Use season + episode in the key so different episodes don't collide,
            // matching what a real provider would produce.
            MediaContext m = (MediaContext)ci[0];
            return m is EpisodeContext ep
                ? $"key:{ep.Title}:S{ep.SeasonNumber}E{ep.EpisodeNumber}"
                : $"key:{m.Title}";
        });
        _provider.GetThoughtsAsync(Arg.Any<MediaContext>(), null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThoughtResult?>(null));

        _lifetime.ApplicationStopping.Returns(CancellationToken.None);

        ServiceCollection services = new();
        services.AddScoped<IThoughtProvider>(_ => _provider);
        ServiceProvider provider = services.BuildServiceProvider();
        _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        _service = new PrefetchService(_scopeFactory, _cache, NullLogger<PrefetchService>.Instance, _lifetime);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private static EpisodeContext MakeEpisode(string title = "Show A", short season = 1, short episode = 1) =>
        new(title, null, string.Empty, season, episode);

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SchedulePrefetch_SameEpisodeTwice_DoesNotDuplicatePrefetch()
    {
        EpisodeContext ep = MakeEpisode();
        _service.SchedulePrefetch(ep);
        await Task.Delay(150); // let background task run

        int callsAfterFirst = _provider.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IThoughtProvider.GetThoughtsAsync));

        _service.SchedulePrefetch(ep); // second call with same episode → should be skipped
        await Task.Delay(150);

        int callsAfterSecond = _provider.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IThoughtProvider.GetThoughtsAsync));

        // No additional calls after the second SchedulePrefetch for the same episode
        callsAfterSecond.Should().Be(callsAfterFirst);
    }

    // ── Eviction — different show ─────────────────────────────────────────────

    [Fact]
    public async Task SchedulePrefetch_DifferentShow_EvictsAllPreviousTargets()
    {
        EpisodeContext ep1 = MakeEpisode("Show A", 1, 1);
        _service.SchedulePrefetch(ep1);
        await Task.Delay(150);

        // Manually verify the predicted next episode is NOT yet in cache (prefetch populates it)
        // by checking GetThoughtsAsync was called for the predicted episodes
        await _provider.Received().GetThoughtsAsync(
            Arg.Is<MediaContext>(c => c.Title == "Show A"),
            null,
            Arg.Any<CancellationToken>());

        // Manually seed a cache entry for a predicted target so we can verify it's evicted
        string cacheKey = _provider.GetCacheKey(MakeEpisode("Show A", 1, 2));
        _cache.Set(cacheKey, "cached-value");

        // Now switch to a different show — all Show A targets should be evicted
        EpisodeContext ep2 = MakeEpisode("Show B", 1, 1);
        _service.SchedulePrefetch(ep2);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse("stale cache entry should have been evicted");
    }

    // ── Eviction — same show, landed on a prediction ─────────────────────────

    [Fact]
    public async Task SchedulePrefetch_LandedOnPrediction_KeepsThatCacheEntry()
    {
        EpisodeContext ep1 = MakeEpisode("Show A", 1, 1);
        _service.SchedulePrefetch(ep1);
        await Task.Delay(150);

        // Seed cache for predicted S1E2
        string nextEpKey = _provider.GetCacheKey(MakeEpisode("Show A", 1, 2));
        _cache.Set(nextEpKey, "prefetched");

        // User advances to S1E2 (the predicted episode)
        EpisodeContext ep2 = MakeEpisode("Show A", 1, 2);
        _service.SchedulePrefetch(ep2);

        // The S1E2 cache entry should be KEPT (not evicted) — it was a cache hit
        _cache.TryGetValue(nextEpKey, out _).Should().BeTrue("predicted entry should not be evicted when user lands on it");
    }

    // ── Provider failure ──────────────────────────────────────────────────────

    [Fact]
    public async Task SchedulePrefetch_ProviderThrows_ContinuesWithoutCrashing()
    {
        _provider.GetThoughtsAsync(Arg.Any<MediaContext>(), null, Arg.Any<CancellationToken>())
            .Returns<Task<ThoughtResult?>>(ci => throw new HttpRequestException("network error"));

        EpisodeContext ep = MakeEpisode();

        Func<Task> act = async () =>
        {
            _service.SchedulePrefetch(ep);
            await Task.Delay(300);
        };

        await act.Should().NotThrowAsync("provider failures should be logged and swallowed");
    }
}
