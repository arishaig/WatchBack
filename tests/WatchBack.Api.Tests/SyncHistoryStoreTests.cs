using FluentAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using WatchBack.Api.Logging;
using WatchBack.Infrastructure.Persistence;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class SyncHistoryStoreTests : IAsyncDisposable
{
    // Holding an open connection to the in-memory database prevents SQLite from
    // wiping it when individual DbContext instances are disposed between operations.
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SyncHistoryStore _store;

    public SyncHistoryStoreTests()
    {
        _keepAlive = new SqliteConnection("Data Source=:memory:");
        _keepAlive.Open();

        ServiceCollection services = new();
        services.AddDbContext<WatchBackDbContext>(opts =>
            opts.UseSqlite(_keepAlive,
                b => b.MigrationsAssembly("WatchBack.Infrastructure")));

        _provider = services.BuildServiceProvider();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        using IServiceScope initScope = _scopeFactory.CreateScope();
        WatchBackDbContext db = initScope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
        db.Database.EnsureCreated();

        _store = new SyncHistoryStore(_scopeFactory, NullLogger<SyncHistoryStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _provider.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    private static SyncSnapshot MakeSnapshot(string status = "Idle", string? title = "Test Show") =>
        new(DateTimeOffset.UtcNow, status, title, [new ProviderSyncRecord("Reddit", 5)]);

    [Fact]
    public void GetLatest_ReturnsNullInitially()
    {
        _store.GetLatest().Should().BeNull();
    }

    [Fact]
    public void Record_UpdatesLatestImmediately_BeforePersistCompletes()
    {
        SyncSnapshot snapshot = MakeSnapshot("Watching", "Breaking Bad");

        _store.Record(snapshot);

        // GetLatest must return the snapshot synchronously — before any async persist
        SyncSnapshot? latest = _store.GetLatest();
        latest.Should().NotBeNull();
        latest!.Status.Should().Be("Watching");
        latest.Title.Should().Be("Breaking Bad");
    }

    [Fact]
    public void Record_OverwritesPreviousLatest()
    {
        _store.Record(MakeSnapshot("Idle", "Show A"));
        _store.Record(MakeSnapshot("Watching", "Show B"));

        _store.GetLatest()!.Title.Should().Be("Show B");
    }

    [Fact]
    public async Task Record_PersistsToDatabase()
    {
        SyncSnapshot snapshot = MakeSnapshot("Idle", "Better Call Saul");
        _store.Record(snapshot, durationMs: 1500);

        // Allow the fire-and-forget persist to complete
        await Task.Delay(300);

        using IServiceScope scope = _scopeFactory.CreateScope();
        WatchBackDbContext db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
        Infrastructure.Persistence.Entities.SyncLogEntity? entity =
            await db.SyncLogs.OrderByDescending(e => e.Id).FirstOrDefaultAsync();

        entity.Should().NotBeNull();
        entity!.Status.Should().Be("Idle");
        entity.Title.Should().Be("Better Call Saul");
        entity.DurationMs.Should().Be(1500);
    }

    [Fact]
    public async Task Record_ThoughtCountSummedAcrossProviders()
    {
        SyncSnapshot snapshot = new(
            DateTimeOffset.UtcNow,
            "Idle",
            "Multi-Source Show",
            [new ProviderSyncRecord("Reddit", 3), new ProviderSyncRecord("Bluesky", 7)]);

        _store.Record(snapshot);
        await Task.Delay(300);

        using IServiceScope scope = _scopeFactory.CreateScope();
        WatchBackDbContext db = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
        Infrastructure.Persistence.Entities.SyncLogEntity? entity =
            await db.SyncLogs.OrderByDescending(e => e.Id).FirstOrDefaultAsync();

        entity!.ThoughtCount.Should().Be(10);
    }
}
