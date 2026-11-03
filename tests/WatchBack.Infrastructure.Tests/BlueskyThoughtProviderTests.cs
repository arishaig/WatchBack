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
        AppPassword = "test-password"
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

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("createSession", tokenJson)
            .Default(searchJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

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

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("createSession", tokenJson)
            .Default(searchJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        List<Thought> capturedThoughts = new();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => capturedThoughts.AddRange(x)))
            .Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        // Duplicate posts should be deduplicated
        capturedThoughts.Should().HaveCount(1);
    }

    // Regression: with no credentials the provider searched unauthenticated, which searchPosts rejects,
    // and never cached the failure, so the 5-second poll loop sent a doomed request every cycle.
    [Fact]
    public async Task GetThoughtsAsync_WithoutCredentials_MakesNoRequests()
    {
        BlueskyOptions optionsWithoutCreds = new() { Handle = "", AppPassword = null };
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        int requests = 0;
        MockHttpMessageHandler handler = new(() =>
        {
            Interlocked.Increment(ref requests);
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });
        BlueskyThoughtProvider provider = new(new HttpClient(handler),
            new OptionsSnapshotStub<BlueskyOptions>(optionsWithoutCreds),
            _cache, Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> ticks = new();
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        result!.Source.Should().Be("Bluesky");
        result.Thoughts.Should().BeEmpty();
        requests.Should().Be(0);
        ticks.Should().BeEmpty("an unconfigured provider does no work, so it must not move the loading bar");
    }

    // Regression: a failed search wasn't cached, so a failing Bluesky was re-queried on every poll cycle.
    [Fact]
    public async Task GetThoughtsAsync_WhenSearchFails_DoesNotRetryOnNextPoll()
    {
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        int searches = 0;
        MockHttpMessageHandler handler = new(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("createSession"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"accessJwt":"test-token"}""")
                };
            }

            Interlocked.Increment(ref searches);
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });
        BlueskyThoughtProvider provider = new(new HttpClient(handler),
            new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        ThoughtResult? first = await provider.GetThoughtsAsync(mediaContext);
        int searchesAfterFirst = searches;
        ThoughtResult? second = await provider.GetThoughtsAsync(mediaContext);

        searches.Should().Be(searchesAfterFirst);
        first!.IsTransientFailure.Should().BeTrue();
        second!.IsTransientFailure.Should().BeTrue("the briefly cached failure must still read as a failure");
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

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("createSession", tokenJson)
            .Default(searchJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

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

    // Regression: the SSE loop polls every few seconds and the frontend shows the
    // loading bar whenever it sees intermediate progress ticks. A cache hit must
    // report zero ticks, or the bar flashes on every poll cycle.
    [Fact]
    public async Task GetThoughtsAsync_OnCacheHit_ReportsNoTicks()
    {
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string tokenJson = """{"accessJwt":"test-token"}""";
        string searchJson = """{"posts":[]}""";

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("createSession", tokenJson)
            .Default(searchJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        ConcurrentBag<SyncProgressTick> ticks = new();
        await provider.GetThoughtsAsync(mediaContext, new CapturingProgress(ticks));

        ticks.Should().BeEmpty("a cached cycle does no work and must not arm the frontend loading bar");
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

    // ---- Movie and season-0 / dateless-episode handling ----

    [Fact]
    public async Task GetThoughtsAsync_WithMovieContext_UsesMovieQuery()
    {
        MediaContext mediaContext = new(
            "Interstellar",
            new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero));

        string tokenJson = """{"accessJwt":"test-token"}""";
        string searchJson = """{"posts":[]}""";

        List<string> searchedUrls = new();
        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
        ]);

        MockHttpMessageHandler handler = new(req =>
        {
            searchedUrls.Add(req.RequestUri?.ToString() ?? "");
            return responses.Dequeue();
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        searchedUrls.Should().Contain(u => Uri.UnescapeDataString(u).Contains("Interstellar movie"));
    }

    // Cross-season scoping: the search query sent to Bluesky must be scoped to the specific season
    // and episode being fetched. If BuildTextQuery ever regressed for episode contexts, Bluesky
    // would silently search for the wrong content with no other guard to catch it.
    [Fact]
    public async Task GetThoughtsAsync_ForSeasonTwoEpisode_SearchUrlContainsSeason2EpisodeCode()
    {
        EpisodeContext mediaContext = new(
            "The Pitt",
            new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
            "Mass Casualty",
            2,
            1);

        string tokenJson = """{"accessJwt":"test-token"}""";
        string searchJson = """{"posts":[]}""";

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("createSession", tokenJson)
            .RespondTo("searchPosts", searchJson)
            .Default(searchJson);

        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        // The search request must contain the S02E01 episode code
        handler.RecordedUris
            .Where(u => u.ToString().Contains("searchPosts"))
            .Should().ContainSingle(u => Uri.UnescapeDataString(u.ToString()).Contains("\"The Pitt\" S02E01"),
                "query must be scoped to season 2 episode 1");

        // Must not accidentally query for a different season
        handler.RecordedUris
            .Where(u => u.ToString().Contains("searchPosts"))
            .Should().NotContain(u => Uri.UnescapeDataString(u.ToString()).Contains("S01E01"));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithSeasonZeroEpisode_WithDate_UsesDateQuery()
    {
        EpisodeContext mediaContext = new(
            "The Daily Show",
            new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "January 20, 2026",
            0,
            1);

        string tokenJson = """{"accessJwt":"test-token"}""";
        string searchJson = """{"posts":[]}""";

        List<string> searchedUrls = new();
        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
        ]);

        MockHttpMessageHandler handler = new(req =>
        {
            searchedUrls.Add(req.RequestUri?.ToString() ?? "");
            return responses.Dequeue();
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());

        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        searchedUrls.Should().Contain(u => Uri.UnescapeDataString(u).Contains("January 20, 2026"));
        searchedUrls.Should().NotContain(u => Uri.UnescapeDataString(u).Contains("S00"));
    }

    // ---- Rate-limiting (T8) ----

    [Fact]
    public async Task GetThoughtsAsync_When429OnSearch_ReturnsEmptyResultWithoutThrowing()
    {
        // Arrange — auth succeeds, search returns 429
        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        string tokenJson = """{"accessJwt":"test-token"}""";

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJson) },
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert — must not throw; returns an empty (non-null) result
        result.Should().NotBeNull();
        result!.Source.Should().Be("Bluesky");
        result.Thoughts.Should().BeEmpty();
    }

    // Regression: if Bluesky rotates/invalidates a cached JWT before it expires, the search returns 401 and we previously dropped results entirely. We should
    // evict the cached token and retry once with a fresh session.
    [Fact]
    public async Task GetThoughtsAsync_When401OnSearch_EvictsTokenAndRetriesOnce()
    {
        DateTimeOffset releaseDate = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // "Episode 1" is a placeholder name and 2020 predates Bluesky: one search, so the sequence is exact.
        EpisodeContext mediaContext = new("Breaking Bad", releaseDate, "Episode 1", 1, 1);

        string tokenJsonA = """{"accessJwt":"stale-token"}""";
        string tokenJsonB = """{"accessJwt":"fresh-token"}""";
        string searchJson = """
                            {
                                "posts": [
                                    {
                                        "uri": "at://did/app.bsky.feed.post/123",
                                        "author": { "handle": "u.bsky.social", "displayName": "U" },
                                        "record": { "text": "Great!", "createdAt": "2020-02-01T00:00:00Z" },
                                        "likeCount": 1
                                    }
                                ]
                            }
                            """;

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJsonA) },
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tokenJsonB) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) }
        ]);

        List<(string? Method, string? Url, string? Auth)> recorded = new();
        MockHttpMessageHandler handler = new(req =>
        {
            recorded.Add((req.Method.Method, req.RequestUri?.ToString(),
                req.Headers.Authorization?.Parameter));
            return responses.Dequeue();
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://bsky.social") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        BlueskyThoughtProvider provider = new(client, new OptionsSnapshotStub<BlueskyOptions>(_options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result!.Thoughts.Should().HaveCount(1);
        // Sequence: createSession (stale) → search (401) → createSession (fresh) → search (200)
        recorded.Should().HaveCount(4);
        recorded[0].Url.Should().Contain("createSession");
        recorded[1].Url.Should().Contain("searchPosts");
        recorded[1].Auth.Should().Be("stale-token");
        recorded[2].Url.Should().Contain("createSession");
        recorded[3].Url.Should().Contain("searchPosts");
        recorded[3].Auth.Should().Be("fresh-token");
    }

    // ---- Phase 3: search plan, session refresh ----

    private static readonly DateTimeOffset s_now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlanSearches_OldEpisode_TopPosts_CodeThenTitleThenHashtagWhileItAired()
    {
        DateTimeOffset aired = new(2025, 1, 17, 2, 0, 0, TimeSpan.Zero);
        EpisodeContext episode = new("Severance", aired, "Hello, Ms. Cobel", 2, 1);

        IReadOnlyList<BlueskyThoughtProvider.SearchSpec> plan = BlueskyThoughtProvider.PlanSearches(episode, s_now);

        plan.Should().Equal(
            new BlueskyThoughtProvider.SearchSpec("\"Severance\" S02E01", "top", aired.AddDays(-7), null),
            new BlueskyThoughtProvider.SearchSpec("\"Severance\" \"Hello, Ms. Cobel\"", "top", aired.AddDays(-7), null),
            new BlueskyThoughtProvider.SearchSpec("#Severance", "top", aired.AddHours(-12), aired.AddDays(2)));
        plan.Count.Should().BeLessThanOrEqualTo(BlueskyThoughtProvider.MaxSearches);
    }

    [Fact]
    public void PlanSearches_RecentEpisode_LatestPosts()
    {
        EpisodeContext episode = new("The Pitt", s_now.AddDays(-2), "8:00 A.M.", 2, 3);

        BlueskyThoughtProvider.PlanSearches(episode, s_now).Take(2).Should().OnlyContain(p => p.Sort == "latest");
    }

    [Theory]
    [InlineData("Episode 4")]
    [InlineData("Chapter 12")]
    [InlineData("")]
    public void PlanSearches_PlaceholderEpisodeName_SkipsTheTitleSearch(string name)
    {
        EpisodeContext episode = new("Shōgun", s_now.AddDays(-30), name, 1, 4);

        BlueskyThoughtProvider.PlanSearches(episode, s_now).Select(p => p.Query)
            .Should().Equal("\"Shōgun\" S01E04", "#Shōgun");
    }

    [Fact]
    public void PlanSearches_AiredBeforeBluesky_NoHashtagSearch()
    {
        EpisodeContext episode = new("Lost", new DateTimeOffset(2004, 9, 22, 0, 0, 0, TimeSpan.Zero), "Pilot (1)", 1, 1);

        BlueskyThoughtProvider.PlanSearches(episode, s_now).Should().NotContain(p => p.Query.StartsWith('#'));
    }

    [Fact]
    public void PlanSearches_Movie_OneSearch()
    {
        MediaContext movie = new("Interstellar", new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero));

        BlueskyThoughtProvider.PlanSearches(movie, s_now).Should().ContainSingle()
            .Which.Should().Be(new BlueskyThoughtProvider.SearchSpec("Interstellar movie", "top",
                movie.ReleaseDate!.Value.AddDays(-7), null));
    }

    [Fact]
    public async Task Search_SendsSortSinceAndUntil()
    {
        DateTimeOffset aired = new(2025, 1, 17, 2, 0, 0, TimeSpan.Zero);
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("createSession", """{"accessJwt":"t"}""")
            .Default("""{"posts":[]}""");
        BlueskyThoughtProvider provider = new(new HttpClient(handler), new OptionsSnapshotStub<BlueskyOptions>(_options),
            _cache, Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(new EpisodeContext("Severance", aired, "Hello, Ms. Cobel", 2, 1));

        handler.RecordedUris.Select(u => Uri.UnescapeDataString(u.Query)).Should().Contain(
            "?q=#Severance&sort=top&limit=100&since=2025-01-16T14:00:00Z&until=2025-01-19T02:00:00Z");
    }

    private static string Jwt(DateTimeOffset expires) =>
        "eyJhbGciOiJIUzI1NiJ9." +
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($$"""{"exp":{{expires.ToUnixTimeSeconds()}}}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    [Fact]
    public void JwtExpiry_ReadsTheExpClaim()
    {
        DateTimeOffset expires = new(2026, 11, 3, 14, 0, 0, TimeSpan.Zero);

        BlueskyThoughtProvider.JwtExpiry(Jwt(expires)).Should().Be(expires);
        BlueskyThoughtProvider.JwtExpiry("not-a-jwt").Should().BeNull();
    }

    // Regression: the access token was dropped every 90 minutes and a new session created with the app
    // password. bsky.social allows an account only a few sign-ins a day (its 429 said "10;w=86400"), so
    // a running instance used them up and Bluesky went dark until the next day. Refresh instead.
    [Fact]
    public async Task ExpiringAccessToken_IsRefreshed_NotSignedInAgain()
    {
        List<string> calls = [];
        int session = 0;
        MockHttpMessageHandler handler = new(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            calls.Add($"{path} {req.Headers.Authorization?.Parameter}");
            return path switch
            {
                "/xrpc/com.atproto.server.createSession" => Ok(
                    $$"""{"accessJwt":"{{Jwt(DateTimeOffset.UtcNow.AddMinutes(1))}}","refreshJwt":"refresh-1"}"""),
                "/xrpc/com.atproto.server.refreshSession" => Ok(
                    $$"""{"accessJwt":"{{Jwt(DateTimeOffset.UtcNow.AddHours(2))}}","refreshJwt":"refresh-{{++session + 1}}"}"""),
                _ => Ok("""{"posts":[]}""")
            };
        });
        BlueskyThoughtProvider provider = new(new HttpClient(handler), new OptionsSnapshotStub<BlueskyOptions>(_options),
            _cache, Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        // The first token expires within the safety margin, so the next lookup refreshes it.
        await provider.GetThoughtsAsync(new MediaContext("Interstellar", null));
        await provider.GetThoughtsAsync(new MediaContext("Arrival", null));
        await provider.GetThoughtsAsync(new MediaContext("Dune", null));

        calls.Count(c => c.Contains("createSession", StringComparison.Ordinal)).Should().Be(1);
        calls.Should().ContainSingle(c => c.Contains("refreshSession", StringComparison.Ordinal))
            .Which.Should().EndWith("refresh-1");
    }

    [Fact]
    public async Task ExpiredTokenError_RefreshesAndRetriesTheSearch()
    {
        int searches = 0;
        MockHttpMessageHandler handler = new(req => req.RequestUri!.AbsolutePath switch
        {
            "/xrpc/com.atproto.server.createSession" => Ok("""{"accessJwt":"a1","refreshJwt":"r1"}"""),
            "/xrpc/com.atproto.server.refreshSession" => Ok("""{"accessJwt":"a2","refreshJwt":"r2"}"""),
            _ => ++searches == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"ExpiredToken","message":"Token has expired"}""")
                }
                : Ok("""{"posts":[{"uri":"at://did/app.bsky.feed.post/1","record":{"text":"hi","createdAt":"2026-01-01T00:00:00Z"}}]}""")
        });
        IReplyTreeBuilder tree = Substitute.For<IReplyTreeBuilder>();
        tree.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => x.ArgAt<IEnumerable<Thought>>(0).ToList());
        BlueskyThoughtProvider provider = new(new HttpClient(handler), new OptionsSnapshotStub<BlueskyOptions>(_options),
            _cache, tree, NullLogger<BlueskyThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(new MediaContext("Interstellar", null));

        result!.IsTransientFailure.Should().BeFalse();
        result.Thoughts.Should().ContainSingle();
    }

    // A refresh that fails for a passing reason (outage, rate limit) must not cost a sign-in or lose the
    // refresh token: there are only a few sign-ins a day.
    [Fact]
    public async Task RefreshOutage_KeepsTheSession_AndDoesNotSignInAgain()
    {
        List<string> calls = [];
        int refreshes = 0;
        MockHttpMessageHandler handler = new(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            calls.Add($"{path} {req.Headers.Authorization?.Parameter}");
            return path switch
            {
                "/xrpc/com.atproto.server.createSession" => Ok(
                    $$"""{"accessJwt":"{{Jwt(DateTimeOffset.UtcNow.AddMinutes(1))}}","refreshJwt":"r1"}"""),
                "/xrpc/com.atproto.server.refreshSession" => ++refreshes == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Ok($$"""{"accessJwt":"{{Jwt(DateTimeOffset.UtcNow.AddHours(2))}}","refreshJwt":"r2"}"""),
                _ => Ok("""{"posts":[]}""")
            };
        });
        BlueskyThoughtProvider provider = new(new HttpClient(handler), new OptionsSnapshotStub<BlueskyOptions>(_options),
            _cache, Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(new MediaContext("Interstellar", null));
        ThoughtResult? duringOutage = await provider.GetThoughtsAsync(new MediaContext("Arrival", null));
        ThoughtResult? after = await provider.GetThoughtsAsync(new MediaContext("Dune", null));

        duringOutage!.IsTransientFailure.Should().BeTrue();
        after!.IsTransientFailure.Should().BeFalse();
        calls.Count(c => c.Contains("createSession", StringComparison.Ordinal)).Should().Be(1);
        calls.Where(c => c.Contains("refreshSession", StringComparison.Ordinal))
            .Should().Equal("/xrpc/com.atproto.server.refreshSession r1", "/xrpc/com.atproto.server.refreshSession r1");
    }

    [Fact]
    public async Task RejectedRefreshToken_SignsInAgain()
    {
        List<string> calls = [];
        MockHttpMessageHandler handler = new(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            calls.Add(path);
            return path switch
            {
                "/xrpc/com.atproto.server.createSession" => Ok(
                    $$"""{"accessJwt":"{{Jwt(DateTimeOffset.UtcNow.AddMinutes(1))}}","refreshJwt":"r1"}"""),
                "/xrpc/com.atproto.server.refreshSession" => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"ExpiredToken","message":"Token has been revoked"}""")
                },
                _ => Ok("""{"posts":[]}""")
            };
        });
        BlueskyThoughtProvider provider = new(new HttpClient(handler), new OptionsSnapshotStub<BlueskyOptions>(_options),
            _cache, Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(new MediaContext("Interstellar", null));
        ThoughtResult? second = await provider.GetThoughtsAsync(new MediaContext("Arrival", null));

        second!.IsTransientFailure.Should().BeFalse();
        calls.Count(c => c.Contains("createSession", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task SearchRejected_CountsAsNothingFound_NotAsAFailure()
    {
        MockHttpMessageHandler handler = new(req => req.RequestUri!.AbsolutePath switch
        {
            "/xrpc/com.atproto.server.createSession" => Ok("""{"accessJwt":"a1","refreshJwt":"r1"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"InvalidRequest","message":"Invalid sort"}""")
            }
        });
        BlueskyThoughtProvider provider = new(new HttpClient(handler), new OptionsSnapshotStub<BlueskyOptions>(_options),
            _cache, Substitute.For<IReplyTreeBuilder>(), NullLogger<BlueskyThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(new MediaContext("Interstellar", null));

        result!.IsTransientFailure.Should().BeFalse("retrying a rejected search every minute can't help");
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
}
