using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using WatchBack.Api;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

using Xunit;

namespace WatchBack.Api.Tests;

public class SyncIntegrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private IWatchStateProvider _mockWatchProvider = null!;
    private IThoughtProvider _mockThoughtProvider = null!;

    public async Task InitializeAsync()
    {
        _mockWatchProvider = Substitute.For<IWatchStateProvider>();
        _mockWatchProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        _mockThoughtProvider = Substitute.For<IThoughtProvider>();
        _mockThoughtProvider.Metadata.Returns(new ThoughtProviderMetadata("Test", "Test", new BrandData("", "")));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Remove the real provider registrations
                    services.RemoveAll<IWatchStateProvider>();
                    services.RemoveAll<IThoughtProvider>();

                    // Register mocks
                    services.AddScoped(_ => _mockWatchProvider);
                    services.AddScoped(_ => _mockThoughtProvider);
                });
            });

        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetSync_WithIdleState_ReturnsIdleStatus()
    {
        // Arrange
        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns((MediaContext?)null);

        // Act
        var response = await _client.GetAsync("/api/sync");
        var json = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.Should().Contain("\"status\":\"Idle\"");
        json.Should().Contain("\"allThoughts\":[]");
        json.Should().Contain("\"sourceResults\":[]");
    }

    [Fact]
    public async Task GetSync_WithActiveEpisode_ReturnsWatchingStatus()
    {
        // Arrange
        var episode = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(episode);

        var thought = new Thought(
            Id: "1",
            ParentId: null,
            Title: null,
            Content: "Amazing episode!",
            Url: null,
            Images: [],
            Author: "TestUser",
            Score: 10,
            CreatedAt: DateTimeOffset.UtcNow,
            Source: "TestSource",
            Replies: []);

        _mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("TestSource", "Episode Discussion", "http://example.com", null, [thought], null));

        // Act
        var response = await _client.GetAsync("/api/sync");
        var json = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.Should().Contain("\"status\":\"Watching\"");
        json.Should().Contain("\"title\":\"Breaking Bad\"");
        json.Should().Contain("Amazing episode!");
        json.Should().Contain("TestSource");
    }

    [Fact]
    public async Task GetSync_AggregatesMultipleThoughtProviders()
    {
        // Arrange
        var episode = new EpisodeContext(
            Title: "Test Show",
            ReleaseDate: DateTimeOffset.UtcNow,
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(episode);

        // First provider
        var thought1 = new Thought("1", null, null, "Great!", null, [], "User1", 5, DateTimeOffset.UtcNow, "Reddit", []);
        _mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Reddit", "Discussion", "http://reddit.com", null, [thought1], null));

        // Act
        var response = await _client.GetAsync("/api/sync");
        var json = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.Should().Contain("\"source\":\"Reddit\"");
        json.Should().Contain("Great!");
    }

    [Fact]
    public async Task GetSync_ResponseIsValidJSON()
    {
        // Arrange
        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns((MediaContext?)null);

        // Act
        var response = await _client.GetAsync("/api/sync");
        var json = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(json);
        doc.Should().NotBeNull();

        var root = doc.RootElement;
        root.HaveProperty("status").Should().BeTrue();
        root.HaveProperty("timeMachineDays").Should().BeTrue();
        root.HaveProperty("allThoughts").Should().BeTrue();
        root.HaveProperty("sourceResults").Should().BeTrue();
    }

    [Fact]
    public async Task GetSync_IncludesMetadataWhenWatching()
    {
        // Arrange
        var episode = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2008, 1, 20, 12, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(episode);

        _mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Source", null, null, null, [], null));

        // Act
        var response = await _client.GetAsync("/api/sync");
        var json = await response.Content.ReadAsStringAsync();

        // Assert
        json.Should().Contain("\"title\":\"Breaking Bad\"");
        json.Should().Contain("\"episodeTitle\":\"Pilot\"");
        json.Should().Contain("\"seasonNumber\":1");
        json.Should().Contain("\"episodeNumber\":1");
    }

    [Fact]
    public async Task GetSync_SortsThoughtsByCreatedAtDescending()
    {
        // Arrange
        var episode = new EpisodeContext(
            Title: "Test",
            ReleaseDate: DateTimeOffset.UtcNow,
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(episode);

        var now = DateTimeOffset.UtcNow;
        var thoughts = new[]
        {
            new Thought("1", null, null, "Oldest", null, [], "Author", 0, now.AddHours(-2), "Source", []),
            new Thought("2", null, null, "Newest", null, [], "Author", 0, now, "Source", []),
            new Thought("3", null, null, "Middle", null, [], "Author", 0, now.AddHours(-1), "Source", []),
        };

        _mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Source", null, null, null, thoughts, null));

        // Act
        var response = await _client.GetAsync("/api/sync");
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        // Assert
        var allThoughts = doc.RootElement.GetProperty("allThoughts");
        allThoughts.GetArrayLength().Should().Be(3);
        allThoughts[0].GetProperty("content").GetString().Should().Be("Newest");
        allThoughts[1].GetProperty("content").GetString().Should().Be("Middle");
        allThoughts[2].GetProperty("content").GetString().Should().Be("Oldest");
    }
}

internal static class JsonElementExtensions
{
    public static bool HaveProperty(this JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out _);
    }
}