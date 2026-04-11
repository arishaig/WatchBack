using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using WatchBack.Api.Endpoints;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Api.Tests;

// file-scoped stubs — mirrors the helpers in AuthEndpointTests.cs
file sealed class FwdAuthTestWatchProvider : IWatchStateProvider
{
    public DataProviderMetadata Metadata => new WatchStateDataProviderMetadata("TestWatch", "Test");
    public string ConfigSection => "TestWatch";
    public bool IsConfigured => true;
    public IReadOnlyList<ProviderConfigField> GetConfigSchema(Func<string, string> e, Func<string, string, bool> o) => [];
    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(new ServiceHealth(true, "OK", DateTimeOffset.UtcNow));
    public Task<ServiceHealth> TestConnectionAsync(IReadOnlyDictionary<string, string> f, CancellationToken ct = default) =>
        Task.FromResult(new ServiceHealth(true, "OK", DateTimeOffset.UtcNow));
    public string? RevealSecret(string key) => null;
    public Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default) =>
        Task.FromResult<MediaContext?>(null);
}

file sealed class FwdAuthTestThoughtProvider : IThoughtProvider
{
    public DataProviderMetadata Metadata => new("TestThought", "Test", BrandData: new BrandData("", ""));
    public string ConfigSection => "TestThought";
    public bool IsConfigured => false;
    public IReadOnlyList<ProviderConfigField> GetConfigSchema(Func<string, string> e, Func<string, string, bool> o) => [];
    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default) =>
        Task.FromResult(new ServiceHealth(false, "Not configured", DateTimeOffset.UtcNow));
    public int ExpectedWeight => 1;
    public string GetCacheKey(MediaContext m) => $"fwdauth-test:{m.Title}";
    public Task<ThoughtResult?> GetThoughtsAsync(MediaContext m, IProgress<SyncProgressTick>? p = null, CancellationToken ct = default) =>
        Task.FromResult<ThoughtResult?>(null);
}

/// <summary>
///     Startup filter that overrides <see cref="HttpContext.Connection.RemoteIpAddress"/> for every
///     request so that IP-based trust tests are deterministic regardless of the underlying
///     test server socket address family.
/// </summary>
file sealed class OverrideRemoteIpFilter(IPAddress remoteIp) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = remoteIp;
                await nextMiddleware(ctx);
            });
            next(app);
        };
    }
}

/// <summary>
///     Integration tests for <c>ForwardAuthHandler</c>.
/// </summary>
public sealed class ForwardAuthHandlerTests : IAsyncLifetime, IDisposable
{
    // Use a documentation IP (RFC 5737) so it cannot collide with any real loopback address.
    private const string TrustedIp = "192.0.2.1";
    private const string HeaderName = "X-Remote-User";

    private HttpClient _client = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private string _tempConfigPath = null!;

