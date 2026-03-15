using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using WatchBack.Api;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Api.Tests;

public class SyncEndpointsTests : IAsyncLifetime, IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var hasher = new PasswordHasher<string>();
        var hash = hasher.HashPassword("", TestPassword);

        var mockWatchProvider = Substitute.For<IWatchStateProvider>();
        mockWatchProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(new EpisodeContext("Test Show", null, "Pilot", 1, 1));

        var mockThoughtProvider = Substitute.For<IThoughtProvider>();
        mockThoughtProvider.Metadata.Returns(new ThoughtProviderMetadata("Test", "Test", new BrandData("", "")));
        mockThoughtProvider.ExpectedWeight.Returns(1);
        mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<IProgress<SyncProgressTick>?>()?.Report(new SyncProgressTick(1, "Test"));
                return new ThoughtResult("Test", null, null, null, [], null);
            });

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Auth:Username"] = TestUsername,
                        ["Auth:PasswordHash"] = hash,
                        ["Auth:OnboardingComplete"] = "true",
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWatchStateProvider>();
                    services.RemoveAll<IThoughtProvider>();
                    services.AddScoped(_ => mockWatchProvider);
                    services.AddScoped(_ => mockThoughtProvider);
                });
            });
        _client = _factory.CreateClient();
        await LoginAsync();
    }

    private async Task LoginAsync()
    {
        var loginBody = new { username = TestUsername, password = TestPassword };
        var response = await _client.PostAsJsonAsync("/api/auth/login", loginBody);
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetSync_ReturnsOkStatus()
    {
        // Act
        var response = await _client.GetAsync("/api/sync");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task GetSync_ReturnsValidSyncResponse()
    {
        // Act
        var response = await _client.GetAsync("/api/sync");
        var jsonString = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonString.Should().NotBeNull();
        jsonString.Should().Contain("status");
    }

    [Fact]
    public async Task GetSync_ResponseHasRequiredFields()
    {
        // Act
        var response = await _client.GetAsync("/api/sync");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("status");
        content.Should().Contain("timeMachineDays");
    }

    [Fact]
    public async Task GetSyncStream_ReturnsEventStream()
    {
        // Act - Use HttpCompletionOption.ResponseHeadersRead to avoid waiting for full response
        var response = await _client.GetAsync("/api/sync/stream", HttpCompletionOption.ResponseHeadersRead);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
    }

    [Fact]
    public async Task GetSyncStream_EmitsProgressEventsBeforeSyncResult()
    {
        // Act — read first few lines of the SSE stream
        using var response = await _client.GetAsync("/api/sync/stream", HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Read until we see a sync result (line containing "status")
        while (!cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            if (line.StartsWith("data: ", StringComparison.Ordinal)) lines.Add(line);
            // Stop once we've received the sync payload
            if (line.Contains("\"status\"")) break;
        }

        // Must have at least one progress event before the final sync event
        lines.Should().HaveCountGreaterThan(1);
        lines.First().Should().Contain("\"completed\"");
        lines.Last().Should().Contain("\"status\"");
    }
}
