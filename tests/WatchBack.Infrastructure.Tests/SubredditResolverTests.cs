using System.Net;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.Persistence;
using WatchBack.Infrastructure.ThoughtProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class SubredditResolverTests : IDisposable
{
    private const string Candidates = """
                                      {"data":[
                                        {"display_name":"thebearded","subscribers":5000,"public_description":"Beards and bearding advice."},
                                        {"display_name":"TheBear","subscribers":357607,"public_description":"Subreddit for FX series 'The Bear'."}
                                      ]}
                                      """;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"watchback-subs-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly List<ServiceProvider> _processes = [];
    private int _requests;

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
    private ArcticShiftSubredditResolver StartProcess(Func<HttpResponseMessage> api, TaskCompletionSource? gate = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WatchBackDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<ResultCacheStore>();

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(RedditThoughtProvider)).Returns(_ => new HttpClient(new GatedHandler(() =>
        {
            Interlocked.Increment(ref _requests);
            return api();
        }, gate)));
        services.AddSingleton(factory);
        services.AddSingleton<ArcticShiftSubredditResolver>();

        ServiceProvider sp = services.BuildServiceProvider();
        using (IServiceScope scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<WatchBackDbContext>().Database.Migrate();
        }

        _processes.Add(sp);
        return sp.GetRequiredService<ArcticShiftSubredditResolver>();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task ResolveAsync_LooksUpOnce_ThenRemembersAcrossARestart()
    {
        ArcticShiftSubredditResolver first = StartProcess(() => Json(Candidates));
        SubredditResolution found = await first.ResolveAsync("The Bear");
        await first.ResolveAsync("The Bear");

        ArcticShiftSubredditResolver afterRestart = StartProcess(() => Json(Candidates));
        SubredditResolution remembered = await afterRestart.ResolveAsync("The Bear");

        found.Should().Be(new SubredditResolution("TheBear"));
        remembered.Should().Be(found);
        _requests.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_NoConvincingCandidate_RemembersNone()
    {
        ArcticShiftSubredditResolver first = StartProcess(() => Json(
            """{"data":[{"display_name":"thebearded","subscribers":5000,"public_description":"Beards."}]}"""));
        SubredditResolution none = await first.ResolveAsync("The Bear");

        await StartProcess(() => Json(Candidates)).ResolveAsync("The Bear");

        none.Should().Be(new SubredditResolution(null));
        _requests.Should().Be(1, "\"no subreddit\" is an answer too, and is kept");
    }

    [Fact]
    public async Task ResolveAsync_Failure_IsReportedAndNotRemembered()
    {
        ArcticShiftSubredditResolver resolver = StartProcess(() =>
            Json("""{"data":null,"error":"Timeout. Maybe slow down a bit"}"""));

        SubredditResolution failed = await resolver.ResolveAsync("The Bear");
        await resolver.ResolveAsync("The Bear");

        failed.Failed.Should().BeTrue();
        _requests.Should().Be(2, "a failure is retried, not stored as an answer");
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentLookupsOfOneShow_ShareOneRequest()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ArcticShiftSubredditResolver resolver = StartProcess(() => Json(Candidates), gate);

        Task<SubredditResolution> a = resolver.ResolveAsync("The Bear");
        Task<SubredditResolution> b = resolver.ResolveAsync("The Bear");
        gate.SetResult();

        (await a).Should().Be(await b);
        _requests.Should().Be(1);
    }

    [Theory]
    [InlineData("Shōgun", "shogun")]
    [InlineData("The Bear", "thebear")]
    [InlineData("It's Always Sunny in Philadelphia", "itsalwayssunnyinphiladelphia")]
    public void Normalize_KeepsLowerCaseLettersAndDigits_WithoutAccents(string title, string expected)
    {
        ArcticShiftSubredditResolver.Normalize(title).Should().Be(expected);
    }

    [Fact]
    public void Pick_IgnoresTinySubreddits()
    {
        ArcticShiftSubredditDto[] candidates =
        [
            new("TheBearTV", 400, "The Bear TV show"),
            new("TheBear", 357607, "Subreddit for FX series 'The Bear'.")
        ];

        ArcticShiftSubredditResolver.Pick(candidates, "The Bear").Should().Be("TheBear");
        ArcticShiftSubredditResolver.Pick(candidates.Take(1), "The Bear").Should().BeNull();
    }

    private sealed class GatedHandler(Func<HttpResponseMessage> respond, TaskCompletionSource? gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (gate is not null)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return respond();
        }
    }
}
