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

public class LemmyThoughtProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private readonly LemmyOptions _options = new()
    {
        InstanceUrl = "https://lemmy.world",
        Community = null,
        MaxPosts = 3,
        MaxComments = 250,
        CacheTtlSeconds = 3600
    };

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private LemmyThoughtProvider CreateProvider(HttpClient client, LemmyOptions? options = null)
    {
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());
        return new LemmyThoughtProvider(client, new OptionsSnapshotStub<LemmyOptions>(options ?? _options),
            _cache, treeBuilder, NullLogger<LemmyThoughtProvider>.Instance);
    }

    private LemmyThoughtProvider CreateProvider(HttpClient client, out IReplyTreeBuilder treeBuilder,
        LemmyOptions? options = null)
    {
        treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());
        return new LemmyThoughtProvider(client, new OptionsSnapshotStub<LemmyOptions>(options ?? _options),
            _cache, treeBuilder, NullLogger<LemmyThoughtProvider>.Instance);
    }

    // ---- GetThoughtsAsync happy path ----

    [Fact]
    public async Task GetThoughtsAsync_WithValidSearchResultsAndComments_ReturnsThoughts()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string searchJson = """
                            {
                                "posts": [
                                    {
                                        "post": {
                                            "id": 101,
                                            "name": "Breaking Bad S01E01 discussion",
                                            "ap_id": "https://lemmy.world/post/101",
                                            "body": "What did everyone think?",
                                            "published": "2008-01-20T12:00:00Z"
                                        },
                                        "creator": { "name": "poster1" },
                                        "counts": { "score": 42 }
                                    }
                                ]
                            }
                            """;

        string commentsJson = """
                              {
                                  "comments": [
                                      {
                                          "comment": {
                                              "id": 201,
                                              "content": "Amazing episode!",
                                              "path": "0.201",
                                              "published": "2008-01-20T13:00:00Z",
                                              "ap_id": "https://lemmy.world/comment/201"
                                          },
                                          "creator": { "name": "user1" },
                                          "counts": { "score": 10 }
                                      }
                                  ]
                              }
                              """;

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("Lemmy");
        result.Thoughts.Should().NotBeEmpty();
        result.PostTitle.Should().Be("Breaking Bad S01E01 discussion");
        result.PostUrl.Should().Be("https://lemmy.world/post/101");
    }

    [Fact]
    public async Task GetThoughtsAsync_WithEmptyPosts_ReturnsEmptyResult()
    {
        // Arrange
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string searchJson = """{"posts": []}""";

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("Lemmy");
        result.Thoughts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetThoughtsAsync_WhenSearchFails_ReturnsEmpty()
    {
        // Arrange
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("Lemmy");
        result.Thoughts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetThoughtsAsync_WithCommunityFilter_IncludesCommunityInSearchUrl()
    {
        // Arrange
        LemmyOptions optionsWithCommunity = new()
        {
            InstanceUrl = "https://lemmy.world",
            Community = "television",
            MaxPosts = 3,
            MaxComments = 250,
            CacheTtlSeconds = 3600
        };

        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        string searchJson = """{"posts": []}""";

        List<string> requestedUrls = [];
        MockHttpMessageHandler handler = new(req =>
        {
            requestedUrls.Add(req.RequestUri?.ToString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) };
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client, optionsWithCommunity);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        requestedUrls.Should().ContainSingle(u => u.Contains("community_name=television"));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithNoCommunity_OmitsCommunityFromSearchUrl()
    {
        // Arrange
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        string searchJson = """{"posts": []}""";

        List<string> requestedUrls = [];
        MockHttpMessageHandler handler = new(req =>
        {
            requestedUrls.Add(req.RequestUri?.ToString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) };
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        requestedUrls.Should().ContainSingle(u => !u.Contains("community_name"));
    }

    // ---- Reply tree / parent ID parsing ----

    [Fact]
    public async Task GetThoughtsAsync_TopLevelComment_HasNullParentId()
    {
        // Arrange
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string searchJson = """
                            {
                                "posts": [{
                                    "post": { "id": 1, "name": "post", "ap_id": "https://lemmy.world/post/1" }
                                }]
                            }
                            """;

        string commentsJson = """
                              {
                                  "comments": [{
                                      "comment": { "id": 10, "content": "top level", "path": "0.10", "published": "2024-01-01T00:00:00Z" },
                                      "creator": { "name": "u1" },
                                      "counts": { "score": 1 }
                                  }]
                              }
                              """;

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        List<Thought> capturedThoughts = [];
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => capturedThoughts.AddRange(x)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        LemmyThoughtProvider provider = new(client, new OptionsSnapshotStub<LemmyOptions>(_options),
            _cache, treeBuilder, NullLogger<LemmyThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert — top-level comment "0.10" should have no parent
        capturedThoughts.Should().ContainSingle(t => t.Id == "lemmy:10" && t.ParentId == null);
    }

    [Fact]
    public async Task GetThoughtsAsync_NestedComment_HasCorrectParentId()
    {
        // Arrange
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string searchJson = """
                            {
                                "posts": [{
                                    "post": { "id": 1, "name": "post", "ap_id": "https://lemmy.world/post/1" }
                                }]
                            }
                            """;

        string commentsJson = """
                              {
                                  "comments": [{
                                      "comment": { "id": 20, "content": "reply", "path": "0.10.20", "published": "2024-01-01T00:00:00Z" },
                                      "creator": { "name": "u2" },
                                      "counts": { "score": 2 }
                                  }]
                              }
                              """;

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        List<Thought> capturedThoughts = [];
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => capturedThoughts.AddRange(x)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        LemmyThoughtProvider provider = new(client, new OptionsSnapshotStub<LemmyOptions>(_options),
            _cache, treeBuilder, NullLogger<LemmyThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert — "0.10.20" means comment 20 is a reply to comment 10
        capturedThoughts.Should().ContainSingle(t => t.Id == "lemmy:20" && t.ParentId == "lemmy:10");
    }

    // ---- Caching ----

    [Fact]
    public async Task GetThoughtsAsync_SecondCall_ReturnsCachedResult()
    {
        // Arrange
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        string searchJson = """{"posts": []}""";

        int callCount = 0;
        MockHttpMessageHandler handler = new(() =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) };
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        // Act
        await provider.GetThoughtsAsync(mediaContext);
        await provider.GetThoughtsAsync(mediaContext);

        // Assert — only one HTTP call because the second was served from cache
        callCount.Should().Be(1);
    }

    // ---- GetServiceHealthAsync ----

    [Fact]
    public async Task GetServiceHealthAsync_WhenSiteReachable_ReturnsHealthy()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WhenSiteUnreachable_ReturnsUnhealthy()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeFalse();
    }

    // ---- Progress reporting ----

    [Fact]
    public async Task GetThoughtsAsync_ReportsOneSearchTickPlusOnePerPost()
    {
        // Arrange
        LemmyOptions optionsTwoPosts = new()
        {
            InstanceUrl = "https://lemmy.world",
            MaxPosts = 2,
            MaxComments = 250,
            CacheTtlSeconds = 3600
        };

        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string searchJson = """
                            {
                                "posts": [
                                    { "post": { "id": 1, "name": "post1", "ap_id": "https://lemmy.world/post/1" } },
                                    { "post": { "id": 2, "name": "post2", "ap_id": "https://lemmy.world/post/2" } }
                                ]
                            }
                            """;
        string commentsJson = """{"comments": []}""";

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client, optionsTwoPosts);

        ConcurrentBag<SyncProgressTick> ticks = new();

        // Act
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        // Assert — 1 tick for the search + 1 per post (2 posts) = 3 ticks total
        ticks.Should().HaveCount(3);
        ticks.Should().OnlyContain(t => t.Provider == "Lemmy" && t.Weight == 1);
    }

    [Fact]
    public async Task GetThoughtsAsync_NullProgress_DoesNotThrow()
    {
        MockHttpMessageHandler handler = new(() =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        Func<Task<ThoughtResult?>> act = async () => await provider.GetThoughtsAsync(mediaContext);
        await act.Should().NotThrowAsync();
    }

    // ---- ExpectedWeight ----

    [Fact]
    public void ExpectedWeight_IsOnePlusMaxPosts()
    {
        LemmyThoughtProvider provider = CreateProvider(new HttpClient());

        // default MaxPosts = 3, so 1 (search) + 3 (posts) = 4
        provider.ExpectedWeight.Should().Be(4);
    }

    // ---- Provider self-description ----

    [Fact]
    public void ConfigSection_IsLemmy()
    {
        LemmyThoughtProvider provider = CreateProvider(new HttpClient());
        provider.ConfigSection.Should().Be("Lemmy");
    }

    [Fact]
    public void IsConfigured_IsTrue()
    {
        // Lemmy has a default instance URL and requires no secrets
        LemmyThoughtProvider provider = CreateProvider(new HttpClient());
        ((IDataProvider)provider).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void GetConfigSchema_ReturnsFourFields()
    {
        LemmyThoughtProvider provider = CreateProvider(new HttpClient());

        IReadOnlyList<ProviderConfigField> fields = provider.GetConfigSchema(_ => "", (_, _) => false);

        fields.Should().HaveCount(4);
        fields.Select(f => f.Key).Should().BeEquivalentTo(
            "Lemmy__InstanceUrl",
            "Lemmy__Community",
            "Lemmy__MaxPosts",
            "Lemmy__MaxComments");
    }

    // ---- GetCacheKey ----

    [Fact]
    public void GetCacheKey_ForEpisode_IncludesSeasonAndEpisode()
    {
        LemmyThoughtProvider provider = CreateProvider(new HttpClient());
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string key = provider.GetCacheKey(mediaContext);

        key.Should().Contain("S01E01");
        key.Should().Contain("Breaking Bad");
    }

    [Fact]
    public void GetCacheKey_ForMovie_ExcludesSeasonAndEpisodeFormat()
    {
        LemmyThoughtProvider provider = CreateProvider(new HttpClient());
        MediaContext mediaContext = new("Interstellar", new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero));

        string key = provider.GetCacheKey(mediaContext);

        key.Should().Contain("Interstellar");
        key.Should().NotMatchRegex(@"S\d{2}E\d{2}");
    }

    // ---- Movie context ----

    [Fact]
    public async Task GetThoughtsAsync_WithMovieContext_BuildsMovieQuery()
    {
        // Arrange
        MediaContext mediaContext = new("Interstellar", new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero));
        string searchJson = """{"posts": []}""";

        List<string> requestedUrls = [];
        MockHttpMessageHandler handler = new(req =>
        {
            requestedUrls.Add(req.RequestUri?.ToString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) };
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://lemmy.world") };

        LemmyThoughtProvider provider = CreateProvider(client);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        requestedUrls.Should().Contain(u => Uri.UnescapeDataString(u).Contains("Interstellar movie"));
    }
}
