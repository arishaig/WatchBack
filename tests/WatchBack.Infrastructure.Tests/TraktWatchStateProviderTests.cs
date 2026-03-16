using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.WatchStateProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;


public class TraktWatchStateProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly TraktOptions _options;

    public TraktWatchStateProviderTests()
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
    public async Task GetCurrentMediaContextAsync_WithNoActivePlayback_ReturnsNull()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithActiveEpisode_ReturnsEpisodeContext()
    {
        // Arrange
        var responseJson = """
            {
                "show": {
                    "title": "Breaking Bad"
                },
                "episode": {
                    "season": 2,
                    "number": 5,
                    "title": "Breakage",
                    "ids": { "trakt": 73608 },
                    "first_aired": "2009-04-12T00:00:00Z"
                }
            }
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<EpisodeContext>();
        var episode = (EpisodeContext)result!;
        episode.Title.Should().Be("Breaking Bad");
        episode.EpisodeTitle.Should().Be("Breakage");
        episode.SeasonNumber.Should().Be(2);
        episode.EpisodeNumber.Should().Be(5);
        episode.ReleaseDate.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithoutFirstAired_ReturnsNullReleaseDate()
    {
        // Arrange
        var responseJson = """
            {
                "show": {
                    "title": "Breaking Bad"
                },
                "episode": {
                    "season": 1,
                    "number": 1,
                    "title": "Pilot",
                    "ids": { "trakt": 73605 }
                }
            }
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().NotBeNull();
        result!.ReleaseDate.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_SecondCall_UsesCacheWithinTtl()
    {
        // Arrange
        var responseJson = """
            {
                "show": {
                    "title": "Breaking Bad"
                },
                "episode": {
                    "season": 1,
                    "number": 1,
                    "title": "Pilot",
                    "ids": { "trakt": 73605 }
                }
            }
            """;

        var callCount = 0;
        var handler = new MockHttpMessageHandler(
            () =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson)
                };
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var result1 = await provider.GetCurrentMediaContextAsync();
        var result2 = await provider.GetCurrentMediaContextAsync();

        // Assert
        callCount.Should().Be(1); // Cache hit on second call
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
    }

    [Fact]
    public void Metadata_SupportedExternalIds_ContainsImdbTmdbTvdb()
    {
        // Arrange
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Assert
        provider.Metadata.Should().BeOfType<WatchStateDataProviderMetadata>();
        var meta = (WatchStateDataProviderMetadata)provider.Metadata;
        meta.SupportedExternalIds.Should().BeEquivalentTo([ExternalIdType.Imdb, ExternalIdType.Tmdb, ExternalIdType.Tvdb]);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithAllShowIds_PopulatesExternalIds()
    {
        // Arrange
        var responseJson = """
            {
                "show": {
                    "title": "Breaking Bad",
                    "ids": { "imdb": "tt0903747", "tmdb": 1396, "tvdb": 81189 }
                },
                "episode": {
                    "season": 1,
                    "number": 1,
                    "title": "Pilot"
                }
            }
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        var episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().NotBeNull();
        episode.ExternalIds![ExternalIdType.Imdb].Should().Be("tt0903747");
        episode.ExternalIds[ExternalIdType.Tmdb].Should().Be("1396");
        episode.ExternalIds[ExternalIdType.Tvdb].Should().Be("81189");
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithPartialShowIds_PopulatesOnlyAvailableIds()
    {
        // Arrange
        var responseJson = """
            {
                "show": {
                    "title": "Breaking Bad",
                    "ids": { "imdb": "tt0903747" }
                },
                "episode": {
                    "season": 1,
                    "number": 1,
                    "title": "Pilot"
                }
            }
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        var episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().NotBeNull();
        episode.ExternalIds![ExternalIdType.Imdb].Should().Be("tt0903747");
        episode.ExternalIds.Should().NotContainKey(ExternalIdType.Tmdb);
        episode.ExternalIds.Should().NotContainKey(ExternalIdType.Tvdb);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithNoShowIds_ReturnsNullExternalIds()
    {
        // Arrange
        var responseJson = """
            {
                "show": {
                    "title": "Breaking Bad"
                },
                "episode": {
                    "season": 1,
                    "number": 1,
                    "title": "Pilot"
                }
            }
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        var episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().BeNull();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WithSuccess_ReturnsHealthy()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WithFailure_ReturnsUnhealthy()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        var provider = new TraktWatchStateProvider(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeFalse();
    }
}