using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.Http;
using WatchBack.Infrastructure.Persistence;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class MediaResultCacheTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchback-cache-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(s_now);
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (ServiceProvider sp in _providers)
        {
            sp.Dispose();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    // One "process": its own memory cache and singletons, on the shared SQLite file. Building a second
    // one on the same file is a restart.
    private ServiceProvider StartProcess()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WatchBackDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<HostBackoffRegistry>();
        services.AddSingleton<ResultCacheStore>();
        services.AddSingleton<IMediaResultCache, MediaResultCache>();
        services.AddSingleton<ResultCacheMaintenance>();
        ServiceProvider sp = services.BuildServiceProvider();
        using (IServiceScope scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<WatchBackDbContext>().Database.Migrate();
        }

        _providers.Add(sp);
        return sp;
    }

    private static EpisodeContext Episode(DateTimeOffset? aired) => new("Severance", aired, "Good News About Hell", 1, 1);

    private static Thought SampleThought => new("t1", null, null, "great episode", null, [], "someone", 10,
        s_now.AddDays(-1), "Fake", []);

    private sealed class FakeThoughtProvider : IThoughtProvider
    {
        public int Calls;
        public Func<ThoughtResult?> Result { get; set; } = () => new ThoughtResult("Fake", null, null, null, [SampleThought], null);
        public TaskCompletionSource? Gate { get; set; }
        public bool Configured { get; set; } = true;
        public TimeSpan? Cap { get; set; }

        public DataProviderMetadata Metadata => new("Fake", "fake provider");
        public bool IsConfigured => Configured;
        public TimeSpan? MaxCacheAge => Cap;
        public int ExpectedWeight => 1;

        public string GetCacheKey(MediaContext mediaContext) =>
            mediaContext is EpisodeContext e ? $"fake:{e.Title}:S{e.SeasonNumber}E{e.EpisodeNumber}" : $"fake:{mediaContext.Title}";

        public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext,
            IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            progress?.Report(new SyncProgressTick(1, "Fake"));
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(ct);
            }

            return Result();
        }

        public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRatingsProvider : IRatingsProvider
    {
        public int Calls;
        public IReadOnlyList<MediaRating> Ratings { get; set; } = [new MediaRating("Internet Movie Database", "8.7/10")];
        public DataProviderMetadata Metadata => new("FakeRatings", "fake ratings");

        public Task<IReadOnlyList<MediaRating>> GetRatingsAsync(string imdbId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Ratings);
        }

        public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // ── Acceptance: one upstream lookup per provider per episode over a 30-minute session ──

    [Fact]
    public async Task ThirtyMinuteSession_TwoViewersPollingEveryFiveSeconds_FetchesOnce()
    {
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        FakeThoughtProvider provider = new();
        EpisodeContext episode = Episode(s_now.AddHours(-20)); // freshest tier: shortest lifetime

        for (int poll = 0; poll < 360; poll++)
        {
            await Task.WhenAll(
                cache.GetThoughtsAsync(provider, episode),
                cache.GetThoughtsAsync(provider, episode));
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        provider.Calls.Should().Be(1);
    }

    // ── Acceptance: an old episode makes zero upstream calls after a restart ──

    [Fact]
    public async Task OldEpisode_AfterRestart_MakesNoUpstreamCalls()
    {
        EpisodeContext episode = Episode(s_now.AddDays(-400));
        FakeThoughtProvider before = new();
        FakeRatingsProvider ratingsBefore = new();
        IMediaResultCache first = StartProcess().GetRequiredService<IMediaResultCache>();
        ThoughtResult? original = await first.GetThoughtsAsync(before, episode);
        await first.GetRatingsAsync(ratingsBefore, episode, "tt11280740");

        FakeThoughtProvider after = new();
        FakeRatingsProvider ratingsAfter = new();
        IMediaResultCache restarted = StartProcess().GetRequiredService<IMediaResultCache>();
        _time.Advance(TimeSpan.FromDays(60));
        ThoughtResult? reloaded = await restarted.GetThoughtsAsync(after, episode);
        IReadOnlyList<MediaRating> ratings = await restarted.GetRatingsAsync(ratingsAfter, episode, "tt11280740");

        after.Calls.Should().Be(0);
        ratingsAfter.Calls.Should().Be(0);
        reloaded.Should().BeEquivalentTo(original);
        ratings.Should().ContainSingle().Which.Value.Should().Be("8.7/10");
    }

    [Fact]
    public async Task OldEpisodeMiss_AfterRestart_IsStillAMiss()
    {
        EpisodeContext episode = Episode(s_now.AddDays(-400));
        IMediaResultCache first = StartProcess().GetRequiredService<IMediaResultCache>();
        await first.GetThoughtsAsync(new FakeThoughtProvider { Result = () => new ThoughtResult("Fake", null, null, null, [], null) }, episode);

        FakeThoughtProvider after = new();
        await StartProcess().GetRequiredService<IMediaResultCache>().GetThoughtsAsync(after, episode);

        after.Calls.Should().Be(0, "a confirmed miss on old media is kept for days");
    }

    // ── Single-flight ──

    [Fact]
    public async Task ConcurrentCallers_ShareOneFetch_AndOnlyTheFirstGetsProgress()
    {
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        FakeThoughtProvider provider = new() { Gate = new TaskCompletionSource() };
        EpisodeContext episode = Episode(s_now.AddDays(-5));
        List<SyncProgressTick> leaderTicks = [];
        List<SyncProgressTick> joinerTicks = [];

        Task<ThoughtResult?> leader = cache.GetThoughtsAsync(provider, episode, new SyncProgress(leaderTicks));
        Task<ThoughtResult?>[] joiners = Enumerable.Range(0, 4)
            .Select(_ => cache.GetThoughtsAsync(provider, episode, new SyncProgress(joinerTicks)))
            .ToArray();
        provider.Gate.SetResult();
        await Task.WhenAll(joiners.Append(leader));

        provider.Calls.Should().Be(1);
        joiners.Should().AllSatisfy(j => j.Result!.Thoughts.Should().HaveCount(1));
        leaderTicks.Should().HaveCount(1);
        joinerTicks.Should().BeEmpty();
    }

    [Fact]
    public async Task CallerThatStartedTheFetch_Leaving_DoesNotCancelItForOthers()
    {
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        FakeThoughtProvider provider = new() { Gate = new TaskCompletionSource() };
        EpisodeContext episode = Episode(s_now.AddDays(-5));
        using CancellationTokenSource leaderLeaves = new();

        Task<ThoughtResult?> leader = cache.GetThoughtsAsync(provider, episode, null, leaderLeaves.Token);
        Task<ThoughtResult?> other = cache.GetThoughtsAsync(provider, episode);
        await leaderLeaves.CancelAsync();
        provider.Gate.SetResult();

        await FluentActions.Awaiting(() => leader).Should().ThrowAsync<OperationCanceledException>();
        (await other)!.Thoughts.Should().HaveCount(1);
        provider.Calls.Should().Be(1);
    }

    // ── Failures, misses and undated lookups ──

    [Fact]
    public async Task TransientFailure_IsHeldBrieflyInMemory_AndNeverPersisted()
    {
        EpisodeContext episode = Episode(s_now.AddDays(-400));
        FakeThoughtProvider failing = new()
        {
            Result = () => new ThoughtResult("Fake", null, null, null, [], null, IsTransientFailure: true)
        };
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();

        await cache.GetThoughtsAsync(failing, episode);
        _time.Advance(TimeSpan.FromSeconds(30));
        await cache.GetThoughtsAsync(failing, episode);
        int callsWithinAMinute = failing.Calls;
        _time.Advance(TimeSpan.FromSeconds(31));
        await cache.GetThoughtsAsync(failing, episode);

        FakeThoughtProvider afterRestart = new();
        await StartProcess().GetRequiredService<IMediaResultCache>().GetThoughtsAsync(afterRestart, episode);

        callsWithinAMinute.Should().Be(1);
        failing.Calls.Should().Be(2, "the failure entry lasts a minute");
        afterRestart.Calls.Should().Be(1, "a failure is not a miss and must not survive a restart");
    }

    [Fact]
    public async Task RecentEpisodeMiss_IsRetriedAfterAnHour()
    {
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        FakeThoughtProvider provider = new() { Result = () => new ThoughtResult("Fake", null, null, null, [], null) };
        EpisodeContext episode = Episode(s_now.AddHours(-6));

        await cache.GetThoughtsAsync(provider, episode);
        _time.Advance(TimeSpan.FromMinutes(59));
        await cache.GetThoughtsAsync(provider, episode);
        _time.Advance(TimeSpan.FromMinutes(2));
        await cache.GetThoughtsAsync(provider, episode);

        provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task UndatedLookup_IsNotPersisted_AndNeverAnswersADatedOne()
    {
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        FakeThoughtProvider provider = new() { Result = () => new ThoughtResult("Fake", null, null, null, [], null) };

        await cache.GetThoughtsAsync(provider, Episode(null));
        await cache.GetThoughtsAsync(provider, Episode(s_now.AddDays(-400)));

        FakeThoughtProvider afterRestart = new();
        await StartProcess().GetRequiredService<IMediaResultCache>().GetThoughtsAsync(afterRestart, Episode(null));

        provider.Calls.Should().Be(2, "the dated lookup searches properly instead of trusting the undated miss");
        afterRestart.Calls.Should().Be(1);
    }

    [Fact]
    public async Task UnconfiguredProvider_IsCalledThrough_AndNothingIsCached()
    {
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        FakeThoughtProvider provider = new() { Configured = false };
        EpisodeContext episode = Episode(s_now.AddDays(-400));

        await cache.GetThoughtsAsync(provider, episode);
        provider.Configured = true;
        await cache.GetThoughtsAsync(provider, episode);

        provider.Calls.Should().Be(2, "configuring the provider must take effect immediately");
    }

    [Fact]
    public async Task ProviderCacheCap_LimitsAnOldEpisodesLifetime()
    {
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        FakeThoughtProvider provider = new() { Cap = TimeSpan.FromDays(7) };
        EpisodeContext episode = Episode(s_now.AddDays(-400));

        await cache.GetThoughtsAsync(provider, episode);
        _time.Advance(TimeSpan.FromDays(8));
        await cache.GetThoughtsAsync(provider, episode);

        provider.Calls.Should().Be(2);
    }

    [Fact]
    public async Task EmptyRatings_AreNotPersisted()
    {
        EpisodeContext episode = Episode(s_now.AddDays(-400));
        await StartProcess().GetRequiredService<IMediaResultCache>()
            .GetRatingsAsync(new FakeRatingsProvider { Ratings = [] }, episode, "tt1");

        FakeRatingsProvider after = new();
        await StartProcess().GetRequiredService<IMediaResultCache>().GetRatingsAsync(after, episode, "tt1");

        after.Calls.Should().Be(1, "an empty list may have been a failed OMDb call");
    }

    // ── Manual refresh ──

    [Fact]
    public async Task Invalidate_ForcesTheNextLookupUpstream_EvenAfterRestart()
    {
        EpisodeContext episode = Episode(s_now.AddDays(-400)) with
        {
            ExternalIds = new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt1" }
        };
        FakeThoughtProvider provider = new();
        FakeRatingsProvider ratings = new();
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        await cache.GetThoughtsAsync(provider, episode);
        await cache.GetRatingsAsync(ratings, episode, "tt1");

        await cache.InvalidateAsync([provider], [ratings], episode);
        await cache.GetThoughtsAsync(provider, episode);
        await cache.GetRatingsAsync(ratings, episode, "tt1");

        provider.Calls.Should().Be(2);
        ratings.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Invalidate_RepeatedWithinAMinute_IsIgnored()
    {
        EpisodeContext episode = Episode(s_now.AddDays(-400));
        FakeThoughtProvider provider = new();
        IMediaResultCache cache = StartProcess().GetRequiredService<IMediaResultCache>();
        await cache.GetThoughtsAsync(provider, episode);

        bool first = await cache.InvalidateAsync([provider], [], episode);
        await cache.GetThoughtsAsync(provider, episode);
        _time.Advance(TimeSpan.FromSeconds(30));
        bool second = await cache.InvalidateAsync([provider], [], episode);
        await cache.GetThoughtsAsync(provider, episode);
        _time.Advance(TimeSpan.FromSeconds(31));
        bool third = await cache.InvalidateAsync([provider], [], episode);
        await cache.GetThoughtsAsync(provider, episode);

        (first, second, third).Should().Be((true, false, true));
        provider.Calls.Should().Be(3, "the ignored refresh reuses the result fetched by the first");
    }

    // ── Acceptance: a 429 never causes a retry sooner than the server asked, across a restart ──

    [Fact]
    public async Task BackoffWindow_SurvivesRestart()
    {
        ServiceProvider first = StartProcess();
        ResultCacheMaintenance maintenance = first.GetRequiredService<ResultCacheMaintenance>();
        await maintenance.StartAsync(CancellationToken.None);
        first.GetRequiredService<HostBackoffRegistry>().Suppress("api.pullpush.io", s_now.AddMinutes(10));
        ResultCacheStore store = first.GetRequiredService<ResultCacheStore>();
        await WaitUntilAsync(async () => (await store.GetLiveAsync("backoff")).Count == 1);
        await maintenance.StopAsync(CancellationToken.None);

        ServiceProvider restarted = StartProcess();
        await restarted.GetRequiredService<ResultCacheMaintenance>().StartAsync(CancellationToken.None);
        HostBackoffRegistry registry = restarted.GetRequiredService<HostBackoffRegistry>();

        registry.IsSuppressed("api.pullpush.io", out DateTimeOffset until).Should().BeTrue();
        until.Should().Be(s_now.AddMinutes(10));
        _time.Advance(TimeSpan.FromMinutes(11));
        registry.IsSuppressed("api.pullpush.io", out _).Should().BeFalse();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 100 && !await condition(); i++)
        {
            await Task.Delay(20);
        }
    }

    private sealed class SyncProgress(List<SyncProgressTick> ticks) : IProgress<SyncProgressTick>
    {
        public void Report(SyncProgressTick value)
        {
            lock (ticks)
            {
                ticks.Add(value);
            }
        }
    }
}

public sealed class CacheTtlPolicyTests
{
    private static readonly DateTimeOffset s_now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(400, 365 * 24)] // settled: effectively permanent
    [InlineData(31, 365 * 24)]
    [InlineData(10, 24)]
    [InlineData(1, 2)] // scores and comment counts still moving
    [InlineData(-3, 2)] // not aired yet
    public void ForHit_DependsOnAge(int daysAgo, int expectedHours)
    {
        CacheTtlPolicy.ForHit(s_now.AddDays(-daysAgo), s_now).Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Theory]
    [InlineData(400, 72)]
    [InlineData(10, 6)]
    [InlineData(1, 1)]
    public void ForMiss_IsHoursWhenRecent_DaysWhenOld(int daysAgo, int expectedHours)
    {
        CacheTtlPolicy.ForMiss(s_now.AddDays(-daysAgo), s_now).Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Fact]
    public void ProviderCap_WinsWhenShorter()
    {
        CacheTtlPolicy.ForHit(s_now.AddDays(-400), s_now, TimeSpan.FromDays(180)).Should().Be(TimeSpan.FromDays(180));
        CacheTtlPolicy.ForHit(s_now.AddDays(-1), s_now, TimeSpan.FromDays(180)).Should().Be(TimeSpan.FromHours(2));
    }
}
