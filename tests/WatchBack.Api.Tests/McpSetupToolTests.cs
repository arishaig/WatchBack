using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using WatchBack.Api.Endpoints;

using Xunit;

namespace WatchBack.Api.Tests;

// The MCP tools an assistant uses to walk someone through setup, called the way a client calls them.
public sealed class McpSetupToolTests : IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestMcp1!@#456";

    // Secrets as they'd be configured; none may ever appear in what the tools return.
    private static readonly Dictionary<string, string?> s_secrets = new()
    {
        ["Jellyfin:ApiKey"] = "jf-secret-4f9a1c",
        ["Bluesky:AppPassword"] = "bsky-secret-77e2",
        ["Trakt:AccessToken"] = "trakt-secret-0b3d",
        ["Omdb:ApiKey"] = "omdb-secret-91aa"
    };

    private static readonly string[] s_resetKeys = ["WatchBack__TimeMachineDays", "Auth__PasswordHash"];

    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"wb-mcp-{Guid.NewGuid():N}.json");
    private readonly WebApplicationFactory<Program> _factory;

    public McpSetupToolTests()
    {
        string hash = new PasswordHasher<string>().HashPassword("", TestPassword);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>(s_secrets)
                {
                    ["Auth:Username"] = TestUsername,
                    ["Auth:PasswordHash"] = hash,
                    ["Auth:OnboardingComplete"] = "true",
                    ["Bluesky:Handle"] = "someone.bsky.social",
                    ["Jellyfin:BaseUrl"] = "http://jellyfin.test:8096"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UserConfigFile>();
                services.AddSingleton(new UserConfigFile(_configPath));
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        File.Delete(_configPath);
    }

    private async Task<HttpClient> ClientAsync()
    {
        HttpClient client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { username = TestUsername, password = TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    // The JSON-RPC result, whether the server answered as JSON or as one SSE event.
    private static async Task<JsonElement> RpcAsync(HttpClient client, string method, object parameters)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        string json = body.Split('\n').FirstOrDefault(l => l.StartsWith("data: ", StringComparison.Ordinal))?[6..] ?? body;
        return JsonDocument.Parse(json).RootElement.GetProperty("result");
    }

    private static async Task<(string Raw, JsonElement Json)> CallAsync(HttpClient client, string tool, object? args = null)
    {
        JsonElement result = await RpcAsync(client, "tools/call", new { name = tool, arguments = args ?? new { } });
        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        return (text, JsonDocument.Parse(text).RootElement);
    }

    private Dictionary<string, Dictionary<string, string>> Saved() =>
        File.Exists(_configPath)
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_configPath))!
            : [];

    [Fact]
    public async Task Initialize_GivesTheSetupInstructions_AndTheWalkthroughPrompt()
    {
        using HttpClient client = await ClientAsync();

        JsonElement init = await RpcAsync(client, "initialize",
            new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        JsonElement prompts = await RpcAsync(client, "prompts/list", new { });
        JsonElement prompt = await RpcAsync(client, "prompts/get", new { name = "set_up_watchback" });

        init.GetProperty("instructions").GetString().Should().Contain("get_settings_link");
        prompts.GetProperty("prompts").EnumerateArray().Select(p => p.GetProperty("name").GetString())
            .Should().Contain("set_up_watchback");
        prompt.GetRawText().Should().Contain("describe_integrations");
    }

    [Fact]
    public async Task DescribeIntegrations_ListsSettings_AndNeverASecretValue()
    {
        using HttpClient client = await ClientAsync();

        (string raw, JsonElement json) = await CallAsync(client, "describe_integrations");

        foreach (string? secret in s_secrets.Values)
        {
            raw.Should().NotContain(secret!);
        }

        JsonElement jellyfin = json.GetProperty("integrations").EnumerateArray()
            .Single(i => i.GetProperty("section").GetString() == "Jellyfin");
        JsonElement apiKey = jellyfin.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "Jellyfin__ApiKey");
        apiKey.GetProperty("secret").GetBoolean().Should().BeTrue();
        apiKey.GetProperty("hasValue").GetBoolean().Should().BeTrue();
        jellyfin.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "Jellyfin__BaseUrl")
            .GetProperty("value").GetString().Should().Be("http://jellyfin.test:8096");
        jellyfin.GetProperty("settingsLink").GetString().Should().EndWith("/#settings/jellyfin");
        jellyfin.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Contain("watching");
    }

    [Fact]
    public async Task UpdateConfig_SavesKnownSettings_AndExplainsEachRejection()
    {
        using HttpClient client = await ClientAsync();

        (_, JsonElement json) = await CallAsync(client, "update_config", new
        {
            values = new Dictionary<string, string>
            {
                ["Jellyfin__BaseUrl"] = "http://media:8096",
                ["WatchBack__TimeMachineDays"] = "7",
                ["Jellyfin__Url"] = "http://typo:8096",
                ["WatchBack__TimeMachineDays2"] = "1",
                ["WatchBack__WatchProvider"] = "plex"
            }
        });

        json.GetProperty("applied").EnumerateArray().Select(a => a.GetString())
            .Should().BeEquivalentTo("Jellyfin__BaseUrl", "WatchBack__TimeMachineDays");
        Dictionary<string, string> reasons = json.GetProperty("rejected").EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("reason").GetString()!);
        reasons["Jellyfin__Url"].Should().Contain("Jellyfin__BaseUrl", "the reason names the real settings");
        reasons.Should().ContainKey("WatchBack__TimeMachineDays2");
        reasons["WatchBack__WatchProvider"].Should().Contain("jellyfin");
        Saved()["Jellyfin"].Should().Equal(new Dictionary<string, string> { ["BaseUrl"] = "http://media:8096" });
    }

    // An API key may configure sources, but must not be able to change how people log in.
    [Theory]
    [InlineData("Auth__ForwardAuthHeader", "X-Remote-User")]
    [InlineData("Auth__ForwardAuthTrustedHost", "any")]
    [InlineData("Auth__PasswordHash", "x")]
    public async Task UpdateConfig_RejectsLoginSettings(string key, string value)
    {
        using HttpClient client = await ClientAsync();

        (_, JsonElement json) = await CallAsync(client, "update_config",
            new { values = new Dictionary<string, string> { [key] = value } });

        json.GetProperty("applied").GetArrayLength().Should().Be(0);
        Saved().Should().NotContainKey("Auth");
    }

    [Fact]
    public async Task SetProviderEnabled_TurnsAnIntegrationOffAndOn()
    {
        using HttpClient client = await ClientAsync();

        await CallAsync(client, "set_provider_enabled", new { section = "lemmy", enabled = false });
        string off = Saved()["WatchBack"]["DisabledProviders"];
        await CallAsync(client, "set_provider_enabled", new { section = "Lemmy", enabled = true });

        off.Should().Be("Lemmy");
        Saved()["WatchBack"]["DisabledProviders"].Should().BeEmpty();
    }

    [Fact]
    public async Task GetSettingsLink_PointsAtTheIntegration_AndUnknownSectionsSayWhatExists()
    {
        using HttpClient client = await ClientAsync();

        (_, JsonElement link) = await CallAsync(client, "get_settings_link", new { section = "Bluesky" });
        (_, JsonElement unknown) = await CallAsync(client, "get_settings_link", new { section = "Plex" });

        link.GetProperty("link").GetString().Should().Be("http://localhost/#settings/bluesky");
        unknown.GetProperty("error").GetString().Should().Contain("Jellyfin");
    }

    [Fact]
    public async Task ResetConfig_RemovesSavedValues_AndRejectsUnknownKeys()
    {
        using HttpClient client = await ClientAsync();
        await CallAsync(client, "update_config",
            new { values = new Dictionary<string, string> { ["WatchBack__TimeMachineDays"] = "3" } });

        (_, JsonElement json) = await CallAsync(client, "reset_config",
            new { keys = s_resetKeys });

        json.GetProperty("reset").EnumerateArray().Select(a => a.GetString()).Should().Equal("WatchBack__TimeMachineDays");
        json.GetProperty("rejected").GetArrayLength().Should().Be(1);
        Saved()["WatchBack"].Should().NotContainKey("TimeMachineDays");
    }
}
