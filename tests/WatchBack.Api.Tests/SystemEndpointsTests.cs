using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using WatchBack.Core.Interfaces;

using Xunit;

namespace WatchBack.Api.Tests;

public class SystemEndpointsTests : IAsyncLifetime, IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";
    private HttpClient _client = null!;

    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

        IWatchStateProvider? mockWatchProvider = Substitute.For<IWatchStateProvider>();
        mockWatchProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));

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
                    services.RemoveAll<IWatchStateProvider>();
                    services.AddScoped(_ => mockWatchProvider);
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

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Auth guard ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearCache_WithoutAuth_Returns401()
    {
        using HttpClient anonClient = _factory.CreateClient();
        HttpResponseMessage response = await anonClient.PostAsync("/api/system/clear-cache", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Restart_WithoutAuth_Returns401()
    {
        using HttpClient anonClient = _factory.CreateClient();
        HttpResponseMessage response = await anonClient.PostAsync("/api/system/restart", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Authenticated ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearCache_WhenAuthenticated_ReturnsOkTrue()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/system/clear-cache", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        OkResponse? body = await response.Content.ReadFromJsonAsync<OkResponse>();
        body!.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task ClearCache_EvictsCachedEntries()
    {
        // Seed a cache entry in a test scope
        using IServiceScope scope = _factory.Services.CreateScope();
        IMemoryCache cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        cache.Set("test-key", "test-value");
        cache.TryGetValue("test-key", out string? _).Should().BeTrue();

        await _client.PostAsync("/api/system/clear-cache", null);

        cache.TryGetValue("test-key", out string? _).Should().BeFalse();
    }

    [Fact]
    public async Task Restart_WhenAuthenticated_ReturnsOkTrue()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/system/restart", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        OkResponse? body = await response.Content.ReadFromJsonAsync<OkResponse>();
        body!.Ok.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task LoginAsync()
    {
        var loginBody = new { username = TestUsername, password = TestPassword };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", loginBody);
        response.EnsureSuccessStatusCode();
    }

    private sealed record OkResponse(bool Ok);
}
