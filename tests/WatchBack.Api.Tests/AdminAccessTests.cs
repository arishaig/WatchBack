using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using WatchBack.Api.Endpoints;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

// Request bodies read best inline; these arrays are built once per test, not in a loop.
#pragma warning disable CA1861

namespace WatchBack.Api.Tests;

// The first watch user picked becomes the admin; only admins change settings. Behind Authelia, its groups decide.
public sealed class AdminAccessTests : IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestAdmin1!@#456";

    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"wb-access-{Guid.NewGuid():N}.json");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HttpClient> _clients = [];

    public AdminAccessTests()
    {
        IWatchStateProvider provider = Substitute.For<IWatchStateProvider, IWatchUserDirectory>();
        provider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        provider.IsConfigured.Returns(true);
        ((IWatchUserDirectory)provider).GetUsersAsync(Arg.Any<CancellationToken>())
            .Returns([new WatchUser("alice", "Alice"), new WatchUser("bob", "Bob"), new WatchUser("cy", "Cy")]);

        string hash = new PasswordHasher<string>().HashPassword("", TestPassword);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Username"] = TestUsername,
                    ["Auth:PasswordHash"] = hash,
                    ["Auth:OnboardingComplete"] = "true",
                    // Behind Authelia: it sends Remote-User and Remote-Groups.
                    ["Auth:ForwardAuthHeader"] = "Remote-User",
                    ["Auth:ForwardAuthTrustedHost"] = "*"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UserConfigFile>();
                services.AddSingleton(new UserConfigFile(_configPath));
                services.RemoveAll<IWatchStateProvider>();
                services.AddScoped(_ => provider);
            });
        });
    }

    public void Dispose()
    {
        foreach (HttpClient c in _clients)
        {
            c.Dispose();
        }

        _factory.Dispose();
        File.Delete(_configPath);
    }

    // A signed-in browser that picked watchUser (or none). Cookies are set by hand so the pick can vary.
    private async Task<HttpClient> BrowserAsync(string? watchUser)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        _clients.Add(client);
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = TestUsername, password = TestPassword });
        login.EnsureSuccessStatusCode();
        string session = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie",
            watchUser is null ? session : $"{session}; wb_watchUser={watchUser}");
        return client;
    }

    private static async Task<Access> AccessAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<Access>("/api/access", s_json))!;

    private string SavedAdmins() =>
        File.Exists(_configPath)
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_configPath))!
                .GetValueOrDefault("Access")?.GetValueOrDefault("Admins") ?? ""
            : "";

    private static Task<HttpResponseMessage> SaveConfig(HttpClient client) =>
        client.PostAsJsonAsync("/api/config", new Dictionary<string, string> { ["WatchBack__TimeMachineDays"] = "7" });

    private static async Task<HttpResponseMessage> McpListTools(HttpClient client)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task WithNoAdmins_AnyoneCanChangeSettings()
    {
        HttpClient noPick = await BrowserAsync(null);

        (await AccessAsync(noPick)).Should().BeEquivalentTo(new Access(false, true, []));
        (await SaveConfig(noPick)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await noPick.GetAsync("/api/keys")).StatusCode.Should().Be(HttpStatusCode.OK);
        SavedAdmins().Should().BeEmpty("a browser that never picked a user claims nothing");
    }

    [Fact]
    public async Task TheFirstPickedUser_BecomesTheAdmin_AndTheNextDoesNot()
    {
        HttpClient alice = await BrowserAsync("alice");
        HttpClient bob = await BrowserAsync("bob");

        (await AccessAsync(alice)).Should().BeEquivalentTo(new Access(true, true, ["alice"]));
        (await AccessAsync(bob)).Should().BeEquivalentTo(new Access(true, false, ["alice"]));
        SavedAdmins().Should().Be("alice");
    }

    [Fact]
    public async Task AnIdTheProviderDoesNotList_CannotClaim()
    {
        HttpClient stranger = await BrowserAsync("mallory");

        (await AccessAsync(stranger)).Should().BeEquivalentTo(new Access(false, true, []));
        SavedAdmins().Should().BeEmpty();
    }

    [Fact]
    public async Task ANonAdmin_IsRefusedEverythingThatChangesSettings_ButCanStillWatch()
    {
        await AccessAsync(await BrowserAsync("alice"));
        HttpClient bob = await BrowserAsync("bob");

        HttpRequestMessage[] refused =
        [
            new(HttpMethod.Post, "/api/config") { Content = JsonContent.Create(new Dictionary<string, string>()) },
            new(HttpMethod.Delete, "/api/config") { Content = JsonContent.Create(new[] { "WatchBack__TimeMachineDays" }) },
            new(HttpMethod.Post, "/api/config/reveal/Jellyfin__ApiKey"),
            new(HttpMethod.Post, "/api/test/jellyfin") { Content = JsonContent.Create(new Dictionary<string, string>()) },
            new(HttpMethod.Get, "/api/keys"),
            new(HttpMethod.Post, "/api/keys") { Content = JsonContent.Create(new { name = "mine" }) },
            new(HttpMethod.Post, "/api/system/clear-cache"),
            new(HttpMethod.Post, "/api/system/restart"),
            new(HttpMethod.Post, "/api/auth/change-password") { Content = JsonContent.Create(new { currentPassword = "x", newPassword = "y" }) },
            new(HttpMethod.Post, "/api/auth/reset-password"),
            new(HttpMethod.Post, "/api/auth/setup") { Content = JsonContent.Create(new { newUsername = "me", newPassword = "Mine1!@#4567" }) },
            new(HttpMethod.Post, "/api/auth/forward-auth") { Content = JsonContent.Create(new { header = "", trustedHost = "" }) },
            new(HttpMethod.Delete, "/api/diagnostics/logs"),
            new(HttpMethod.Delete, "/api/diagnostics/sync-history"),
            new(HttpMethod.Post, "/api/subreddit-mappings/local") { Content = JsonContent.Create(new { title = "x", subreddits = new[] { "y" } }) },
            new(HttpMethod.Put, "/api/access/admins") { Content = JsonContent.Create(new { ids = new[] { "bob" } }) }
        ];
        foreach (HttpRequestMessage request in refused)
        {
            string what = $"{request.Method} {request.RequestUri}";
            (await bob.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden, what);
        }

        (await McpListTools(bob)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a browser calling /mcp");
        (await bob.GetAsync("/api/config")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await bob.GetAsync("/api/watchstate/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        SavedAdmins().Should().Be("alice");
    }

    [Fact]
    public async Task AnAdmin_CanChangeSettings_AndAnotherBrowserWithNoPickCannot()
    {
        HttpClient alice = await BrowserAsync("alice");
        await AccessAsync(alice);

        (await SaveConfig(alice)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await alice.GetAsync("/api/keys")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SaveConfig(await BrowserAsync(null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnAdmin_CanMakeOthersAdmins_ButNotDropThemselvesOrEveryone()
    {
        HttpClient alice = await BrowserAsync("alice");
        await AccessAsync(alice);

        (await alice.PutAsJsonAsync("/api/access/admins", new { ids = new[] { "alice", "bob" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await AccessAsync(await BrowserAsync("bob"))).IsAdmin.Should().BeTrue();

        (await alice.PutAsJsonAsync("/api/access/admins", new { ids = new[] { "bob" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "removing yourself");
        (await alice.PutAsJsonAsync("/api/access/admins", new { ids = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "leaving no admin");
        (await alice.PutAsJsonAsync("/api/access/admins", new { ids = new[] { "alice", "mallory" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a user the provider doesn't list");
        SavedAdmins().Should().Be("alice,bob");

        // Another admin can remove you.
        (await (await BrowserAsync("bob")).PutAsJsonAsync("/api/access/admins", new { ids = new[] { "bob" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await AccessAsync(alice)).IsAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task TheAdminList_CannotBeWrittenOrClearedThroughTheConfigApi()
    {
        HttpClient alice = await BrowserAsync("alice");
        await AccessAsync(alice);

        await alice.PostAsJsonAsync("/api/config", new Dictionary<string, string> { ["Access__Admins"] = "bob" });
        HttpRequestMessage reset = new(HttpMethod.Delete, "/api/config")
        {
            Content = JsonContent.Create(new[] { "Access__Admins" })
        };
        await alice.SendAsync(reset);

        SavedAdmins().Should().Be("alice");
    }

    [Fact]
    public async Task AnApiKey_StillReachesMcp_OnceAdminsExist()
    {
        HttpClient alice = await BrowserAsync("alice");
        await AccessAsync(alice);
        HttpResponseMessage created = await alice.PostAsJsonAsync("/api/keys", new { name = "assistant" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        string key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;

        HttpClient assistant = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        _clients.Add(assistant);
        assistant.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        (await McpListTools(assistant)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SaveConfig(assistant)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a key opens /mcp only, as before");
    }

    // Signed in through Authelia as user, with the groups it sent (null: no groups header at all).
    private HttpClient ThroughAuthelia(string user, string? groups)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        _clients.Add(client);
        client.DefaultRequestHeaders.Add("Remote-User", user);
        if (groups is not null)
        {
            client.DefaultRequestHeaders.Add("Remote-Groups", groups);
        }

        return client;
    }

    [Fact]
    public async Task BehindAuthelia_TheAdminsGroupDecides()
    {
        HttpClient admin = ThroughAuthelia("isaac", "dev, admins");
        HttpClient user = ThroughAuthelia("guest", "users");

        (await SaveConfig(admin)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync("/api/keys")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SaveConfig(user)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the groups decide even before anyone has claimed admin by picking a user");
        (await McpListTools(user)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        JsonElement access = await user.GetFromJsonAsync<JsonElement>("/api/access");
        access.GetProperty("restricted").GetBoolean().Should().BeTrue();
        access.GetProperty("isAdmin").GetBoolean().Should().BeFalse();
        access.GetProperty("decidedBy").GetString().Should().Be("group");
        access.GetProperty("adminGroup").GetString().Should().Be("admins");
    }

    [Fact]
    public async Task BehindAuthelia_APickedUserDoesNotClaimAdmin()
    {
        HttpClient guest = ThroughAuthelia("guest", "users");
        guest.DefaultRequestHeaders.Add("Cookie", "wb_watchUser=alice");

        (await AccessAsync(guest)).IsAdmin.Should().BeFalse();
        SavedAdmins().Should().BeEmpty();
    }

    [Fact]
    public async Task AProxyThatSendsNoGroups_FallsBackToThePickedUser()
    {
        HttpClient noGroups = ThroughAuthelia("isaac", null);

        (await SaveConfig(noGroups)).StatusCode.Should().Be(HttpStatusCode.OK, "no admins stored yet");
        await AccessAsync(await BrowserAsync("alice"));
        (await SaveConfig(noGroups)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "alice claimed it");
    }

    [Fact]
    public async Task AGroupAdmin_CannotRenameTheAdminGroupToOneTheyAreNotIn()
    {
        HttpClient admin = ThroughAuthelia("isaac", "admins");

        (await admin.PostAsJsonAsync("/api/auth/forward-auth",
                new { header = "Remote-User", trustedHost = "*", groupsHeader = "Remote-Groups", adminGroup = "wb-admins" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/auth/forward-auth",
                new { header = "Remote-User", trustedHost = "*", groupsHeader = "Remote-Groups", adminGroup = "Admins" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record Access(bool Restricted, bool IsAdmin, string[] Admins);
}
