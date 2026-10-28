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

public class ManualWatchStateEndpointTests : IAsyncLifetime, IDisposable
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
    public async Task SetManualWatchState_WithValidTitle_Returns204()
    {
        var body = new
        {
            title = "Inception",
            releaseDate = (DateTimeOffset?)null,
            episodeTitle = (string?)null,
            seasonNumber = (short?)null,
            episodeNumber = (short?)null,
            externalIds = (Dictionary<string, string>?)null
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/watchstate/manual", body);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SetManualWatchState_WithEmptyTitle_Returns400()
    {
        var body = new
        {
            title = "",
            releaseDate = (DateTimeOffset?)null,
            episodeTitle = (string?)null,
            seasonNumber = (short?)null,
            episodeNumber = (short?)null,
            externalIds = (Dictionary<string, string>?)null
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/watchstate/manual", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetManualWatchState_WithWhitespaceTitle_Returns400()
    {
        var body = new
        {
            title = "   ",
            releaseDate = (DateTimeOffset?)null,
            episodeTitle = (string?)null,
            seasonNumber = (short?)null,
            episodeNumber = (short?)null,
            externalIds = (Dictionary<string, string>?)null
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/watchstate/manual", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ClearManualWatchState_Returns204()
    {
        HttpResponseMessage response = await _client.DeleteAsync("/api/watchstate/manual");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ManualWatchState_IsPerClient_AndSyncFollowsTheCallersClientId()
    {
        static object Body(string title) => new
        {
            title,
            releaseDate = (DateTimeOffset?)null,
            episodeTitle = (string?)null,
            seasonNumber = (short?)null,
            episodeNumber = (short?)null,
            externalIds = (Dictionary<string, string>?)null
        };

        (await _client.PostAsJsonAsync("/api/watchstate/manual?clientId=client-a", Body("Alien")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.PostAsJsonAsync("/api/watchstate/manual?clientId=client-b", Body("Heat")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.GetStringAsync("/api/sync?clientId=client-a")).Should().Contain("Alien");
        (await _client.GetStringAsync("/api/sync?clientId=client-b")).Should().Contain("Heat");

        (await _client.DeleteAsync("/api/watchstate/manual?clientId=client-a"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.GetStringAsync("/api/sync?clientId=client-a")).Should().NotContain("Alien");
        (await _client.GetStringAsync("/api/sync?clientId=client-b")).Should().Contain("Heat");
    }

    [Fact]
    public async Task ManualWatchState_SameJellyfinUserOnTwoBrowsers_IsShared_DifferentUsersAreNot()
    {
        static object Body(string title) => new
        {
            title,
            releaseDate = (DateTimeOffset?)null,
            episodeTitle = (string?)null,
            seasonNumber = (short?)null,
            episodeNumber = (short?)null,
            externalIds = (Dictionary<string, string>?)null
        };

        await _client.PostAsJsonAsync("/api/watchstate/manual?userId=aaaa&clientId=laptop", Body("Alien"));

        // Another browser, same user: sees it. Dashed/upper-case ids are the same user.
        (await _client.GetStringAsync("/api/sync?userId=AAAA&clientId=phone")).Should().Contain("Alien");
        // A different user on the same browser: does not.
        (await _client.GetStringAsync("/api/sync?userId=bbbb&clientId=laptop")).Should().NotContain("Alien");
        // The same browser with no user chosen is a different viewer too.
        (await _client.GetStringAsync("/api/sync?clientId=laptop")).Should().NotContain("Alien");

        // Clearing from the second browser clears it for the user everywhere.
        await _client.DeleteAsync("/api/watchstate/manual?userId=aaaa&clientId=phone");
        (await _client.GetStringAsync("/api/sync?userId=aaaa&clientId=laptop")).Should().NotContain("Alien");
    }

    [Fact]
    public async Task SetManualWatchState_Unauthenticated_Returns401()
    {
        using HttpClient unauthClient = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var body = new
        {
            title = "Inception",
            releaseDate = (DateTimeOffset?)null,
            episodeTitle = (string?)null,
            seasonNumber = (short?)null,
            episodeNumber = (short?)null,
            externalIds = (Dictionary<string, string>?)null
        };

        HttpResponseMessage response = await unauthClient.PostAsJsonAsync("/api/watchstate/manual", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
