using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Core.Services;
using WatchBack.Infrastructure.ThoughtProviders;
using WatchBack.Infrastructure.WatchStateProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;


/// <summary>
/// Live integration tests against real APIs.
/// These tests are marked with [Trait("Category", "Integration")] and should be run separately.
/// They require valid .env configuration (see .env at the solution root).
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class LiveIntegrationTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly HttpClient _httpClient = new();

    public LiveIntegrationTests() => LoadEnvFile();

    public void Dispose()
    {
        _cache.Dispose();
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void LoadEnvFile()
    {
        // Try to find .env relative to current directory or solution root
        var envPath = Path.Combine(Directory.GetCurrentDirectory(), "../../../../.env");
        if (!File.Exists(envPath))
        {
            // Fallback: look in sibling Python project
            var dir = Directory.GetCurrentDirectory();
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "WatchBack.sln")))
                {
                    var sibling = Path.Combine(Path.GetDirectoryName(dir)!, "WatchBack", ".env");
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
            foreach (var line in File.ReadAllLines(envPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var parts = line.Split('=', 2);
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
        var options = new JellyfinOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("JF_URL") ?? "http://192.168.1.158:8096",
            ApiKey = Environment.GetEnvironmentVariable("JF_API_KEY") ?? "",
            CacheTtlSeconds = 10
        };

        if (string.IsNullOrEmpty(options.ApiKey))
            return; // Skip: JF_API_KEY not set in environment

        var provider = new JellyfinWatchStateProvider(_httpClient, new OptionsSnapshotStub<JellyfinOptions>(options), _cache, Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();
        var health = await provider.GetServiceHealthAsync();

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
        var options = new TraktOptions
        {
            ClientId = Environment.GetEnvironmentVariable("TRAKT_CLIENT_ID") ?? "",
            AccessToken = Environment.GetEnvironmentVariable("TRAKT_ACCESS_TOKEN") ?? "",
            CacheTtlSeconds = 30
        };

        if (string.IsNullOrEmpty(options.ClientId))
            return; // Skip: TRAKT_CLIENT_ID not set in environment

        var provider = new TraktWatchStateProvider(_httpClient, new OptionsSnapshotStub<TraktOptions>(options), _cache, Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var health = await provider.GetServiceHealthAsync();

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
        var options = new TraktOptions
        {
            ClientId = Environment.GetEnvironmentVariable("TRAKT_CLIENT_ID") ?? "",
            AccessToken = Environment.GetEnvironmentVariable("TRAKT_ACCESS_TOKEN") ?? "",
            CacheTtlSeconds = 30
        };

        if (string.IsNullOrEmpty(options.ClientId))
            return; // Skip: TRAKT_CLIENT_ID not set in environment

        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var treeBuilder = new ReplyTreeBuilder();
        var provider = new TraktThoughtProvider(_httpClient, new OptionsSnapshotStub<TraktOptions>(options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.Should().NotBeNull();
        result.Should().NotBeNull();
        result!.Source.Should().Be("Trakt");
        // Note: Actual content may be empty if Trakt API is unavailable or has rate limiting
        // We're primarily validating the provider can be called without throwing
    }

    [Fact]
    public async Task RedditThoughts_GetThoughts_SearchWorks()
    {
        // Arrange
        var options = new RedditOptions
        {
            MaxThreads = 2,
            MaxComments = 100,
            CacheTtlSeconds = 3600
        };

        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var treeBuilder = new ReplyTreeBuilder();
        var provider = new RedditThoughtProvider(_httpClient, new OptionsSnapshotStub<RedditOptions>(options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.Should().NotBeNull();
        result.Should().NotBeNull();
        result!.Source.Should().Be("Reddit");
        // Note: PullPush API may be unavailable or rate limited. We're validating the provider
        // can be called without throwing and returns a valid ThoughtResult structure
    }

    [Fact]
    public async Task BlueskyThoughts_GetThoughts_SearchWorks()
    {
        // Arrange
        var options = new BlueskyOptions
        {
            Handle = Environment.GetEnvironmentVariable("BSKY_IDENTIFIER") ?? "arishaig.bsky.social",
            AppPassword = Environment.GetEnvironmentVariable("BSKY_APP_PASSWORD"),
            TokenCacheTtlSeconds = 5400
        };

        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var treeBuilder = new ReplyTreeBuilder();
        var provider = new BlueskyThoughtProvider(_httpClient, new OptionsSnapshotStub<BlueskyOptions>(options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.Should().NotBeNull();
        result.Should().NotBeNull();
        result!.Source.Should().Be("Bluesky");
        // Note: Auth may fail if credentials are invalid. We're validating provider integration
        // works without exceptions and returns valid result structure
    }

    [Fact]
    public async Task AllProviders_CanBeInstantiatedWithEnvConfig()
    {
        // This test verifies that all providers can be created with environment variable configuration
        // and that they respond to health checks (doesn't require specific watch state or thoughts)

        // Jellyfin
        var jellyfinOpts = new JellyfinOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("JF_URL") ?? "http://192.168.1.158:8096",
            ApiKey = Environment.GetEnvironmentVariable("JF_API_KEY") ?? ""
        };
        var jellyfinProvider = new JellyfinWatchStateProvider(_httpClient, new OptionsSnapshotStub<JellyfinOptions>(jellyfinOpts), _cache, Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinWatchStateProvider>.Instance);

        // Trakt
        var traktOpts = new TraktOptions
        {
            ClientId = Environment.GetEnvironmentVariable("TRAKT_CLIENT_ID") ?? "",
            AccessToken = Environment.GetEnvironmentVariable("TRAKT_ACCESS_TOKEN") ?? ""
        };
        var traktWatchProvider = new TraktWatchStateProvider(_httpClient, new OptionsSnapshotStub<TraktOptions>(traktOpts), _cache, Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktWatchStateProvider>.Instance);
        var traktThoughtProvider = new TraktThoughtProvider(
            _httpClient, new OptionsSnapshotStub<TraktOptions>(traktOpts), _cache, new ReplyTreeBuilder(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TraktThoughtProvider>.Instance);

        // Bluesky
        var blueskyOpts = new BlueskyOptions
        {
            Handle = Environment.GetEnvironmentVariable("BSKY_IDENTIFIER") ?? "",
            AppPassword = Environment.GetEnvironmentVariable("BSKY_APP_PASSWORD")
        };
        var blueskyProvider = new BlueskyThoughtProvider(
            _httpClient, new OptionsSnapshotStub<BlueskyOptions>(blueskyOpts), _cache, new ReplyTreeBuilder(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BlueskyThoughtProvider>.Instance);

        // Reddit
        var redditOpts = new RedditOptions();
        var redditProvider = new RedditThoughtProvider(
            _httpClient, new OptionsSnapshotStub<RedditOptions>(redditOpts), _cache, new ReplyTreeBuilder(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        // Act - all providers should handle health checks without throwing
        var jellyfinHealth = await jellyfinProvider.GetServiceHealthAsync();
        var traktWatchHealth = await traktWatchProvider.GetServiceHealthAsync();
        var traktThoughtHealth = await traktThoughtProvider.GetServiceHealthAsync();
        var blueskyHealth = await blueskyProvider.GetServiceHealthAsync();
        var redditHealth = await redditProvider.GetServiceHealthAsync();

        // Assert - just verify we got health responses (they may be healthy or unhealthy depending on config)
        jellyfinHealth.Should().NotBeNull();
        traktWatchHealth.Should().NotBeNull();
        traktThoughtHealth.Should().NotBeNull();
        blueskyHealth.Should().NotBeNull();
        redditHealth.Should().NotBeNull();
    }
}