using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Net;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.WatchState;

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

    public void Dispose() => _cache.Dispose();

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
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache);

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
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache);

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
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache);

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
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache);

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
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache);

        // Act
        var result1 = await provider.GetCurrentMediaContextAsync();
        var result2 = await provider.GetCurrentMediaContextAsync();

        // Assert
        callCount.Should().Be(1); // Second call uses cache
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
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
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache);

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
        var provider = new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache);

        // Act
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeFalse();
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
