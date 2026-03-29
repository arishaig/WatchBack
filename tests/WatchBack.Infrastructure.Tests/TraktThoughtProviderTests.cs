using System.Collections.Concurrent;
using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.ThoughtProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public class TraktThoughtProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

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

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchResponseJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsResponseJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchResponseJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsResponseJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>())
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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
            NullLogger<TraktThoughtProvider>.Instance);

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
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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
            Substitute.For<IReplyTreeBuilder>(), NullLogger<TraktThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> ticks = new();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Trakt");
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
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());
        TraktThoughtProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

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
            Substitute.For<IReplyTreeBuilder>(), NullLogger<TraktThoughtProvider>.Instance);

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
            NullLogger<TraktThoughtProvider>.Instance);

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
            NullLogger<TraktThoughtProvider>.Instance);

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
            NullLogger<TraktThoughtProvider>.Instance);

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
            NullLogger<TraktThoughtProvider>.Instance);

        IReadOnlyList<ProviderConfigField> fields = ((IDataProvider)provider).GetConfigSchema(_ => "", (_, _) => false);
        fields.Should().BeEmpty();
    }
}