    public async Task InitializeAsync()
    {
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"watchback-fwdauth-test-{Guid.NewGuid()}.json");
        _factory = BuildFactory(_tempConfigPath, trustedHost: TrustedIp, overrideRemoteIp: TrustedIp);
        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_tempConfigPath))
        {
            File.Delete(_tempConfigPath);
        }

        GC.SuppressFinalize(this);
    }

    private static WebApplicationFactory<Program> BuildFactory(
        string tempConfigPath,
        string? headerName = HeaderName,
        string? trustedHost = null,
        string? overrideRemoteIp = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Auth:ForwardAuthHeader"] = headerName,
                        ["Auth:ForwardAuthTrustedHost"] = trustedHost,
                        ["Auth:OnboardingComplete"] = "true"
                    }));

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWatchStateProvider>();
                    services.RemoveAll<IThoughtProvider>();
                    services.AddScoped<IWatchStateProvider>(_ => new FwdAuthTestWatchProvider());
                    services.AddScoped<IThoughtProvider>(_ => new FwdAuthTestThoughtProvider());
                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(tempConfigPath));

                    if (overrideRemoteIp != null)
                    {
                        services.AddSingleton<IStartupFilter>(
                            new OverrideRemoteIpFilter(IPAddress.Parse(overrideRemoteIp)));
                    }
                });
            });
    }

    // ── No header ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetMe_NotAuthenticated_WhenHeaderAbsent()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeFalse();
    }

    // ── Header present, trusted host is a specific IP ────────────────────────
    // RemoteIpAddress is overridden to TrustedIp by OverrideRemoteIpFilter so the
    // IP comparison in the handler is deterministic across all test environments.

    [Fact]
    public async Task GetMe_Authenticated_WhenHeaderPresentAndTrustedHostMatchesRemoteIp()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add(HeaderName, "testuser");

        HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("authMethod").GetString().Should().Be("forwardAuth");
    }

    // ── TrustAll bypass ("any") ──────────────────────────────────────────────

    [Fact]
    public async Task GetMe_Authenticated_WhenTrustedHostIsAny()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"watchback-fwdauth-test-{Guid.NewGuid()}.json");
        try
        {
            using WebApplicationFactory<Program> factory = BuildFactory(tempPath, trustedHost: "any");
            using HttpClient client = factory.CreateClient();

            HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/me");
            request.Headers.Add(HeaderName, "anyone");

            HttpResponseMessage response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task GetMe_Authenticated_WhenTrustedHostIsWildcard()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"watchback-fwdauth-test-{Guid.NewGuid()}.json");
        try
        {
            using WebApplicationFactory<Program> factory = BuildFactory(tempPath, trustedHost: "*");
            using HttpClient client = factory.CreateClient();

            HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/me");
            request.Headers.Add(HeaderName, "anyone");

            HttpResponseMessage response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    // ── IP mismatch → not authenticated ─────────────────────────────────────

    [Fact]
    public async Task GetMe_NotAuthenticated_WhenTrustedHostIpDoesNotMatchRemoteIp()
    {
        // TrustedIp is "192.0.2.1" (overridden by filter); use a different trusted host so it won't match.
        string tempPath = Path.Combine(Path.GetTempPath(), $"watchback-fwdauth-test-{Guid.NewGuid()}.json");
        try
        {
            using WebApplicationFactory<Program> factory = BuildFactory(
                tempPath, trustedHost: "10.0.0.99", overrideRemoteIp: TrustedIp);
            using HttpClient client = factory.CreateClient();

            HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/me");
            request.Headers.Add(HeaderName, "testuser");

            HttpResponseMessage response = await client.SendAsync(request);

            JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    // ── Missing TrustedHost → forward auth disabled ──────────────────────────

    [Fact]
    public async Task GetMe_NotAuthenticated_WhenTrustedHostIsEmpty()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"watchback-fwdauth-test-{Guid.NewGuid()}.json");
        try
        {
            using WebApplicationFactory<Program> factory = BuildFactory(tempPath, trustedHost: "");
            using HttpClient client = factory.CreateClient();

            HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/me");
            request.Headers.Add(HeaderName, "testuser");

            HttpResponseMessage response = await client.SendAsync(request);

            JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // Header present but no trusted host → NoResult → not authenticated
            doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    // ── GetMe hides forwardAuth config from anonymous callers ───────────────

    [Fact]
    public async Task GetMe_DoesNotExposeForwardAuthConfigToUnauthenticatedCaller()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/auth/me");

        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeFalse();

        // forwardAuthHeader and forwardAuthTrustedHost should be null for unauthenticated callers
        JsonElement headerEl = doc.RootElement.GetProperty("forwardAuthHeader");
        headerEl.ValueKind.Should().Be(JsonValueKind.Null);

        JsonElement hostEl = doc.RootElement.GetProperty("forwardAuthTrustedHost");
        hostEl.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetMe_ExposesForwardAuthConfigToAuthenticatedCaller()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add(HeaderName, "testuser");

        HttpResponseMessage response = await _client.SendAsync(request);

        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("forwardAuthHeader").GetString().Should().Be(HeaderName);
        doc.RootElement.GetProperty("forwardAuthTrustedHost").GetString().Should().Be(TrustedIp);
    }
}
