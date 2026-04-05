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

public class SearchEndpointTests : IAsyncLifetime, IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";
    private HttpClient _client = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private IMediaSearchProvider _mockSearchProvider = null!;

    public async Task InitializeAsync()
    {
        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

        IWatchStateProvider mockWatchProvider = Substitute.For<IWatchStateProvider>();
        mockWatchProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Test", "Test"));
        mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns((MediaContext?)null);

        _mockSearchProvider = Substitute.For<IMediaSearchProvider>();
        _mockSearchProvider.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MediaSearchResult>>(
                [new MediaSearchResult("tt0903747", "Breaking Bad", "2008", "series", null)]));
        _mockSearchProvider.GetSeasonsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SeasonInfo>>([new SeasonInfo(1, 7)]));
        _mockSearchProvider.GetEpisodesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EpisodeInfo>>(
                [new EpisodeInfo("tt1232456", "Pilot", 1, 1, null)]));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Auth:Username"] = TestUsername,
                        ["Auth:PasswordHash"] = hash,
                        ["Auth:OnboardingComplete"] = "true"
                    }));

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWatchStateProvider>();
                    services.RemoveAll<IThoughtProvider>();
                    services.RemoveAll<IMediaSearchProvider>();
                    services.AddScoped<IWatchStateProvider>(_ => mockWatchProvider);
                    services.AddScoped<IMediaSearchProvider>(_ => _mockSearchProvider);
                });
            });

        _client = _factory.CreateClient();
        await LoginAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task LoginAsync()
    {
        var body = new { username = TestUsername, password = TestPassword };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", body);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Search_WithQuery_Returns200WithResults()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/search?q=Breaking+Bad");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Search_WithMissingQuery_Returns400()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/search");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_WithEmptyQuery_Returns400()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/search?q=");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSeasons_WithImdbId_Returns200()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/search/show/tt0903747/seasons");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetEpisodes_WithImdbIdAndSeason_Returns200()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/search/show/tt0903747/season/1/episodes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Search_Unauthenticated_Returns401()
    {
        using HttpClient unauthClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await unauthClient.GetAsync("/api/search?q=test");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
