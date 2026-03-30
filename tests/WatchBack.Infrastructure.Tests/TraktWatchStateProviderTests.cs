using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.WatchStateProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public class TraktWatchStateProviderTests : IDisposable
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
    public async Task GetCurrentMediaContextAsync_WithNoActivePlayback_ReturnsNull()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.NoContent));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithActiveEpisode_ReturnsEpisodeContext()
    {
        // Arrange
        string responseJson = """
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

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<EpisodeContext>();
        EpisodeContext episode = (EpisodeContext)result;
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
        string responseJson = """
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

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().NotBeNull();
        result.ReleaseDate.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_SecondCall_UsesCacheWithinTtl()
    {
        // Arrange
        string responseJson = """
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

        int callCount = 0;
        MockHttpMessageHandler handler = new(() =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        MediaContext? result1 = await provider.GetCurrentMediaContextAsync();
        MediaContext? result2 = await provider.GetCurrentMediaContextAsync();

        // Assert
        callCount.Should().Be(1); // Cache hit on second call
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
    }

    [Fact]
    public void Metadata_SupportedExternalIds_ContainsImdbTmdbTvdb()
    {
        // Arrange
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Assert
        provider.Metadata.Should().BeOfType<WatchStateDataProviderMetadata>();
        WatchStateDataProviderMetadata meta = (WatchStateDataProviderMetadata)provider.Metadata;
        meta.SupportedExternalIds.Should()
            .BeEquivalentTo(ExternalIdType.Imdb, ExternalIdType.Tmdb, ExternalIdType.Tvdb);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithAllShowIds_PopulatesExternalIds()
    {
        // Arrange
        string responseJson = """
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

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        EpisodeContext? episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().NotBeNull();
        episode.ExternalIds![ExternalIdType.Imdb].Should().Be("tt0903747");
        episode.ExternalIds[ExternalIdType.Tmdb].Should().Be("1396");
        episode.ExternalIds[ExternalIdType.Tvdb].Should().Be("81189");
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithPartialShowIds_PopulatesOnlyAvailableIds()
    {
        // Arrange
        string responseJson = """
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

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        EpisodeContext? episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().NotBeNull();
        episode.ExternalIds![ExternalIdType.Imdb].Should().Be("tt0903747");
        episode.ExternalIds.Should().NotContainKey(ExternalIdType.Tmdb);
        episode.ExternalIds.Should().NotContainKey(ExternalIdType.Tvdb);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithNoShowIds_ReturnsNullExternalIds()
    {
        // Arrange
        string responseJson = """
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

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        EpisodeContext? episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().BeNull();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WithSuccess_ReturnsHealthy()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WithFailure_ReturnsUnhealthy()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.trakt.tv") };
        TraktWatchStateProvider provider = new(client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeFalse();
    }

    // ---- Provider self-description ----

    [Fact]
    public void ConfigSection_IsTrakt()
    {
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        provider.ConfigSection.Should().Be("Trakt");
    }

    [Fact]
    public void IsConfigured_WhenClientIdSet_IsTrue()
    {
        TraktOptions opts = new()
        {
            ClientId = "abc",
            Username = "",
            AccessToken = _options.AccessToken,
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenUsernameSet_IsTrue()
    {
        TraktOptions opts = new()
        {
            ClientId = "",
            Username = "myuser",
            AccessToken = _options.AccessToken,
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenBothEmpty_IsFalse()
    {
        TraktOptions opts = new()
        {
            ClientId = "",
            Username = "",
            AccessToken = _options.AccessToken,
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void GetConfigSchema_ReturnsThreeFields()
    {
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        IReadOnlyList<ProviderConfigField> fields = provider.GetConfigSchema(_ => "", (_, _) => false);

        fields.Should().HaveCount(3);
        fields.Select(f => f.Key).Should().BeEquivalentTo("Trakt__ClientId", "Trakt__AccessToken", "Trakt__Username");
    }

    [Fact]
    public void GetConfigSchema_AccessTokenField_IsPasswordType()
    {
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        ProviderConfigField tokenField = provider.GetConfigSchema(_ => "", (_, _) => false)
            .First(f => f.Key == "Trakt__AccessToken");

        tokenField.Type.Should().Be("password");
        tokenField.Value.Should().BeEmpty("password fields must never expose their stored value");
    }

    [Fact]
    public async Task TestConnectionAsync_WithValidClientId_ReturnsHealthy()
    {
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        HttpClient client = new(handler);
        TraktWatchStateProvider provider = new(
            client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        ServiceHealth health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Trakt__ClientId"] = _options.ClientId
        });

        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnectionAsync_MissingClientId_ReturnsUnhealthy()
    {
        TraktOptions opts = new()
        {
            ClientId = "",
            Username = _options.Username,
            AccessToken = _options.AccessToken,
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(opts), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        ServiceHealth health = await provider.TestConnectionAsync(new Dictionary<string, string>());

        health.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ExistingPlaceholder_ResolvesToStoredAccessToken()
    {
        List<HttpRequestMessage> requestsSeen = new();
        MockHttpMessageHandler handler = new(req =>
        {
            requestsSeen.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        HttpClient client = new(handler);
        TraktWatchStateProvider provider = new(
            client, new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        ServiceHealth health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Trakt__ClientId"] = _options.ClientId,
            ["Trakt__AccessToken"] = "__EXISTING__"
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
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        provider.RevealSecret("Trakt__AccessToken").Should().Be(_options.AccessToken);
    }

    [Fact]
    public void RevealSecret_OtherKey_ReturnsNull()
    {
        TraktWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<TraktOptions>(_options), _cache,
            NullLogger<TraktWatchStateProvider>.Instance);

        provider.RevealSecret("Trakt__ClientId").Should().BeNull();
        provider.RevealSecret("Trakt__Username").Should().BeNull();
    }
}
