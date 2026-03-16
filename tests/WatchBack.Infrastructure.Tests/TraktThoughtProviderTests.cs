using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

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
    private readonly TraktOptions _options;

    public TraktThoughtProviderTests()
    {
        _options = new TraktOptions
        {
            ClientId = "test-client-id",
            Username = "testuser",
            AccessToken = "test-token",
            CacheTtlSeconds = 30
        };
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetThoughtsAsync_WithValidShow_ReturnsComments()
    {
        // Arrange
        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var searchResponseJson = """
            [
                {
                    "show": {
                        "ids": { "trakt": 1 },
                        "title": "Breaking Bad"
                    }
                }
            ]
            """;

        var commentsResponseJson = """
            [
                {
                    "comment": "Great pilot!",
                    "rating": 8.5,
                    "user": { "username": "user1" },
                    "created_at": "2009-01-20T12:00:00Z"
                }
            ]
            """;

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchResponseJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsResponseJson) }
        });

        var handler = new MockHttpMessageHandler(
            () => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new TraktThoughtProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("Trakt");
        result.Thoughts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetThoughtsAsync_WithUnknownShow_ReturnsEmpty()
    {
        // Arrange
        var mediaContext = new EpisodeContext(
            Title: "Unknown Show",
            ReleaseDate: null,
            EpisodeTitle: "Episode",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var searchResponseJson = "[]";

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchResponseJson)
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();

        var provider = new TraktThoughtProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result!.Thoughts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetThoughtsAsync_CallsReplyTreeBuilder()
    {
        // Arrange
        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var searchResponseJson = """
            [
                {
                    "show": {
                        "ids": { "trakt": 1 },
                        "title": "Breaking Bad"
                    }
                }
            ]
            """;

        var commentsResponseJson = """
            [
                {
                    "comment": "Great pilot!",
                    "rating": 8.5,
                    "user": { "username": "user1" },
                    "created_at": "2009-01-20T12:00:00Z"
                }
            ]
            """;

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchResponseJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsResponseJson) }
        });

        var handler = new MockHttpMessageHandler(() => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        var flatThoughts = new List<Thought>();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => flatThoughts.AddRange(x)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new TraktThoughtProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        treeBuilder.Received(1).BuildTree(Arg.Any<IEnumerable<Thought>>());
    }

    [Fact]
    public async Task GetServiceHealthAsync_ReturnsHealthy()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();

        var provider = new TraktThoughtProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        // Act
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void ExpectedWeight_IsOne()
    {
        var provider = new TraktThoughtProvider(
            new HttpClient(),
            new OptionsSnapshotStub<TraktOptions>(_options),
            _cache,
            Substitute.For<IReplyTreeBuilder>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        provider.ExpectedWeight.Should().Be(1);
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsOneTickWithWeightOne()
    {
        var mediaContext = new EpisodeContext("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        var searchJson = """[{"show":{"ids":{"trakt":1},"title":"Breaking Bad"}}]""";
        var commentsJson = """[]""";

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) },
        });

        var handler = new MockHttpMessageHandler(() => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new TraktThoughtProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        var ticks = new System.Collections.Concurrent.ConcurrentBag<SyncProgressTick>();
        var progress = new CapturingProgress(ticks);

        await provider.GetThoughtsAsync(mediaContext, progress);

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Trakt");
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsTickEvenOnError()
    {
        var mediaContext = new EpisodeContext("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        var handler = new MockHttpMessageHandler(() =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };

        var provider = new TraktThoughtProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, Substitute.For<IReplyTreeBuilder>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        var ticks = new System.Collections.Concurrent.ConcurrentBag<SyncProgressTick>();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Trakt");
    }

    [Fact]
    public async Task GetThoughtsAsync_NullProgress_DoesNotThrow()
    {
        var handler = new MockHttpMessageHandler(() =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktThoughtProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, Substitute.For<IReplyTreeBuilder>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        var mediaContext = new EpisodeContext("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        var act = async () => await provider.GetThoughtsAsync(mediaContext, null);
        await act.Should().NotThrowAsync();
    }

    private sealed class CapturingProgress(System.Collections.Concurrent.ConcurrentBag<SyncProgressTick> bag)
        : IProgress<SyncProgressTick>
    {
        public void Report(SyncProgressTick value) => bag.Add(value);
    }
}