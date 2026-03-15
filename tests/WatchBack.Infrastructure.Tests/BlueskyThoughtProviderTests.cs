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

public class BlueskyThoughtProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly BlueskyOptions _options;

    public BlueskyThoughtProviderTests()
    {
        _options = new BlueskyOptions
        {
            Handle = "testuser.bsky.social",
            AppPassword = "test-password",
            TokenCacheTtlSeconds = 5400
        };
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetThoughtsAsync_WithValidSearchResults_ReturnsPosts()
    {
        // Arrange
        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var tokenJson = """
            {
                "accessJwt": "test-token"
            }
            """;

        var searchJson = """
            {
                "posts": [
                    {
                        "uri": "at://did/app.bsky.feed.post/123",
                        "cid": "abc123",
                        "author": {
                            "handle": "user1.bsky.social",
                            "displayName": "User One"
                        },
                        "record": {
                            "text": "Amazing first episode!",
                            "createdAt": "2009-01-20T12:00:00Z"
                        },
                        "likeCount": 10
                    }
                ]
            }
            """;

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
        });

        var handler = new MockHttpMessageHandler(() => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://bsky.social") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new BlueskyThoughtProvider(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("Bluesky");
        result.Thoughts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetThoughtsAsync_DeduplicatesSimilarPosts()
    {
        // Arrange
        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var tokenJson = """
            {
                "accessJwt": "test-token"
            }
            """;

        var searchJson = """
            {
                "posts": [
                    {
                        "uri": "at://did/app.bsky.feed.post/123",
                        "cid": "abc123",
                        "author": { "handle": "user1.bsky.social" },
                        "record": {
                            "text": "This is an amazing episode!",
                            "createdAt": "2009-01-20T12:00:00Z"
                        },
                        "likeCount": 10
                    },
                    {
                        "uri": "at://did/app.bsky.feed.post/124",
                        "cid": "abc124",
                        "author": { "handle": "user2.bsky.social" },
                        "record": {
                            "text": "This is an amazing episode!",
                            "createdAt": "2009-01-20T13:00:00Z"
                        },
                        "likeCount": 5
                    }
                ]
            }
            """;

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
        });

        var handler = new MockHttpMessageHandler(() => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://bsky.social") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        var capturedThoughts = new List<Thought>();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => capturedThoughts.AddRange(x)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new BlueskyThoughtProvider(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        // Duplicate posts should be deduplicated
        capturedThoughts.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetThoughtsAsync_WithoutCredentials_UsesPublicAPI()
    {
        // Arrange
        var optionsWithoutCreds = new BlueskyOptions
        {
            Handle = "",
            AppPassword = null,
            TokenCacheTtlSeconds = 5400
        };

        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var searchJson = """
            {
                "posts": []
            }
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://bsky.social") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new BlueskyThoughtProvider(client, new OptionsSnapshotStub<BlueskyOptions>(optionsWithoutCreds), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("Bluesky");
    }

    [Fact]
    public async Task GetServiceHealthAsync_ReturnsHealthy()
    {
        // Arrange
        var tokenJson = """
            {
                "accessJwt": "test-token"
            }
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://bsky.social") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();

        var provider = new BlueskyThoughtProvider(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void ExpectedWeight_IsOne()
    {
        var provider = new BlueskyThoughtProvider(
            new HttpClient(),
            new OptionsSnapshotStub<BlueskyOptions>(_options),
            _cache,
            Substitute.For<IReplyTreeBuilder>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        provider.ExpectedWeight.Should().Be(1);
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsOneTickWithWeightOne()
    {
        var mediaContext = new EpisodeContext("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        var tokenJson = """{"accessJwt":"test-token"}""";
        var searchJson = """{"posts":[]}""";

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) },
        });

        var handler = new MockHttpMessageHandler(() => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://bsky.social") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new BlueskyThoughtProvider(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        var ticks = new System.Collections.Concurrent.ConcurrentBag<SyncProgressTick>();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Bluesky");
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsTickEvenOnAuthFailure()
    {
        var mediaContext = new EpisodeContext("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        var handler = new MockHttpMessageHandler(() =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://bsky.social") };

        var provider = new BlueskyThoughtProvider(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache, Substitute.For<IReplyTreeBuilder>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        var ticks = new System.Collections.Concurrent.ConcurrentBag<SyncProgressTick>();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Bluesky");
    }

    [Fact]
    public async Task GetThoughtsAsync_NullProgress_DoesNotThrow()
    {
        var handler = new MockHttpMessageHandler(() =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://bsky.social") };
        var provider = new BlueskyThoughtProvider(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache, Substitute.For<IReplyTreeBuilder>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

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