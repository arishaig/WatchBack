using System.Collections.Concurrent;
using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.ThoughtProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public class TraktThoughtProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly InMemoryLookupCache _ids = new();

    private readonly TraktOptions _options = new()
    {
        ClientId = "test-client-id",
        Username = "testuser",
        AccessToken = "test-token",
        CacheTtlSeconds = 30
    };

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetThoughtsAsync_WithValidShow_ReturnsComments()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string searchResponseJson = """
                                    [
                                        {
                                            "show": {
                                                "ids": { "trakt": 1 },
                                                "title": "Breaking Bad"
                                            }
                                        }
                                    ]
                                    """;

        string commentsResponseJson = """
                                      [
                                          {
                                              "comment": "Great pilot!",
                                              "rating": 8.5,
                                              "user": { "username": "user1" },
                                              "created_at": "2009-01-20T12:00:00Z"
                                          }
                                      ]
                                      """;

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/", searchResponseJson)
            .RespondTo("/episodes/1/comments/", commentsResponseJson)
            .Default("[]");
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result.Source.Should().Be("Trakt");
        result.Thoughts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetThoughtsAsync_WithUnknownShow_ReturnsEmpty()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Unknown Show",
            null,
            "Episode",
            1,
            1);

        string searchResponseJson = "[]";

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchResponseJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result.Thoughts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetThoughtsAsync_CallsReplyTreeBuilder()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string searchResponseJson = """
                                    [
                                        {
                                            "show": {
                                                "ids": { "trakt": 1 },
                                                "title": "Breaking Bad"
                                            }
                                        }
                                    ]
                                    """;

        string commentsResponseJson = """
                                      [
                                          {
                                              "comment": "Great pilot!",
                                              "rating": 8.5,
                                              "user": { "username": "user1" },
                                              "created_at": "2009-01-20T12:00:00Z"
                                          }
                                      ]
                                      """;

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/", searchResponseJson)
            .RespondTo("/episodes/1/comments/", commentsResponseJson)
            .Default("[]");
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>())
            .Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        treeBuilder.Received(1).BuildTree(Arg.Any<IEnumerable<Thought>>());
    }

    [Fact]
    public async Task GetServiceHealthAsync_ReturnsHealthy()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void ExpectedWeight_IsOne()
    {
        TraktThoughtProvider provider = new(
            new HttpClient(),
            new OptionsSnapshotStub<TraktOptions>(_options),
            _cache,
            Substitute.For<IReplyTreeBuilder>(),
            _ids, NullLogger<TraktThoughtProvider>.Instance);

        provider.ExpectedWeight.Should().Be(1);
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsOneTickWithWeightOne()
    {
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string searchJson = """[{"show":{"ids":{"trakt":1},"title":"Breaking Bad"}}]""";
        string commentsJson = """[]""";

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> ticks = new();
        CapturingProgress progress = new(ticks);

        await provider.GetThoughtsAsync(mediaContext, progress);

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Trakt");
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsTickEvenOnError()
    {
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        MockHttpMessageHandler handler = new(() =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(), _ids, NullLogger<TraktThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> ticks = new();
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Trakt");
        result!.IsTransientFailure.Should().BeTrue();
    }

    // Regression: the SSE loop polls every few seconds and the frontend shows the
    // loading bar whenever it sees intermediate progress ticks. A cache hit must
    // report zero ticks, or the bar flashes on every poll cycle.
    [Fact]
    public async Task GetThoughtsAsync_OnCacheHit_ReportsNoTicks()
    {
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string searchJson = """[{"show":{"ids":{"trakt":1},"title":"Breaking Bad"}}]""";
        string commentsJson = """[]""";

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        ConcurrentBag<SyncProgressTick> ticks = new();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().BeEmpty("a cached cycle does no work and must not arm the frontend loading bar");
    }

    // Regression: when Trakt rate-limits (429) slug resolution, the failed lookup used to
    // go uncached, so every poll cycle retried it and re-ticked, flashing the loading bar.
    [Fact]
    public async Task GetThoughtsAsync_AfterRateLimitedLookup_ReportsNoTicksOnNextCall()
    {
        EpisodeContext mediaContext = new("Love Is Blind", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        int requestCount = 0;
        MockHttpMessageHandler handler = new(() =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(), _ids, NullLogger<TraktThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);
        int requestsAfterFirst = requestCount;

        ConcurrentBag<SyncProgressTick> ticks = new();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().BeEmpty("a cached failure does no work and must not arm the frontend loading bar");
        requestCount.Should().Be(requestsAfterFirst);
    }

    // The loading bar is for the initial bulk load only: once a fetch has been attempted for an
    // episode, a retry after the cached failure expires must run without reporting progress.
    [Fact]
    public async Task GetThoughtsAsync_RetryAfterFailureExpires_ReportsNoTicks()
    {
        EpisodeContext mediaContext = new("Love Is Blind", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        int requestCount = 0;
        MockHttpMessageHandler handler = new(() =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(), _ids, NullLogger<TraktThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> initialTicks = new();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(initialTicks));
        initialTicks.Should().ContainSingle("the initial load reports progress");
        int requestsAfterFirst = requestCount;

        _cache.Remove(provider.GetCacheKey(mediaContext)); // simulate the failure entry's TTL expiring

        ConcurrentBag<SyncProgressTick> retryTicks = new();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(retryTicks));

        requestCount.Should().BeGreaterThan(requestsAfterFirst, "the retry still hits the network");
        retryTicks.Should().BeEmpty("retries are background work and must not arm the loading bar");
    }

    [Fact]
    public async Task GetThoughtsAsync_WhenNotConfigured_ReportsNoTicks()
    {
        TraktOptions unconfigured = new() { ClientId = "" };
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        TraktThoughtProvider provider = new(new HttpClient(), new OptionsSnapshotStub<TraktOptions>(unconfigured),
            _cache, Substitute.For<IReplyTreeBuilder>(), _ids, NullLogger<TraktThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> ticks = new();
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        result.Should().NotBeNull();
        result!.Thoughts.Should().BeEmpty();
        ticks.Should().BeEmpty("an unconfigured provider does no work and must not arm the frontend loading bar");
    }

    [Fact]
    public async Task GetThoughtsAsync_WithImdbId_UsesIdLookupInsteadOfTitleSearch()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad", null, "Pilot", 1, 1,
            new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0903747" });

        List<string> requestedUrls = new();
        string searchJson = """[{"show":{"ids":{"trakt":1,"slug":"breaking-bad"},"title":"Breaking Bad"}}]""";
        string commentsJson = "[]";

        MockHttpMessageHandler handler = new(request =>
        {
            requestedUrls.Add(request.RequestUri?.ToString() ?? "");
            return requestedUrls.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) };
        });
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert — first request is the IMDB ID lookup, not a text search
        requestedUrls[0].Should().Contain("/search/imdb/tt0903747");
        requestedUrls[0].Should().NotContain("/search/show");
    }

    [Fact]
    public async Task GetThoughtsAsync_WithImdbIdReturningNoResults_FallsBackToTitleSearch()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad", null, "Pilot", 1, 1,
            new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0903747" });

        List<string> requestedUrls = new();
        string titleSearchJson = """[{"show":{"ids":{"trakt":1,"slug":"breaking-bad"},"title":"Breaking Bad"}}]""";
        string commentsJson = "[]";

        MockHttpMessageHandler handler = new(request =>
        {
            string url = request.RequestUri?.ToString() ?? "";
            requestedUrls.Add(url);
            if (url.Contains("/search/imdb/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            }

            if (url.Contains("/search/show"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(titleSearchJson) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) };
        });
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert — tried IMDB first, fell back to title search, still returned a result
        requestedUrls.Should().Contain(u => u.Contains("/search/imdb/"));
        requestedUrls.Should().Contain(u => u.Contains("/search/show"));
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetThoughtsAsync_WithMultipleExternalIds_PrefersImdbOverTvdbAndTmdb()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad", null, "Pilot", 1, 1,
            new Dictionary<string, string>
            {
                [ExternalIdType.Imdb] = "tt0903747",
                [ExternalIdType.Tvdb] = "81189",
                [ExternalIdType.Tmdb] = "1396"
            });

        List<string> requestedUrls = new();
        string searchJson = """[{"show":{"ids":{"trakt":1,"slug":"breaking-bad"},"title":"Breaking Bad"}}]""";
        string commentsJson = "[]";

        MockHttpMessageHandler handler = new(request =>
        {
            string url = request.RequestUri?.ToString() ?? "";
            requestedUrls.Add(url);
            return url.Contains("/search/imdb/") || url.Contains("/search/show")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) };
        });
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert — IMDB used first; TVDB and TMDB not needed
        requestedUrls[0].Should().Contain("/search/imdb/tt0903747");
        requestedUrls.Should().NotContain(u => u.Contains("/search/tvdb/"));
        requestedUrls.Should().NotContain(u => u.Contains("/search/tmdb/"));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithOnlyTvdbId_UsesTvdbLookupAndSkipsTitleSearch()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad", null, "Pilot", 1, 1,
            new Dictionary<string, string> { [ExternalIdType.Tvdb] = "81189" });

        List<string> requestedUrls = new();
        string searchJson = """[{"show":{"ids":{"trakt":1,"slug":"breaking-bad"},"title":"Breaking Bad"}}]""";
        string commentsJson = "[]";

        MockHttpMessageHandler handler = new(request =>
        {
            string url = request.RequestUri?.ToString() ?? "";
            requestedUrls.Add(url);
            return url.Contains("/search/tvdb/") || url.Contains("/search/show")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) };
        });
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert — TVDB used, title search not needed
        requestedUrls[0].Should().Contain("/search/tvdb/81189");
        requestedUrls.Should().NotContain(u => u.Contains("/search/show"));
    }

    [Fact]
    public async Task GetThoughtsAsync_NullProgress_DoesNotThrow()
    {
        MockHttpMessageHandler handler = new(() =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(), _ids, NullLogger<TraktThoughtProvider>.Instance);

        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        Func<Task<ThoughtResult?>> act = async () => await provider.GetThoughtsAsync(mediaContext);
        await act.Should().NotThrowAsync();
    }

    // ---- Provider self-description ----

    [Fact]
    public void ConfigSection_IsTrakt()
    {
        TraktThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            _ids, NullLogger<TraktThoughtProvider>.Instance);

        // Shares ConfigSection with TraktWatchStateProvider — same key, one merged card in the UI
        provider.ConfigSection.Should().Be("Trakt");
    }

    [Fact]
    public void IsConfigured_WhenClientIdSet_IsTrue()
    {
        TraktOptions opts = new()
        {
            ClientId = "some-client-id",
            Username = _options.Username,
            AccessToken = _options.AccessToken,
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        TraktThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            _ids, NullLogger<TraktThoughtProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenClientIdEmpty_IsFalse()
    {
        TraktOptions opts = new()
        {
            ClientId = "",
            Username = _options.Username,
            AccessToken = _options.AccessToken,
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        TraktThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            _ids, NullLogger<TraktThoughtProvider>.Instance);

        provider.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void GetConfigSchema_ReturnsEmpty_SchemaOwnedByWatchStateProvider()
    {
        // TraktThoughtProvider defers schema ownership to TraktWatchStateProvider.
        // Both share the same ConfigSection; the endpoint picks fields from the first
        // provider that returns a non-empty schema.
        TraktThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            _ids, NullLogger<TraktThoughtProvider>.Instance);

        IReadOnlyList<ProviderConfigField> fields = ((IDataProvider)provider).GetConfigSchema(_ => "", (_, _) => false);
        fields.Should().BeEmpty();
    }

    // ---- Movie handling ----

    [Fact]
    public async Task GetThoughtsAsync_WithMovieContext_FetchesMovieComments()
    {
        MediaContext mediaContext = new(
            "Interstellar",
            new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero));

        string movieSearchJson = """[{"movie":{"title":"Interstellar","ids":{"trakt":1,"slug":"interstellar-2014"}}}]""";
        string commentsJson = """
                              [
                                  {
                                      "comment": "Mind-bending film!",
                                      "user": { "username": "user1" },
                                      "created_at": "2014-11-10T12:00:00Z"
                                  }
                              ]
                              """;

        List<string> requestedUrls = new();
        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(movieSearchJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        ]);

        MockHttpMessageHandler handler = new(req =>
        {
            requestedUrls.Add(req.RequestUri?.ToString() ?? "");
            return responses.Dequeue();
        });
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result.Thoughts.Should().NotBeEmpty();
        // Must use movie search endpoint, not show search
        requestedUrls.Should().Contain(u => u.Contains("/search/movie"));
        requestedUrls.Should().NotContain(u => u.Contains("/search/show"));
        // Must fetch from movies comments endpoint
        requestedUrls.Should().Contain(u => u.Contains("/movies/") && u.Contains("/comments/"));
        requestedUrls.Should().NotContain(u => u.Contains("/shows/"));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithMovieContextAndImdbId_UsesImdbLookup()
    {
        MediaContext mediaContext = new(
            "Interstellar",
            new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero),
            new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0816692" });

        string movieSearchJson = """[{"movie":{"title":"Interstellar","ids":{"trakt":1,"slug":"interstellar-2014"}}}]""";
        string commentsJson = "[]";

        List<string> requestedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            requestedUrls.Add(req.RequestUri?.ToString() ?? "");
            return requestedUrls.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(movieSearchJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) };
        });
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        // First request must be the IMDB ID lookup with type=movie
        requestedUrls[0].Should().Contain("/search/imdb/tt0816692");
        requestedUrls[0].Should().Contain("type=movie");
        requestedUrls[0].Should().NotContain("type=show");
        requestedUrls.Should().NotContain(u => u.Contains("/search/movie?query="));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithMovieContextAndImdbId_TvdbNotUsedForMovies()
    {
        MediaContext mediaContext = new(
            "Interstellar",
            null,
            new Dictionary<string, string>
            {
                [ExternalIdType.Imdb] = "tt0816692",
                [ExternalIdType.Tvdb] = "999999"
            });

        string movieSearchJson = """[{"movie":{"title":"Interstellar","ids":{"trakt":1,"slug":"interstellar-2014"}}}]""";
        string commentsJson = "[]";

        List<string> requestedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            requestedUrls.Add(req.RequestUri?.ToString() ?? "");
            return requestedUrls.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(movieSearchJson) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) };
        });
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        // TVDB must never be used for movies
        requestedUrls.Should().NotContain(u => u.Contains("/search/tvdb/"));
    }

    // Cross-season scoping: Trakt uses season/episode as path parameters in the comments URL,
    // so a regression in slug resolution or URL construction would silently fetch the wrong episode.
    [Fact]
    public async Task GetThoughtsAsync_ForSeasonTwoEpisode_CommentsUrlContainsCorrectSeasonAndEpisodeNumbers()
    {
        EpisodeContext mediaContext = new(
            "The Pitt",
            new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
            "Mass Casualty",
            2,
            1);

        string slugSearchJson = """[{"show":{"title":"The Pitt","ids":{"trakt":12345,"slug":"the-pitt"}}}]""";
        string commentsJson = "[]";

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/", slugSearchJson)
            .Default(commentsJson);

        HttpClient client = new(handler);
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        // The comments request must address season 2, episode 1 of the resolved show
        handler.RecordedUris.Should().Contain(
            u => u.ToString().Contains("/seasons/2/episodes/1/"),
            "comments URL must be scoped to season 2 episode 1");

        handler.RecordedUris.Should().NotContain(
            u => u.ToString().Contains("/seasons/1/"),
            "must not accidentally request season 1 comments");
    }

    [Fact]
    public async Task GetThoughtsAsync_WithUnknownMovie_ReturnsEmpty()
    {
        MediaContext mediaContext = new("Nonexistent Movie 12345", null);

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        HttpClient client = new(handler);
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result.Thoughts.Should().BeEmpty();
    }

    // Regression: the slug cache key used only Title, so two shows with the same
    // title but different IMDB IDs (e.g. "The Office" US vs UK) would cross-contaminate
    // each other's cached slug for the whole 24 h TTL.
    [Fact]
    public async Task ResolveSlugAsync_TwoShowsWithSameTitleDifferentImdbIds_DoNotShareCache()
    {
        EpisodeContext officeUs = new(
            "The Office",
            new DateTimeOffset(2005, 3, 24, 0, 0, 0, TimeSpan.Zero),
            "Pilot", 1, 1,
            new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0386676" });
        EpisodeContext officeUk = new(
            "The Office",
            new DateTimeOffset(2001, 7, 9, 0, 0, 0, TimeSpan.Zero),
            "Downsize", 1, 1,
            new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0290978" });

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/imdb/tt0386676",
                """[{"show":{"title":"The Office","ids":{"trakt":1,"slug":"the-office-us"}}}]""")
            .RespondTo("/search/imdb/tt0290978",
                """[{"show":{"title":"The Office","ids":{"trakt":2,"slug":"the-office-uk"}}}]""")
            .Default("[]");

        HttpClient client = new(handler);
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, _ids, NullLogger<TraktThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(officeUs);
        await provider.GetThoughtsAsync(officeUk);

        // Each show must fetch comments under its own slug — no cache bleed
        handler.RecordedUris.Should().Contain(u => u.ToString().Contains("/shows/1/"));
        handler.RecordedUris.Should().Contain(u => u.ToString().Contains("/shows/2/"));
    }
    // ---- Phase 3: ID lookup, likes, fallbacks, replies ----

    private TraktThoughtProvider Provider(RoutableMockHttpHandler handler, MemoryCache? cache = null)
    {
        IReplyTreeBuilder tree = Substitute.For<IReplyTreeBuilder>();
        tree.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        return new TraktThoughtProvider(new HttpClient(handler), new OptionsSnapshotStub<TraktOptions>(_options),
            cache ?? _cache, tree, _ids, NullLogger<TraktThoughtProvider>.Instance);
    }

    private static string Comments(params (int Id, int Likes, int Replies, bool Spoiler)[] comments) =>
        "[" + string.Join(",", comments.Select(c =>
            $$"""{"id":{{c.Id}},"comment":"comment {{c.Id}}","likes":{{c.Likes}},"replies":{{c.Replies}},"spoiler":{{(c.Spoiler ? "true" : "false")}},"user":{"username":"u{{c.Id}}"},"created_at":"2022-02-18T12:00:00Z"}""")) + "]";

    private static readonly Dictionary<string, string> s_severanceE1Ids = new()
    {
        [ExternalIdType.Imdb] = "tt11650328",
        [ExternalIdType.Tvdb] = "8582131",
        [ExternalIdType.Tmdb] = "3302766"
    };

    // Jellyfin passes the playing episode's own provider IDs. Recorded from Trakt (November 2026):
    // GET /search/imdb/tt11650328 with no type filter; /search/tvdb/8891221?type=episode returns the same.
    private const string SeveranceEpisodeHit =
        """[{"show":{"ids":{"imdb":"tt11280740","plex":{"guid":"5e1622d34f2292003e91314a","slug":"severance"},"slug":"severance","tmdb":95396,"tvdb":371980,"trakt":154997},"year":2022,"title":"Severance","aired_episodes":19},"type":"episode","score":100,"episode":{"ids":{"imdb":"tt11650328","plex":{"guid":"5e1622d34f2292003e913150"},"tmdb":1982925,"tvdb":8891221,"trakt":4648809},"title":"Good News About Hell","number":1,"season":1}}]""";

    // Regression: an unfiltered TVDB or TMDB lookup finds only shows (live, November 2026: /search/tvdb/
    // 8891221 returns [], with ?type=episode it returns the episode), so an episode Jellyfin knew only by
    // its TVDB or TMDB ID never found its show.
    [Theory]
    [InlineData("Tvdb", "8891221")]
    [InlineData("Tmdb", "1982925")]
    public async Task TvdbOrTmdbEpisodeId_IsLookedUpAsAnEpisode_WhenItIsNoShow(string idType, string id)
    {
        string key = idType == "Tvdb" ? ExternalIdType.Tvdb : ExternalIdType.Tmdb;
        EpisodeContext pilot = new("Severance", null, "Good News About Hell", 1, 1,
            new Dictionary<string, string> { [key] = id });
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo(u => u.AbsolutePath == $"/search/{key}/{id}" && u.Query == "?type=episode", SeveranceEpisodeHit)
            .RespondTo(u => u.AbsolutePath == $"/search/{key}/{id}", "[]")
            .Default("[]");

        await Provider(handler).GetThoughtsAsync(pilot);

        handler.RecordedUris.Should().Contain(u => u.AbsolutePath == "/shows/154997/seasons/1/episodes/1/comments/likes");
        handler.RecordedUris.Should().NotContain(u => u.AbsolutePath == "/search/show",
            "the ID was enough; no title guess");
        _ids.Answers[$"trakt:show:{key}:{id}"].Should().Be(new LookupAnswer("154997"));
    }

    [Fact]
    public async Task EpisodeIds_ResolveToTheirShow_InOneCall_AndTheAnswerIsKept()
    {
        EpisodeContext pilot = new("Severance", null, "Good News About Hell", 1, 1, s_severanceE1Ids);
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/imdb/tt11650328", SeveranceEpisodeHit)
            .Default(Comments((1, 9, 0, false), (2, 8, 0, false), (3, 7, 0, false), (4, 6, 0, false), (5, 5, 0, false)));

        await Provider(handler).GetThoughtsAsync(pilot);
        using MemoryCache afterRestart = new(new MemoryCacheOptions());
        await Provider(handler, afterRestart).GetThoughtsAsync(pilot);

        handler.RecordedUris.Count(u => u.AbsolutePath.StartsWith("/search/", StringComparison.Ordinal)).Should().Be(1);
        handler.RecordedUris[0].Query.Should().NotContain("type=show",
            "the IDs may be the episode's, so the lookup must accept an episode result");
        handler.RecordedUris.Should().Contain(u => u.AbsolutePath == "/shows/154997/seasons/1/episodes/1/comments/likes");
        _ids.Answers["trakt:show:imdb:tt11650328"].Should().Be(new LookupAnswer("154997"));
    }

    [Fact]
    public async Task TvdbEpisodeResult_MustHaveTheSameSeasonAndNumber()
    {
        // TVDB numbers shows and episodes separately: one ID can be another show's episode.
        EpisodeContext episode = new("Severance", null, "Half Loop", 1, 2,
            new Dictionary<string, string> { [ExternalIdType.Tvdb] = "8582132" });
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/tvdb/",
                """[{"type":"episode","episode":{"season":4,"number":7},"show":{"title":"Other","ids":{"trakt":1}}}]""")
            .RespondTo("/search/show", """[{"type":"show","show":{"title":"Severance","ids":{"trakt":154997}}}]""")
            .Default("[]");

        await Provider(handler).GetThoughtsAsync(episode);

        handler.RecordedUris.Should().NotContain(u => u.AbsolutePath.StartsWith("/shows/1/", StringComparison.Ordinal));
        handler.RecordedUris.Should().Contain(u => u.AbsolutePath.StartsWith("/shows/154997/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotOnTrakt_IsAConfirmedMiss_AndIsNotLookedUpAgain()
    {
        EpisodeContext episode = new("Obscure Local Show", null, "Pilot", 1, 1,
            new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0000001" });
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler().Default("[]");

        ThoughtResult? first = await Provider(handler).GetThoughtsAsync(episode);
        int calls = handler.RecordedUris.Count;
        using MemoryCache afterRestart = new(new MemoryCacheOptions());
        await Provider(handler, afterRestart).GetThoughtsAsync(episode);

        first!.IsTransientFailure.Should().BeFalse();
        calls.Should().Be(2, "one ID lookup and one title search");
        handler.RecordedUris.Count.Should().Be(calls, "\"not on Trakt\" is remembered");
    }

    [Fact]
    public async Task FailedLookup_IsNotRemembered()
    {
        EpisodeContext episode = new("Severance", null, "Pilot", 1, 1, s_severanceE1Ids);
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/", "{}", HttpStatusCode.BadGateway)
            .Default("[]");

        ThoughtResult? result = await Provider(handler).GetThoughtsAsync(episode);

        result!.IsTransientFailure.Should().BeTrue();
        _ids.Answers.Should().BeEmpty();
        handler.RecordedUris.Should().NotContain(u => u.AbsolutePath == "/search/show",
            "a title guess while the ID lookup is failing could show another show's comments");
    }

    [Fact]
    public async Task TitleSearch_PrefersTheExactTitleOverTheTopHit()
    {
        EpisodeContext episode = new("The Office", null, "Pilot", 1, 1);
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/show",
                """[{"type":"show","show":{"title":"The Office Mix","ids":{"trakt":7}}},{"type":"show","show":{"title":"The Office","ids":{"trakt":8}}}]""")
            .Default("[]");

        await Provider(handler).GetThoughtsAsync(episode);

        handler.RecordedUris.Should().Contain(u => u.AbsolutePath.StartsWith("/shows/8/", StringComparison.Ordinal));
    }

    // Regression: the score came from a top-level "rating" field Trakt's comment object doesn't have
    // (the rating is user_stats.rating), so every Trakt comment had no score.
    [Fact]
    public async Task Comments_AreSortedByLikes_AndScoredByLikes()
    {
        EpisodeContext episode = new("Severance", null, "Pilot", 1, 1, s_severanceE1Ids);
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/", SeveranceEpisodeHit)
            .RespondTo("/episodes/1/comments/likes",
                """[{"id":1,"comment":"Loved it","likes":42,"replies":0,"user_stats":{"rating":8},"user":{"username":"a"},"created_at":"2022-02-18T12:00:00Z"}]""")
            .Default("[]");

        ThoughtResult? result = await Provider(handler).GetThoughtsAsync(episode);

        result!.Thoughts.Should().ContainSingle(t => t.Id == "trakt:1").Which.Score.Should().Be(42);
        handler.RecordedUris.Should().NotContain(u => u.AbsolutePath.EndsWith("/newest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FewEpisodeComments_AddSpoilerFreeSeasonThenShowComments_Labelled()
    {
        EpisodeContext episode = new("Severance", null, "Pilot", 1, 1, s_severanceE1Ids);
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/", SeveranceEpisodeHit)
            .RespondTo("/episodes/1/comments/likes", Comments((1, 3, 0, false)))
            .RespondTo("/seasons/1/comments/likes", Comments((10, 5, 0, false), (11, 4, 0, true)).Replace(
                "]", """,{"id":12,"comment":"Season review","likes":30,"replies":0,"review":true,"user":{"username":"r"}}]"""))
            .RespondTo("/shows/154997/comments/likes", Comments((20, 9, 0, false), (1, 3, 0, false)))
            .Default("[]");

        ThoughtResult? result = await Provider(handler).GetThoughtsAsync(episode);

        result!.Thoughts!.Select(t => (t.Id, t.PostTitle)).Should().Equal(
            ("trakt:1", null),
            ("trakt:10", "Season 1 comments"),
            ("trakt:20", "Comments on the whole show"));
    }

    [Fact]
    public async Task EnoughEpisodeComments_SkipTheFallbacks()
    {
        EpisodeContext episode = new("Severance", null, "Pilot", 1, 1, s_severanceE1Ids);
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/", SeveranceEpisodeHit)
            .RespondTo("/episodes/1/comments/likes",
                Comments((1, 9, 0, false), (2, 8, 0, false), (3, 7, 0, false), (4, 6, 0, false), (5, 5, 0, false)))
            .Default("[]");

        await Provider(handler).GetThoughtsAsync(episode);

        handler.RecordedUris.Should().NotContain(u => u.AbsolutePath.EndsWith("/seasons/1/comments/likes", StringComparison.Ordinal));
        handler.RecordedUris.Should().NotContain(u => u.AbsolutePath == "/shows/154997/comments/likes");
    }

    [Fact]
    public async Task Replies_AreFetchedOnlyForTheMostLikedCommentsWithReplies()
    {
        EpisodeContext episode = new("Severance", null, "Pilot", 1, 1, s_severanceE1Ids);
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/search/", SeveranceEpisodeHit)
            .RespondTo("/episodes/1/comments/likes", Comments(
                (1, 50, 4, false), (2, 40, 2, false), (3, 30, 1, false), (4, 20, 6, false), (5, 1, 9, false),
                (6, 60, 0, false)))
            .RespondTo("/comments/1/replies", Comments((100, 2, 0, false)))
            .Default("[]");

        ThoughtResult? result = await Provider(handler).GetThoughtsAsync(episode);

        handler.RecordedUris.Where(u => u.AbsolutePath.EndsWith("/replies", StringComparison.Ordinal)).Select(u => u.AbsolutePath)
            .Should().BeEquivalentTo("/comments/1/replies", "/comments/2/replies", "/comments/3/replies");
        result!.Thoughts.Should().ContainSingle(t => t.Id == "trakt:100").Which.ParentId.Should().Be("trakt:1");
    }
}

// IDurableLookupCache without SQLite: what the Trakt provider remembers, and nothing else.
internal sealed class InMemoryLookupCache : IDurableLookupCache
{
    public ConcurrentDictionary<string, LookupAnswer> Answers { get; } = new();

    public Task<LookupAnswer?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Answers.TryGetValue(key, out LookupAnswer a) ? a : (LookupAnswer?)null);

    public Task SetAsync(string key, string kind, string? value, TimeSpan lifetime, CancellationToken ct = default)
    {
        Answers[key] = new LookupAnswer(value);
        return Task.CompletedTask;
    }
}
