using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Api.Tests;

public class SyncIntegrationTests : IAsyncLifetime
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";
    private HttpClient _client = null!;

    private WebApplicationFactory<Program> _factory = null!;
    private IThoughtProvider _mockThoughtProvider = null!;
    private IWatchStateProvider _mockWatchProvider = null!;

    public async Task InitializeAsync()
    {
        _mockWatchProvider = Substitute.For<IWatchStateProvider>();
        _mockWatchProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        _mockThoughtProvider = Substitute.For<IThoughtProvider>();
        _mockThoughtProvider.Metadata.Returns(
            new DataProviderMetadata("Test", "Test", BrandData: new BrandData("", "")));

        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Auth:Username"] = TestUsername,
                        ["Auth:PasswordHash"] = hash,
                        ["Auth:OnboardingComplete"] = "true"
                    });
                });
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
        await LoginAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private async Task LoginAsync()
    {
        var loginBody = new { username = TestUsername, password = TestPassword };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", loginBody);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetSync_WithIdleState_ReturnsIdleStatus()
    {
        // Arrange
        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns((MediaContext?)null);

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/sync");
        string json = await response.Content.ReadAsStringAsync();

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
        EpisodeContext episode = new(
            "Breaking Bad",
            new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(episode);

        Thought thought = new(
            "1",
            null,
            null,
            "Amazing episode!",
            null,
            [],
            "TestUser",
            10,
            DateTimeOffset.UtcNow,
            "TestSource",
            []);

        _mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("TestSource", "Episode Discussion", "http://example.com", null, [thought],
                null));

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/sync");
        string json = await response.Content.ReadAsStringAsync();

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
        EpisodeContext episode = new(
            "Test Show",
            DateTimeOffset.UtcNow,
            "Pilot",
            1,
            1);

        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(episode);

        // First provider
        Thought thought1 = new("1", null, null, "Great!", null, [], "User1", 5, DateTimeOffset.UtcNow, "Reddit", []);
        _mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Reddit", "Discussion", "http://reddit.com", null, [thought1], null));

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/sync");
        string json = await response.Content.ReadAsStringAsync();

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
        HttpResponseMessage response = await _client.GetAsync("/api/sync");
        string json = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = JsonDocument.Parse(json);
        doc.Should().NotBeNull();

        JsonElement root = doc.RootElement;
        root.HaveProperty("status").Should().BeTrue();
        root.HaveProperty("timeMachineDays").Should().BeTrue();
        root.HaveProperty("allThoughts").Should().BeTrue();
        root.HaveProperty("sourceResults").Should().BeTrue();
    }

    [Fact]
    public async Task GetSync_IncludesMetadataWhenWatching()
    {
        // Arrange
        EpisodeContext episode = new(
            "Breaking Bad",
            new DateTimeOffset(2008, 1, 20, 12, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(episode);

        _mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Source", null, null, null, [], null));

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/sync");
        string json = await response.Content.ReadAsStringAsync();

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
        EpisodeContext episode = new(
            "Test",
            DateTimeOffset.UtcNow,
            "Pilot",
            1,
            1);

        _mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(episode);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Thought[] thoughts =
        [
            new("1", null, null, "Oldest", null, [], "Author", 0, now.AddHours(-2), "Source", []),
            new("2", null, null, "Newest", null, [], "Author", 0, now, "Source", []),
            new("3", null, null, "Middle", null, [], "Author", 0, now.AddHours(-1), "Source", [])
        ];

        _mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Source", null, null, null, thoughts, null));

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/sync");
        string json = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(json);

        // Assert
        JsonElement allThoughts = doc.RootElement.GetProperty("allThoughts");
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
