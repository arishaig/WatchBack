using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace WatchBack.Api.Tests;

public class McpEndpointTests
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestMcp1!@#456";

    [Fact]
    public async Task Mcp_Unauthenticated_Returns401()
    {
        using WebApplicationFactory<Program> factory = BuildFactory();
        using HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.SendAsync(McpRequest(1, "initialize",
            """{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_Authenticated_ReturnsSuccessfulInitialize()
    {
        using WebApplicationFactory<Program> factory = BuildFactory();
        using HttpClient client = factory.CreateClient();
        await LoginAsync(client);

        HttpResponseMessage response = await client.SendAsync(McpRequest(1, "initialize",
            """{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"result\"");
        body.Should().Contain("protocolVersion");
    }

    [Fact]
    public async Task Mcp_Authenticated_ToolsListContainsExpectedTools()
    {
        using WebApplicationFactory<Program> factory = BuildFactory();
        using HttpClient client = factory.CreateClient();
        await LoginAsync(client);

        await client.SendAsync(McpRequest(1, "initialize",
            """{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}"""));

        HttpResponseMessage response = await client.SendAsync(McpRequest(2, "tools/list", "{}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("get_watch_state");
        body.Should().Contain("trigger_sync");
        body.Should().Contain("get_config");
        body.Should().Contain("update_config");
        body.Should().Contain("reset_config");
        body.Should().Contain("set_manual_watch_state");
        body.Should().Contain("clear_manual_watch_state");
    }

    private static HttpRequestMessage McpRequest(int id, string method, string paramsJson)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static WebApplicationFactory<Program> BuildFactory()
    {
        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

        return new WebApplicationFactory<Program>()
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
            });
    }

    private static async Task LoginAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = TestUsername, password = TestPassword });
        response.EnsureSuccessStatusCode();
    }
}
