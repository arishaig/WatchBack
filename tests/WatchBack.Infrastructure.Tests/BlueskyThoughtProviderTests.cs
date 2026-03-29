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

public class BlueskyThoughtProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private readonly BlueskyOptions _options = new()
    {
        Handle = "testuser.bsky.social",
        AppPassword = "test-password",
        TokenCacheTtlSeconds = 5400
    };

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetThoughtsAsync_WithValidSearchResults_ReturnsPosts()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string tokenJson = """
                           {
                               "accessJwt": "test-token"
                           }
                           """;

        string searchJson = """
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

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result.Source.Should().Be("Bluesky");
        result.Thoughts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetThoughtsAsync_DeduplicatesSimilarPosts()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string tokenJson = """
                           {
                               "accessJwt": "test-token"
                           }
                           """;

        string searchJson = """
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

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        List<Thought> capturedThoughts = new();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => capturedThoughts.AddRange(x)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

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
        BlueskyOptions optionsWithoutCreds = new() { Handle = "", AppPassword = null, TokenCacheTtlSeconds = 5400 };

        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string searchJson = """
                            {
                                "posts": []
                            }
                            """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(optionsWithoutCreds),
            _cache, treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result.Source.Should().Be("Bluesky");
    }

    [Fact]
    public async Task GetServiceHealthAsync_ReturnsHealthy()
    {
        // Arrange
        string tokenJson = """
                           {
                               "accessJwt": "test-token"
                           }
                           """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void ExpectedWeight_IsOne()
    {
        BlueskyThoughtProvider provider = new(
            new HttpClient(),
            new OptionsSnapshotStub<BlueskyOptions>(_options),
            _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        provider.ExpectedWeight.Should().Be(1);
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsOneTickWithWeightOne()
    {
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string tokenJson = """{"accessJwt":"test-token"}""";
        string searchJson = """{"posts":[]}""";

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> ticks = new();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Bluesky");
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsTickEvenOnAuthFailure()
    {
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        MockHttpMessageHandler handler = new(() =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> ticks = new();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().ContainSingle(t => t.Weight == 1 && t.Provider == "Bluesky");
    }

    [Fact]
    public async Task GetThoughtsAsync_NullProgress_DoesNotThrow()
    {
        MockHttpMessageHandler handler = new(() =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        Func<Task<ThoughtResult?>> act = async () => await provider.GetThoughtsAsync(mediaContext);
        await act.Should().NotThrowAsync();
    }

    // ---- Provider self-description ----

    [Fact]
    public void ConfigSection_IsBluesky()
    {
        BlueskyThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        provider.ConfigSection.Should().Be("Bluesky");
    }

    [Fact]
    public void IsConfigured_WhenBothSet_IsTrue()
    {
        BlueskyThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenHandleMissing_IsFalse()
    {
        BlueskyOptions opts = new()
        {
            Handle = "",
            AppPassword = _options.AppPassword,
            TokenCacheTtlSeconds = _options.TokenCacheTtlSeconds,
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        BlueskyThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<BlueskyOptions>(opts), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        provider.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WhenPasswordMissing_IsFalse()
    {
        BlueskyOptions opts = new()
        {
            Handle = _options.Handle,
            AppPassword = "",
            TokenCacheTtlSeconds = _options.TokenCacheTtlSeconds,
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        BlueskyThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<BlueskyOptions>(opts), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        provider.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void GetConfigSchema_ReturnsTwoFields()
    {
        BlueskyThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        IReadOnlyList<ProviderConfigField> fields = provider.GetConfigSchema(_ => "", (_, _) => false);

        fields.Should().HaveCount(2);
        fields.Select(f => f.Key).Should().BeEquivalentTo("Bluesky__Handle", "Bluesky__AppPassword");
    }

    [Fact]
    public void GetConfigSchema_AppPasswordField_IsPasswordType()
    {
        BlueskyThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        ProviderConfigField pwField = provider.GetConfigSchema(_ => "", (_, _) => false)
            .First(f => f.Key == "Bluesky__AppPassword");

        pwField.Type.Should().Be("password");
        pwField.Value.Should().BeEmpty("password fields must never expose their stored value");
    }

    [Fact]
    public void RevealSecret_AppPassword_ReturnsStoredValue()
    {
        BlueskyThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        provider.RevealSecret("Bluesky__AppPassword").Should().Be(_options.AppPassword);
    }

    [Fact]
    public void RevealSecret_UnknownKey_ReturnsNull()
    {
        BlueskyThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        provider.RevealSecret("Bluesky__Handle").Should().BeNull();
        provider.RevealSecret("Other__Key").Should().BeNull();
    }
}
