using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Core.Services;
using WatchBack.Infrastructure.ThoughtProviders;
using WatchBack.Infrastructure.WatchStateProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

/// <summary>
///     Live integration tests against real APIs.
///     These tests are marked with [Trait("Category", "Integration")] and should be run separately.
///     They require valid .env configuration (see .env at the solution root).
///     Run with: dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class LiveIntegrationTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly HttpClient _httpClient = new();

    public LiveIntegrationTests()
    {
        LoadEnvFile();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void LoadEnvFile()
    {
        // Try to find .env relative to current directory or solution root
        string envPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../../.env");
        if (!File.Exists(envPath))
        {
            // Fallback: look in sibling Python project
            string? dir = Directory.GetCurrentDirectory();
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "WatchBack.sln")))
                {
                    string sibling = Path.Combine(Path.GetDirectoryName(dir)!, "WatchBack", ".env");
                    if (File.Exists(sibling))
                    {
                        envPath = sibling;
                        break;
                    }
                }

                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        if (File.Exists(envPath))
        {
            foreach (string line in File.ReadAllLines(envPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                {
                    continue;
                }

                string[] parts = line.Split('=', 2);
                if (parts.Length == 2)
                {
                    Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
                }
            }
        }
    }

    [Fact]
    public async Task JellyfinWatchState_GetCurrentMediaContext_ReturnsDataOrNull()
    {
        // Arrange
        JellyfinOptions options = new()
        {
            BaseUrl = Environment.GetEnvironmentVariable("JF_URL") ?? "http://192.168.1.158:8096",
            ApiKey = Environment.GetEnvironmentVariable("JF_API_KEY") ?? "",
            CacheTtlSeconds = 10
        };

        if (string.IsNullOrEmpty(options.ApiKey))
        {
            return; // Skip: JF_API_KEY not set in environment
        }

        JellyfinWatchStateProvider provider = new(_httpClient, new OptionsSnapshotStub<JellyfinOptions>(options),
            _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue($"Jellyfin health check failed: {health.Message}");
        // Result could be null (idle) or an EpisodeContext
        if (result != null)
        {
            result.Should().BeOfType<EpisodeContext>();
            ((EpisodeContext)result).Title.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task TraktWatchState_GetServiceHealth_ReturnsValidResponse()
    {
        // Arrange
        TraktOptions options = new()
        {
            ClientId = Environment.GetEnvironmentVariable("TRAKT_CLIENT_ID") ?? "",
            AccessToken = Environment.GetEnvironmentVariable("TRAKT_ACCESS_TOKEN") ?? "",
            CacheTtlSeconds = 30
        };

        if (string.IsNullOrEmpty(options.ClientId))
        {
            return; // Skip: TRAKT_CLIENT_ID not set in environment
        }

        TraktWatchStateProvider provider = new(_httpClient, new OptionsSnapshotStub<TraktOptions>(options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.Should().NotBeNull();
        health.CheckedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        // Note: IsHealthy may be false due to API changes, auth issues, or rate limiting
        // We're just verifying the provider can call the API and return a response
    }

    [Fact]
    public async Task TraktThoughts_GetThoughts_SearchWorks()
    {
        // Arrange
        TraktOptions options = new()
        {
            ClientId = Environment.GetEnvironmentVariable("TRAKT_CLIENT_ID") ?? "",
            AccessToken = Environment.GetEnvironmentVariable("TRAKT_ACCESS_TOKEN") ?? "",
            CacheTtlSeconds = 30
        };

        if (string.IsNullOrEmpty(options.ClientId))
        {
            return; // Skip: TRAKT_CLIENT_ID not set in environment
        }

        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        ReplyTreeBuilder treeBuilder = new();
        TraktThoughtProvider provider = new(_httpClient, new OptionsSnapshotStub<TraktOptions>(options), _cache,
            treeBuilder, NullLogger<TraktThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.Should().NotBeNull();
        result.Should().NotBeNull();
        result.Source.Should().Be("Trakt");
        // Note: Actual content may be empty if Trakt API is unavailable or has rate limiting
        // We're primarily validating the provider can be called without throwing
    }

    [Fact]
    public async Task RedditThoughts_GetThoughts_SearchWorks()
    {
        // Arrange
        RedditOptions options = new() { MaxThreads = 2, MaxComments = 100, CacheTtlSeconds = 3600 };

        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        ReplyTreeBuilder treeBuilder = new();
        RedditThoughtProvider provider = new(_httpClient, new OptionsSnapshotStub<RedditOptions>(options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.Should().NotBeNull();
        result.Should().NotBeNull();
        result.Source.Should().Be("Reddit");
        // Note: PullPush API may be unavailable or rate limited. We're validating the provider
        // can be called without throwing and returns a valid ThoughtResult structure
    }

    [Fact]
    public async Task BlueskyThoughts_GetThoughts_SearchWorks()
    {
        // Arrange
        BlueskyOptions options = new()
        {
            Handle = Environment.GetEnvironmentVariable("BSKY_IDENTIFIER") ?? "arishaig.bsky.social",
            AppPassword = Environment.GetEnvironmentVariable("BSKY_APP_PASSWORD"),
            TokenCacheTtlSeconds = 5400
        };

        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        ReplyTreeBuilder treeBuilder = new();
        BlueskyThoughtProvider provider = new(_httpClient, new OptionsSnapshotStub<BlueskyOptions>(options), _cache,
            treeBuilder, NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.Should().NotBeNull();
        result.Should().NotBeNull();
        result.Source.Should().Be("Bluesky");
        // Note: Auth may fail if credentials are invalid. We're validating provider integration
        // works without exceptions and returns valid result structure
    }

    [Fact]
    public async Task LemmyThoughts_GetThoughts_ApiReachableAndReturnsValidStructure()
    {
        // Arrange — no auth required, uses public lemmy.world API.
        // Lemmy TV discussions are typically season/show-level rather than per-episode,
        // so we verify the provider round-trips correctly without asserting on result count.
        LemmyOptions options = new()
        {
            InstanceUrl = "https://lemmy.world",
            MaxPosts = 3,
            MaxComments = 50,
            CacheTtlSeconds = 3600
        };

        EpisodeContext mediaContext = new(
            "The Bear",
            new DateTimeOffset(2022, 6, 23, 0, 0, 0, TimeSpan.Zero),
            "Brigade",
            1,
            3);

        ReplyTreeBuilder treeBuilder = new();
        LemmyThoughtProvider provider = new(_httpClient, new OptionsSnapshotStub<LemmyOptions>(options), _cache,
            treeBuilder, NullLogger<LemmyThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue($"lemmy.world health check failed: {health.Message}");
        result.Should().NotBeNull();
        result!.Source.Should().Be("Lemmy");
        // Thoughts may be empty — episode-level posts don't reliably exist on Lemmy
    }

    [Fact]
    public async Task AllProviders_CanBeInstantiatedWithEnvConfig()
    {
        // This test verifies that all providers can be created with environment variable configuration
        // and that they respond to health checks (doesn't require specific watch state or thoughts)

        // Jellyfin
        JellyfinOptions jellyfinOpts = new()
        {
            BaseUrl = Environment.GetEnvironmentVariable("JF_URL") ?? "http://192.168.1.158:8096",
            ApiKey = Environment.GetEnvironmentVariable("JF_API_KEY") ?? ""
        };
        JellyfinWatchStateProvider jellyfinProvider = new(_httpClient,
            new OptionsSnapshotStub<JellyfinOptions>(jellyfinOpts), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Trakt
        TraktOptions traktOpts = new()
        {
            ClientId = Environment.GetEnvironmentVariable("TRAKT_CLIENT_ID") ?? "",
            AccessToken = Environment.GetEnvironmentVariable("TRAKT_ACCESS_TOKEN") ?? ""
        };
        TraktWatchStateProvider traktWatchProvider = new(_httpClient, new OptionsSnapshotStub<TraktOptions>(traktOpts),
            _cache, NullLogger<TraktWatchStateProvider>.Instance);
        TraktThoughtProvider traktThoughtProvider = new(
            _httpClient, new OptionsSnapshotStub<TraktOptions>(traktOpts), _cache, new ReplyTreeBuilder(),
            NullLogger<TraktThoughtProvider>.Instance);

        // Bluesky
        BlueskyOptions blueskyOpts = new()
        {
            Handle = Environment.GetEnvironmentVariable("BSKY_IDENTIFIER") ?? "",
            AppPassword = Environment.GetEnvironmentVariable("BSKY_APP_PASSWORD")
        };
        BlueskyThoughtProvider blueskyProvider = new(
            _httpClient, new OptionsSnapshotStub<BlueskyOptions>(blueskyOpts), _cache, new ReplyTreeBuilder(),
            NullLogger<BlueskyThoughtProvider>.Instance);

        // Reddit
        RedditOptions redditOpts = new();
        RedditThoughtProvider redditProvider = new(
            _httpClient, new OptionsSnapshotStub<RedditOptions>(redditOpts), _cache, new ReplyTreeBuilder(),
            NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        // Act - all providers should handle health checks without throwing
        ServiceHealth jellyfinHealth = await jellyfinProvider.GetServiceHealthAsync();
        ServiceHealth traktWatchHealth = await traktWatchProvider.GetServiceHealthAsync();
        ServiceHealth traktThoughtHealth = await traktThoughtProvider.GetServiceHealthAsync();
        ServiceHealth blueskyHealth = await blueskyProvider.GetServiceHealthAsync();
        ServiceHealth redditHealth = await redditProvider.GetServiceHealthAsync();

        // Assert - just verify we got health responses (they may be healthy or unhealthy depending on config)
        jellyfinHealth.Should().NotBeNull();
        traktWatchHealth.Should().NotBeNull();
        traktThoughtHealth.Should().NotBeNull();
        blueskyHealth.Should().NotBeNull();
        redditHealth.Should().NotBeNull();
    }

    private static ISubredditMappingService NoMappings()
    {
        ISubredditMappingService svc = Substitute.For<ISubredditMappingService>();
        svc.GetSubreddits(Arg.Any<MediaContext>()).Returns([]);
        return svc;
    }
}
