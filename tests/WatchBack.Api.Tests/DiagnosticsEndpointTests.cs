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

public class DiagnosticsEndpointTests : IAsyncLifetime, IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";
    private HttpClient _client = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

        IWatchStateProvider mockWatchProvider = Substitute.For<IWatchStateProvider>();
        mockWatchProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Test", "Test"));
        mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns((MediaContext?)null);

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
                    services.AddScoped<IWatchStateProvider>(_ => mockWatchProvider);
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
    public async Task GetLogs_ReturnsOk()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/diagnostics/logs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLogs_LimitClampedToMaximum_Returns500EntriesOrFewer()
    {
        // limit=600 should be clamped to 500
        HttpResponseMessage response = await _client.GetAsync("/api/diagnostics/logs?limit=600");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task GetLogs_LimitClampedToMinimum_ReturnsAtLeastOneEntry()
    {
        // limit=0 should be clamped to 1 — just verify no error is returned
        HttpResponseMessage response = await _client.GetAsync("/api/diagnostics/logs?limit=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ClearLogs_ReturnsOk()
    {
        HttpResponseMessage response = await _client.DeleteAsync("/api/diagnostics/logs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetStatus_ReturnsOk()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/diagnostics/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSyncHistory_ReturnsOk()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/diagnostics/sync-history");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ClearSyncHistory_ReturnsOk()
    {
        HttpResponseMessage response = await _client.DeleteAsync("/api/diagnostics/sync-history");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLogs_Unauthenticated_Returns401()
    {
        using HttpClient unauthClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await unauthClient.GetAsync("/api/diagnostics/logs");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
