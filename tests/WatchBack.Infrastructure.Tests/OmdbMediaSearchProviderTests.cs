using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Omdb;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class OmdbMediaSearchProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private readonly OmdbOptions _options = new()
    {
        ApiKey = "test-api-key"
    };

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private OmdbMediaSearchProvider CreateProvider(HttpClient client, OmdbOptions? options = null)
    {
        return new OmdbMediaSearchProvider(client, _cache,
            new OptionsSnapshotStub<OmdbOptions>(options ?? _options));
    }

    // ---- SearchAsync — plain title (movie) ----

    [Fact]
    public async Task SearchAsync_WithPlainTitle_ReturnsResults()
    {
        // Arrange
        string searchJson = """
            {
                "Search": [
                    {
                        "imdbID": "tt0111161",
                        "Title": "The Shawshank Redemption",
                        "Year": "1994",
                        "Type": "movie",
                        "Poster": "https://example.com/poster.jpg"
                    }
                ],
                "Response": "True"
            }
            """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://www.omdbapi.com") };

        OmdbMediaSearchProvider provider = CreateProvider(client);

        // Act
        IReadOnlyList<MediaSearchResult> results = await provider.SearchAsync("Shawshank");

        // Assert
        results.Should().ContainSingle();
        results[0].ImdbId.Should().Be("tt0111161");
        results[0].Title.Should().Be("The Shawshank Redemption");
        results[0].Type.Should().Be("movie");
    }

    // ---- SearchAsync — episode format query ----

    [Fact]
    public async Task SearchAsync_WithSxxExxQuery_FetchesEpisodeDetail()
    {
        // Arrange — first request: series search; second: episode detail
        string seriesJson = """
            {
                "Search": [{"imdbID": "tt0903747", "Title": "Breaking Bad", "Year": "2008", "Type": "series"}],
                "Response": "True"
            }
            """;

        string episodeJson = """
            {
                "imdbID": "tt1232456",
                "Title": "Pilot",
                "Poster": "N/A",
                "Released": "20 Jan 2008",
                "Response": "True"
            }
            """;

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(seriesJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(episodeJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://www.omdbapi.com") };

        OmdbMediaSearchProvider provider = CreateProvider(client);

        // Act
        IReadOnlyList<MediaSearchResult> results = await provider.SearchAsync("Breaking Bad S01E01");

        // Assert
        results.Should().ContainSingle();
        results[0].Type.Should().Be("episode");
        results[0].Title.Should().Contain("Breaking Bad");
        results[0].Title.Should().Contain("Pilot");
        results[0].Title.Should().Contain("S01E01");
    }

    [Fact]
    public async Task SearchAsync_WhenEpisodeDetailResponseIsFalse_FallsBackToShowResult()
    {
        // Arrange — episode detail returns Response: "False"
        string seriesJson = """
            {
                "Search": [{"imdbID": "tt0903747", "Title": "Breaking Bad", "Year": "2008", "Type": "series"}],
                "Response": "True"
            }
            """;

        string episodeJson = """{"Response": "False"}""";

        Queue<HttpResponseMessage> responses = new(
        [
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(seriesJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(episodeJson) }
        ]);

        MockHttpMessageHandler handler = new(() => responses.Dequeue());
        HttpClient client = new(handler) { BaseAddress = new Uri("https://www.omdbapi.com") };

        OmdbMediaSearchProvider provider = CreateProvider(client);

        // Act
        IReadOnlyList<MediaSearchResult> results = await provider.SearchAsync("Breaking Bad S01E01");

        // Assert — falls back to the series-level result
        results.Should().ContainSingle();
        results[0].ImdbId.Should().Be("tt0903747");
        results[0].Type.Should().Be("series");
    }

    // ---- GetRatingsAsync ----

    [Fact]
    public async Task GetRatingsAsync_WithValidImdbId_ReturnsRatings()
    {
        // Arrange
        string ratingsJson = """
            {
                "Ratings": [
                    {"Source": "Internet Movie Database", "Value": "9.3/10"},
                    {"Source": "Rotten Tomatoes", "Value": "91%"}
                ],
                "Response": "True"
            }
            """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ratingsJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://www.omdbapi.com") };

        OmdbMediaSearchProvider provider = CreateProvider(client);

        // Act
        IReadOnlyList<MediaRating> ratings = await provider.GetRatingsAsync("tt0903747");

        // Assert
        ratings.Should().HaveCount(2);
        ratings.Should().Contain(r => r.Source == "Internet Movie Database" && r.Value == "9.3/10");
        ratings.Should().Contain(r => r.Source == "Rotten Tomatoes" && r.Value == "91%");
    }

    [Fact]
    public async Task GetRatingsAsync_WhenResponseIsFalse_ReturnsEmpty()
    {
        // Arrange
        string json = """{"Response": "False", "Error": "Movie not found!"}""";

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://www.omdbapi.com") };

        OmdbMediaSearchProvider provider = CreateProvider(client);

        // Act
        IReadOnlyList<MediaRating> ratings = await provider.GetRatingsAsync("tt9999999");

        // Assert
        ratings.Should().BeEmpty();
    }

    // ---- TestConnectionAsync ----

    [Fact]
    public async Task TestConnectionAsync_WithValidApiKey_ReturnsHealthy()
    {
        // Arrange
        string json = """{"Title": "Test", "Response": "True"}""";

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://www.omdbapi.com") };

        OmdbMediaSearchProvider provider = CreateProvider(client);

        // Act
        ServiceHealth health = await ((IDataProvider)provider).TestConnectionAsync(
            new Dictionary<string, string> { ["Omdb__ApiKey"] = "valid-key" });

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnectionAsync_WhenResponseContainsKeyError_ReturnsUnhealthy()
    {
        // Arrange — OMDb returns 200 with an "Error" body for invalid keys
        string json = """{"Response": "False", "Error": "Invalid API key!"}""";

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://www.omdbapi.com") };

        OmdbMediaSearchProvider provider = CreateProvider(client);

        // Act
        ServiceHealth health = await ((IDataProvider)provider).TestConnectionAsync(
            new Dictionary<string, string> { ["Omdb__ApiKey"] = "bad-key" });

        // Assert
        health.IsHealthy.Should().BeFalse();
    }

    // ---- No API key configured ----

    [Fact]
    public async Task SearchAsync_WithNoApiKey_ReturnsEmpty()
    {
        // Arrange
        OmdbOptions noKeyOptions = new() { ApiKey = "" };
        OmdbMediaSearchProvider provider = CreateProvider(new HttpClient(), noKeyOptions);

        // Act
        IReadOnlyList<MediaSearchResult> results = await provider.SearchAsync("Breaking Bad");

        // Assert — returns empty without making any HTTP calls
        results.Should().BeEmpty();
    }
}
