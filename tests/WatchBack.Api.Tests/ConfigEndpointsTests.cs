using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using WatchBack.Api;
using WatchBack.Api.Endpoints;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Api.Tests;

public class ConfigEndpointsTests : IAsyncLifetime, IDisposable
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _tempConfigPath = null!;

    public async Task InitializeAsync()
    {
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"watchback-test-{Guid.NewGuid()}.json");

        var mockWatchProvider = Substitute.For<IWatchStateProvider>();
        mockWatchProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));

        var mockThoughtProvider = Substitute.For<IThoughtProvider>();
        mockThoughtProvider.Metadata.Returns(new ThoughtProviderMetadata("Reddit", "Test", new BrandData("#FF4500", "")));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWatchStateProvider>();
                    services.RemoveAll<IThoughtProvider>();
                    services.AddScoped(_ => mockWatchProvider);
                    services.AddScoped(_ => mockThoughtProvider);

                    // Use temp config file
                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(_tempConfigPath));
                });
            });

        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        if (File.Exists(_tempConfigPath))
            File.Delete(_tempConfigPath);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetConfig_ReturnsOkWithIntegrations()
    {
        var response = await _client.GetAsync("/api/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("integrations");
        json.Should().Contain("preferences");
        json.Should().Contain("jellyfin");
        json.Should().Contain("trakt");
        json.Should().Contain("bluesky");
        json.Should().Contain("reddit");
    }

    [Fact]
    public async Task GetStatus_ReturnsWatchProvider()
    {
        var response = await _client.GetAsync("/api/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("watchProvider");
        json.Should().Contain("jellyfin");
    }

    [Fact]
    public async Task SaveConfig_WithValidKeys_PersistsConfig()
    {
        var payload = new Dictionary<string, string>
        {
            ["Jellyfin__BaseUrl"] = "http://test:8096",
            ["Trakt__ClientId"] = "test-client-id",
        };

        var response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(_tempConfigPath).Should().BeTrue();

        var saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().Contain("http://test:8096");
        saved.Should().Contain("test-client-id");
    }

    [Fact]
    public async Task SaveConfig_RejectsUnknownSections()
    {
        var payload = new Dictionary<string, string>
        {
            ["ConnectionStrings__Default"] = "Server=evil",
            ["Jellyfin__BaseUrl"] = "http://legit:8096",
        };

        var response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().NotContain("ConnectionStrings");
        saved.Should().Contain("http://legit:8096");
    }

    [Fact]
    public async Task SaveConfig_SkipsEmptyValues()
    {
        var payload = new Dictionary<string, string>
        {
            ["Jellyfin__BaseUrl"] = "http://test:8096",
            ["Jellyfin__ApiKey"] = "",
        };

        var response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().Contain("BaseUrl");
        saved.Should().NotContain("ApiKey");
    }

    [Fact]
    public async Task TestService_Reddit_ReturnsConnected()
    {
        var payload = new Dictionary<string, string>();
        var response = await _client.PostAsJsonAsync("/api/test/reddit", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Connected");
    }

    [Fact]
    public async Task TestService_Unknown_ReturnsError()
    {
        var payload = new Dictionary<string, string>();
        var response = await _client.PostAsJsonAsync("/api/test/unknown", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Unknown service");
    }
}