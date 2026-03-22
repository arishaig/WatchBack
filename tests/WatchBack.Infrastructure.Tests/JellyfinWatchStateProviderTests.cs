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


public class JellyfinWatchStateProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly JellyfinOptions _options;

    public JellyfinWatchStateProviderTests()
    {
        _options = new JellyfinOptions
        {
            BaseUrl = "http://jellyfin:8096",
            ApiKey = "test-api-key",
            CacheTtlSeconds = 10
        };
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithNoActiveSessions_ReturnsNull()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithActiveSessions_ReturnsEpisodeContext()
    {
        // Arrange
        var sessionJson = """
            [
                {
                    "Id": "session1",
                    "NowPlayingItem": {
                        "Name": "Breaking Bad",
                        "SeriesName": "Breaking Bad",
                        "ParentIndexNumber": 3,
                        "IndexNumber": 7,
                        "PremiereDate": "2010-03-28T00:00:00Z"
                    }
                }
            ]
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sessionJson)
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<EpisodeContext>();
        var episode = (EpisodeContext)result!;
        episode.Title.Should().Be("Breaking Bad");
        episode.EpisodeTitle.Should().Be("Breaking Bad");
        episode.SeasonNumber.Should().Be(3);
        episode.EpisodeNumber.Should().Be(7);
        episode.ReleaseDate.Should().HaveValue();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithoutPremiereDate_ReturnsNullReleaseDate()
    {
        // Arrange
        var sessionJson = """
            [
                {
                    "Id": "session1",
                    "NowPlayingItem": {
                        "Name": "Breaking Bad",
                        "SeriesName": "Breaking Bad",
                        "Season": 1,
                        "IndexNumber": 1
                    }
                }
            ]
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sessionJson)
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().NotBeNull();
        result!.ReleaseDate.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithUnauthorized_ReturnsNull()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_SecondCall_UsesCacheWithinTtl()
    {
        // Arrange
        var sessionJson = """
            [
                {
                    "Id": "session1",
                    "NowPlayingItem": {
                        "Name": "Breaking Bad",
                        "SeriesName": "Breaking Bad",
                        "Season": 1,
                        "IndexNumber": 1
                    }
                }
            ]
            """;

        var callCount = 0;
        var handler = new MockHttpMessageHandler(
            () =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sessionJson)
                };
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        var result1 = await provider.GetCurrentMediaContextAsync();
        var result2 = await provider.GetCurrentMediaContextAsync();

        // Assert
        callCount.Should().Be(1); // Second call uses cache
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
    }

    [Fact]
    public void Metadata_SupportedExternalIds_ContainsImdbTmdbTvdb()
    {
        // Arrange
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Assert
        provider.Metadata.Should().BeOfType<WatchStateDataProviderMetadata>();
        var meta = (WatchStateDataProviderMetadata)provider.Metadata;
        meta.SupportedExternalIds.Should().BeEquivalentTo([ExternalIdType.Imdb, ExternalIdType.Tmdb, ExternalIdType.Tvdb]);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithAllProviderIds_PopulatesExternalIds()
    {
        // Arrange
        var sessionJson = """
            [
                {
                    "Id": "session1",
                    "NowPlayingItem": {
                        "Name": "Ozymandias",
                        "SeriesName": "Breaking Bad",
                        "ParentIndexNumber": 5,
                        "IndexNumber": 14,
                        "ProviderIds": {
                            "Imdb": "tt0903747",
                            "Tmdb": "1396",
                            "Tvdb": "81189"
                        }
                    }
                }
            ]
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

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
    public async Task GetCurrentMediaContextAsync_WithPartialProviderIds_PopulatesOnlyAvailableIds()
    {
        // Arrange
        var sessionJson = """
            [
                {
                    "Id": "session1",
                    "NowPlayingItem": {
                        "Name": "Ozymandias",
                        "SeriesName": "Breaking Bad",
                        "ParentIndexNumber": 5,
                        "IndexNumber": 14,
                        "ProviderIds": {
                            "Tvdb": "81189"
                        }
                    }
                }
            ]
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        var result = await provider.GetCurrentMediaContextAsync();

        // Assert
        var episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().NotBeNull();
        episode.ExternalIds![ExternalIdType.Tvdb].Should().Be("81189");
        episode.ExternalIds.Should().NotContainKey(ExternalIdType.Imdb);
        episode.ExternalIds.Should().NotContainKey(ExternalIdType.Tmdb);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithNoProviderIds_ReturnsNullExternalIds()
    {
        // Arrange
        var sessionJson = """
            [
                {
                    "Id": "session1",
                    "NowPlayingItem": {
                        "Name": "Breaking Bad",
                        "SeriesName": "Breaking Bad",
                        "ParentIndexNumber": 1,
                        "IndexNumber": 1
                    }
                }
            ]
            """;

        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

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
                Content = new StringContent("[]")
            });
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

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
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeFalse();
    }

    // ---- Provider self-description ----

    [Fact]
    public void ConfigSection_IsJellyfin()
    {
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.ConfigSection.Should().Be("Jellyfin");
    }

    [Fact]
    public void IsConfigured_WhenApiKeySet_IsTrue()
    {
        var opts = new JellyfinOptions { BaseUrl = _options.BaseUrl, ApiKey = "my-key", CacheTtlSeconds = _options.CacheTtlSeconds };
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(opts), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenApiKeyEmpty_IsFalse()
    {
        var opts = new JellyfinOptions { BaseUrl = _options.BaseUrl, ApiKey = "", CacheTtlSeconds = _options.CacheTtlSeconds };
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(opts), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void GetConfigSchema_ReturnsBothFields()
    {
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        var fields = provider.GetConfigSchema(_ => "", (_, _) => false);

        fields.Should().HaveCount(2);
        fields.Select(f => f.Key).Should().BeEquivalentTo(["Jellyfin__BaseUrl", "Jellyfin__ApiKey"]);
    }

    [Fact]
    public void GetConfigSchema_ApiKeyField_IsPasswordType()
    {
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        var apiKeyField = provider.GetConfigSchema(_ => "", (_, _) => false)
            .First(f => f.Key == "Jellyfin__ApiKey");

        apiKeyField.Type.Should().Be("password");
        apiKeyField.Value.Should().BeEmpty("password fields must never expose their stored value");
    }

    [Fact]
    public async Task TestConnectionAsync_WithValidServer_ReturnsHealthy()
    {
        var infoJson = """{"Version":"10.9.11","ServerName":"My Jellyfin"}""";
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(infoJson) });
        var client = new HttpClient(handler);
        var provider = new JellyfinWatchStateProvider(
            client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        var health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Jellyfin__BaseUrl"] = _options.BaseUrl,
            ["Jellyfin__ApiKey"] = _options.ApiKey,
        });

        health.IsHealthy.Should().BeTrue();
        health.Message.Should().Contain("10.9.11");
    }

    [Fact]
    public async Task TestConnectionAsync_WithMissingCredentials_ReturnsUnhealthy()
    {
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        var health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Jellyfin__BaseUrl"] = "",
            ["Jellyfin__ApiKey"] = "",
        });

        health.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ExistingPlaceholder_ResolvesToStoredApiKey()
    {
        // Arrange: server responds with 200 OK when the stored key is used
        var requestsSeen = new List<HttpRequestMessage>();
        var handler = new MockHttpMessageHandler(req =>
        {
            requestsSeen.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Version":"10.9.0"}"""),
            };
        });
        var client = new HttpClient(handler);
        var provider = new JellyfinWatchStateProvider(
            client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act: form sends "__EXISTING__" for the password field
        var health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Jellyfin__BaseUrl"] = _options.BaseUrl,
            ["Jellyfin__ApiKey"] = "__EXISTING__",
        });

        // Assert: the stored key was used (request was made, server returned OK)
        health.IsHealthy.Should().BeTrue();
        requestsSeen.Should().ContainSingle();
        requestsSeen[0].Headers.GetValues("X-Emby-Authorization").First()
            .Should().Contain(_options.ApiKey);
    }

    [Fact]
    public void RevealSecret_ApiKey_ReturnsStoredValue()
    {
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.RevealSecret("Jellyfin__ApiKey").Should().Be(_options.ApiKey);
    }

    [Fact]
    public void RevealSecret_UnknownKey_ReturnsNull()
    {
        var provider = new JellyfinWatchStateProvider(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache, NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.RevealSecret("Jellyfin__BaseUrl").Should().BeNull();
        provider.RevealSecret("SomeOther__Key").Should().BeNull();
    }
}

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage>? _responseFactory;
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _requestFactory;
    private readonly HttpResponseMessage? _fixedResponse;

    public MockHttpMessageHandler(HttpResponseMessage fixedResponse)
    {
        _fixedResponse = fixedResponse;
    }

    public MockHttpMessageHandler(Func<HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> requestFactory)
    {
        _requestFactory = requestFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = _fixedResponse ?? _requestFactory?.Invoke(request) ?? _responseFactory!();
        return Task.FromResult(response);
    }
}