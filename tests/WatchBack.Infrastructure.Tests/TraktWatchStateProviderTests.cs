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

    // ---- Provider self-description ----

    [Fact]
    public void ConfigSection_IsTrakt()
    {
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        provider.ConfigSection.Should().Be("Trakt");
    }

    [Fact]
    public void IsConfigured_WhenClientIdSet_IsTrue()
    {
        var opts = new TraktOptions { ClientId = "abc", Username = "", AccessToken = _options.AccessToken, CacheTtlSeconds = _options.CacheTtlSeconds };
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenUsernameSet_IsTrue()
    {
        var opts = new TraktOptions { ClientId = "", Username = "myuser", AccessToken = _options.AccessToken, CacheTtlSeconds = _options.CacheTtlSeconds };
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenBothEmpty_IsFalse()
    {
        var opts = new TraktOptions { ClientId = "", Username = "", AccessToken = _options.AccessToken, CacheTtlSeconds = _options.CacheTtlSeconds };
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void GetConfigSchema_ReturnsThreeFields()
    {
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        var fields = provider.GetConfigSchema(_ => "", (_, _) => false);

        fields.Should().HaveCount(3);
        fields.Select(f => f.Key).Should().BeEquivalentTo(
            ["Trakt__ClientId", "Trakt__AccessToken", "Trakt__Username"]);
    }

    [Fact]
    public void GetConfigSchema_AccessTokenField_IsPasswordType()
    {
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        var tokenField = provider.GetConfigSchema(_ => "", (_, _) => false)
            .First(f => f.Key == "Trakt__AccessToken");

        tokenField.Type.Should().Be("password");
        tokenField.Value.Should().BeEmpty("password fields must never expose their stored value");
    }

    [Fact]
    public async Task TestConnectionAsync_WithValidClientId_ReturnsHealthy()
    {
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var client = new HttpClient(handler);
        var provider = new TraktWatchStateProvider(
            client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        var health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Trakt__ClientId"] = _options.ClientId,
        });

        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnectionAsync_MissingClientId_ReturnsUnhealthy()
    {
        var opts = new TraktOptions { ClientId = "", Username = _options.Username, AccessToken = _options.AccessToken, CacheTtlSeconds = _options.CacheTtlSeconds };
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        var health = await provider.TestConnectionAsync(new Dictionary<string, string>());

        health.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ExistingPlaceholder_ResolvesToStoredAccessToken()
    {
        var requestsSeen = new List<HttpRequestMessage>();
        var handler = new MockHttpMessageHandler(req =>
        {
            requestsSeen.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        var client = new HttpClient(handler);
        var provider = new TraktWatchStateProvider(
            client, new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        var health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Trakt__ClientId"] = _options.ClientId,
            ["Trakt__AccessToken"] = "__EXISTING__",
        });

        health.IsHealthy.Should().BeTrue();
        requestsSeen.Should().ContainSingle();
        // ClientId should be sent as trakt-api-key header
        requestsSeen[0].Headers.GetValues("trakt-api-key").First()
            .Should().Be(_options.ClientId);
    }

    [Fact]
    public void RevealSecret_AccessToken_ReturnsStoredValue()
    {
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        provider.RevealSecret("Trakt__AccessToken").Should().Be(_options.AccessToken);
    }

    [Fact]
    public void RevealSecret_OtherKey_ReturnsNull()
    {
        var provider = new TraktWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache, NullLogger<TraktWatchStateProvider>.Instance);

        provider.RevealSecret("Trakt__ClientId").Should().BeNull();
        provider.RevealSecret("Trakt__Username").Should().BeNull();
    }
}
