using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.Persistence;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class DurableLookupCacheTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchback-lookup-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly List<ServiceProvider> _processes = [];

    public void Dispose()
    {
        foreach (ServiceProvider sp in _processes)
        {
            sp.Dispose();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    // One "process": its own memory cache, on the shared SQLite file. A second one is a restart.
    private IDurableLookupCache StartProcess()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WatchBackDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<ResultCacheStore>();
        services.AddSingleton<IDurableLookupCache, DurableLookupCache>();

        ServiceProvider sp = services.BuildServiceProvider();
        using (IServiceScope scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<WatchBackDbContext>().Database.Migrate();
        }

        _processes.Add(sp);
        return sp.GetRequiredService<IDurableLookupCache>();
    }

    [Fact]
    public async Task AnAnswer_SurvivesARestart_UntilItsLifetimeEnds()
    {
        await StartProcess().SetAsync("trakt:show:imdb:tt1", "trakt-id", "1390", TimeSpan.FromDays(30));

        IDurableLookupCache restarted = StartProcess();
        LookupAnswer? kept = await restarted.GetAsync("trakt:show:imdb:tt1");
        _time.Advance(TimeSpan.FromDays(31));
        LookupAnswer? expired = await StartProcess().GetAsync("trakt:show:imdb:tt1");

        kept.Should().Be(new LookupAnswer("1390"));
        expired.Should().BeNull();
    }

    [Fact]
    public async Task NothingFound_IsRememberedAsAnAnswer()
    {
        await StartProcess().SetAsync("trakt:show:imdb:tt2", "trakt-id", null, TimeSpan.FromDays(7));

        LookupAnswer? none = await StartProcess().GetAsync("trakt:show:imdb:tt2");
        LookupAnswer? unknown = await StartProcess().GetAsync("trakt:show:imdb:tt3");

        none.Should().Be(new LookupAnswer(null));
        unknown.Should().BeNull();
    }
}
