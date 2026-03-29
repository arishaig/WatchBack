using System.Net;
using System.Net.Http.Json;

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

public class SyncEndpointsTests : IAsyncLifetime, IDisposable
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
        mockWatchProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>())
            .Returns(new EpisodeContext("Test Show", null, "Pilot", 1, 1));

        IThoughtProvider? mockThoughtProvider = Substitute.For<IThoughtProvider>();
        mockThoughtProvider.Metadata.Returns(new DataProviderMetadata("Test", "Test",
            BrandData: new BrandData("", "")));
        mockThoughtProvider.ExpectedWeight.Returns(1);
        mockThoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
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
                        ["Auth:OnboardingComplete"] = "true"
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

    private async Task LoginAsync()
    {
        var loginBody = new { username = TestUsername, password = TestPassword };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", loginBody);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetSync_ReturnsOkStatus()
    {
        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/sync");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task GetSync_ReturnsValidSyncResponse()
    {
        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/sync");
        string jsonString = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonString.Should().NotBeNull();
        jsonString.Should().Contain("status");
    }

    [Fact]
    public async Task GetSync_ResponseHasRequiredFields()
    {
        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/sync");
        string content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("status");
        content.Should().Contain("timeMachineDays");
    }

    [Fact]
    public async Task GetSyncStream_ReturnsEventStream()
    {
        // Act - Use HttpCompletionOption.ResponseHeadersRead to avoid waiting for full response
        HttpResponseMessage response =
            await _client.GetAsync("/api/sync/stream", HttpCompletionOption.ResponseHeadersRead);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
    }

    [Fact]
    public async Task GetSyncStream_EmitsProgressEventsBeforeSyncResult()
    {
        // Act — read first few lines of the SSE stream
        using HttpResponseMessage response =
            await _client.GetAsync("/api/sync/stream", HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using StreamReader reader = new(stream);

        List<string> lines = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        // Read until we see a sync result (line containing "status")
        while (!cts.Token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cts.Token);
            if (line == null)
            {
                break;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                lines.Add(line);
            }

            // Stop once we've received the sync payload
            if (line.Contains("\"status\""))
            {
                break;
            }
        }

        // Must have at least one progress event before the final sync event
        lines.Should().HaveCountGreaterThan(1);
        lines.First().Should().Contain("\"completed\"");
        lines.Last().Should().Contain("\"status\"");
    }
}
