using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using WatchBack.Api.Endpoints;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Api.Tests;

// ---------------------------------------------------------------------------
// Concrete test providers — avoids uncertainty around NSubstitute and
// default interface member (DIM) interception.
// ---------------------------------------------------------------------------

file sealed class TestWatchProvider(
    string configSection = "TestWatch",
    bool requiresManualInput = false) : IWatchStateProvider
{
    public DataProviderMetadata Metadata => new WatchStateDataProviderMetadata(
        configSection, "Test watch provider")
    { RequiresManualInput = requiresManualInput };

    public string ConfigSection => configSection;
    public bool IsConfigured => true;

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return
        [
            new ProviderConfigField($"{configSection}__Url", "Server URL", "text", "", true, "http://test:8096", "",
                false)
        ];
    }

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ServiceHealth(true, "Connected", DateTimeOffset.UtcNow));
    }

    public Task<ServiceHealth> TestConnectionAsync(
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ServiceHealth(true, "Connected", DateTimeOffset.UtcNow));
    }

    public string? RevealSecret(string key)
    {
        return key == $"{configSection}__Secret" ? "revealed-value" : null;
    }

    public Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default)
    {
        return Task.FromResult<MediaContext?>(null);
    }
}

file sealed class NullSectionWatchProvider : IWatchStateProvider
{
    public DataProviderMetadata Metadata =>
        new WatchStateDataProviderMetadata("NoConfig", "Provider without config section");

    public string? ConfigSection => null; // explicitly no config panel

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ServiceHealth(true, "OK", DateTimeOffset.UtcNow));
    }

    public Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default)
    {
        return Task.FromResult<MediaContext?>(null);
    }
}

file sealed class TestThoughtProvider(string configSection = "TestThought") : IThoughtProvider
{
    public DataProviderMetadata Metadata =>
        new(configSection, "Test thought provider", BrandData: new BrandData("", ""));

    public string ConfigSection => configSection;
    public bool IsConfigured => false;

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return [];
    }

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ServiceHealth(false, "Not configured", DateTimeOffset.UtcNow));
    }

    public int ExpectedWeight => 100;

    public string GetCacheKey(MediaContext mediaContext)
    {
        return $"test:{mediaContext.Title}";
    }

    public Task<ThoughtResult?> GetThoughtsAsync(
        MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null,
        CancellationToken ct = default)
    {
        return Task.FromResult<ThoughtResult?>(null);
    }
}

// ---------------------------------------------------------------------------
// Shared factory helpers
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Standard scenario: one watch provider + one thought provider
// ---------------------------------------------------------------------------

public class ConfigEndpointsTests : IAsyncLifetime, IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";

    private static readonly string[] s_resetWatchUrlKeys = ["TestWatch__Url"];
    private static readonly string[] s_resetConnectionStringKeys = ["ConnectionStrings__Default"];
    private HttpClient _client = null!;

    private WebApplicationFactory<Program> _factory = null!;
    private string _tempConfigPath = null!;

    public async Task InitializeAsync()
    {
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"watchback-test-{Guid.NewGuid()}.json");

        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

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

                    services.AddScoped<IWatchStateProvider>(_ => new TestWatchProvider());
                    services.AddScoped<IThoughtProvider>(_ => new TestThoughtProvider());

                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(_tempConfigPath));
                });
            });

        _client = _factory.CreateClient();
        await LoginAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        if (File.Exists(_tempConfigPath))
        {
            File.Delete(_tempConfigPath);
        }

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

    // ---- Basic GET /api/config ----

    [Fact]
    public async Task GetConfig_ReturnsOkWithIntegrations()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("integrations");
        json.Should().Contain("preferences");
        json.Should().Contain("testwatch");
        json.Should().Contain("testthought");
    }

    [Fact]
    public async Task GetConfig_IntegrationContainsProviderTypes()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement integration = doc.RootElement
            .GetProperty("integrations")
            .GetProperty("testwatch");

        integration.TryGetProperty("providerTypes", out JsonElement pt).Should().BeTrue();
        pt.ValueKind.Should().Be(JsonValueKind.Array);
        pt.EnumerateArray().Select(e => e.GetString()).Should().Contain("watchState");
    }

    [Fact]
    public async Task GetConfig_ThoughtIntegrationContainsThoughtProviderType()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        string?[] providerTypes = doc.RootElement
            .GetProperty("integrations")
            .GetProperty("testthought")
            .GetProperty("providerTypes")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        providerTypes.Should().Contain("thought");
        providerTypes.Should().NotContain("watchState");
    }

    [Fact]
    public async Task GetConfig_SearchConfigured_FalseWhenNoRatingsProviders()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        doc.RootElement.GetProperty("preferences")
            .GetProperty("searchConfigured")
            .GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public async Task GetConfig_WatchProviders_IncludesRequiresManualInput()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement[] providers = doc.RootElement.GetProperty("preferences")
            .GetProperty("watchProviders")
            .EnumerateArray()
            .ToArray();

        providers.Should().NotBeEmpty();
        providers[0].TryGetProperty("requiresManualInput", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetConfig_Fields_ExposedFromProvider()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement[] fields = doc.RootElement
            .GetProperty("integrations")
            .GetProperty("testwatch")
            .GetProperty("fields")
            .EnumerateArray()
            .ToArray();

        fields.Should().HaveCount(1);
        fields[0].GetProperty("key").GetString().Should().Be("TestWatch__Url");
    }

    // ---- GET /api/status ----

    [Fact]
    public async Task GetStatus_ReturnsWatchProvider()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("watchProvider");
        json.Should().Contain("testwatch");
    }

    // ---- POST /api/config ----

    [Fact]
    public async Task SaveConfig_WithValidKeys_PersistsConfig()
    {
        Dictionary<string, string> payload = new() { ["TestWatch__Url"] = "http://test:8096" };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(_tempConfigPath).Should().BeTrue();
        string saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().Contain("http://test:8096");
    }

    [Fact]
    public async Task SaveConfig_WatchBackSection_AlwaysAllowed()
    {
        // WatchBack is always in allowed sections regardless of providers
        Dictionary<string, string> payload = new() { ["WatchBack__TimeMachineDays"] = "30" };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().Contain("30");
    }

    [Fact]
    public async Task SaveConfig_RejectsUnknownSections()
    {
        Dictionary<string, string> payload = new()
        {
            ["ConnectionStrings__Default"] = "Server=evil",
            ["TestWatch__Url"] = "http://legit:8096"
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().NotContain("ConnectionStrings");
        saved.Should().Contain("http://legit:8096");
    }

    [Fact]
    public async Task SaveConfig_PersistsEmptyValuesAsClearOperation()
    {
        // Empty string is a valid "clear" operation — it removes the key from config
        Dictionary<string, string> payload = new()
        {
            ["TestWatch__Url"] = "http://test:8096",
            ["TestWatch__Secret"] = ""
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().Contain("Url");
        // Empty string IS written — the empty value clears the setting
        saved.Should().Contain("Secret");
    }

    // ---- POST /api/test/{service} ----

    [Fact]
    public async Task TestService_KnownProvider_ReturnsHealthResult()
    {
        Dictionary<string, string> payload = new();
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/test/testwatch", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Connected");
    }

    [Fact]
    public async Task TestService_Unknown_ReturnsError()
    {
        Dictionary<string, string> payload = new();
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/test/unknown", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Unknown service");
    }

    // ---- POST /api/config/reveal/{key} ----

    [Fact]
    public async Task RevealConfigValue_KnownKey_ReturnsValue()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/config/reveal/TestWatch__Secret", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("revealed-value");
    }

    [Fact]
    public async Task RevealConfigValue_UnknownKey_ReturnsNotFound()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/config/reveal/Nonexistent__Key", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- DELETE /api/config ----

    [Fact]
    public async Task ResetConfig_RemovesSpecifiedKeys()
    {
        // First save a value
        await _client.PostAsJsonAsync("/api/config",
            new Dictionary<string, string> { ["TestWatch__Url"] = "http://test:8096" });
        File.Exists(_tempConfigPath).Should().BeTrue();

        // Then reset it
        HttpResponseMessage response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/config")
        {
            Content = JsonContent.Create(s_resetWatchUrlKeys)
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().NotContain("http://test:8096");
    }

    [Fact]
    public async Task ResetConfig_RejectsUnknownSections()
    {
        // Attempting to reset a key not in allowed sections — should not crash, just ignore
        HttpResponseMessage response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/config")
        {
            Content = JsonContent.Create(s_resetConnectionStringKeys)
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// ---------------------------------------------------------------------------
// Edge case: provider with null ConfigSection
// ---------------------------------------------------------------------------

public class ConfigEndpointsNullConfigSectionTests : IAsyncLifetime, IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";
    private HttpClient _client = null!;

    private WebApplicationFactory<Program> _factory = null!;
    private string _tempConfigPath = null!;

    public async Task InitializeAsync()
    {
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"watchback-test-{Guid.NewGuid()}.json");

        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

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

                    // One provider with a config section, one without
                    services.AddScoped<IWatchStateProvider>(_ => new TestWatchProvider("HasConfig"));
                    services.AddScoped<IWatchStateProvider>(_ => new NullSectionWatchProvider());

                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(_tempConfigPath));
                });
            });

        _client = _factory.CreateClient();
        var loginBody = new { username = TestUsername, password = TestPassword };
        (await _client.PostAsJsonAsync("/api/auth/login", loginBody)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        if (File.Exists(_tempConfigPath))
        {
            File.Delete(_tempConfigPath);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetConfig_NullConfigSection_ExcludedFromIntegrations()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement integrations = doc.RootElement.GetProperty("integrations");

        integrations.TryGetProperty("noconfig", out _).Should().BeFalse(
            "providers with null ConfigSection should not appear in integrations");
        integrations.TryGetProperty("hasconfig", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetConfig_NullConfigSection_ProviderStillAppearsInWatchProviders()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        string?[] watchProviders = doc.RootElement.GetProperty("preferences")
            .GetProperty("watchProviders")
            .EnumerateArray()
            .Select(e => e.GetProperty("value").GetString())
            .ToArray();

        // Both providers listed, regardless of ConfigSection
        watchProviders.Should().Contain("hasconfig");
        watchProviders.Should().Contain("noconfig");
    }

    [Fact]
    public async Task SaveConfig_NullConfigSectionProvider_KeysRejected()
    {
        // "NoConfig" is not in allowed sections (no ConfigSection), so its keys should be silently rejected
        Dictionary<string, string> payload = new() { ["NoConfig__Key"] = "secret" };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Nothing should be saved for the null-section provider
        if (File.Exists(_tempConfigPath))
        {
            string saved = await File.ReadAllTextAsync(_tempConfigPath);
            saved.Should().NotContain("secret");
        }
    }
}

// ---------------------------------------------------------------------------
// Edge case: shared ConfigSection between providers (Trakt pattern)
// ---------------------------------------------------------------------------

public class ConfigEndpointsSharedConfigSectionTests : IAsyncLifetime, IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";
    private HttpClient _client = null!;

    private WebApplicationFactory<Program> _factory = null!;
    private string _tempConfigPath = null!;

    public async Task InitializeAsync()
    {
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"watchback-test-{Guid.NewGuid()}.json");

        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

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

                    // Both providers share "SharedSection" — same pattern as Trakt watch + Trakt thought
                    services.AddScoped<IWatchStateProvider>(_ => new TestWatchProvider("SharedSection"));
                    services.AddScoped<IThoughtProvider>(_ => new TestThoughtProvider("SharedSection"));

                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(_tempConfigPath));
                });
            });

        _client = _factory.CreateClient();
        var loginBody = new { username = TestUsername, password = TestPassword };
        (await _client.PostAsJsonAsync("/api/auth/login", loginBody)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        if (File.Exists(_tempConfigPath))
        {
            File.Delete(_tempConfigPath);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetConfig_SharedConfigSection_ProducesOneIntegration()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement integrations = doc.RootElement.GetProperty("integrations");

        string[] keys = integrations.EnumerateObject().Select(p => p.Name).ToArray();
        keys.Should().ContainSingle(k => k == "sharedsection");
    }

    [Fact]
    public async Task GetConfig_SharedConfigSection_ProviderTypesContainsBothRoles()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        string?[] providerTypes = doc.RootElement
            .GetProperty("integrations")
            .GetProperty("sharedsection")
            .GetProperty("providerTypes")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        providerTypes.Should().Contain("watchState");
        providerTypes.Should().Contain("thought");
        providerTypes.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetConfig_SharedConfigSection_FieldsFromFirstProviderWithFields()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement[] fields = doc.RootElement
            .GetProperty("integrations")
            .GetProperty("sharedsection")
            .GetProperty("fields")
            .EnumerateArray()
            .ToArray();

        // TestWatchProvider returns one field; TestThoughtProvider returns none.
        // The watch provider owns the fields (thought provider has empty schema).
        fields.Should().HaveCount(1);
        fields[0].GetProperty("key").GetString().Should().Be("SharedSection__Url");
    }

    [Fact]
    public async Task SaveConfig_SharedConfigSection_SectionAllowed()
    {
        Dictionary<string, string> payload = new() { ["SharedSection__Url"] = "http://shared:8096" };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/config", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string saved = await File.ReadAllTextAsync(_tempConfigPath);
        saved.Should().Contain("http://shared:8096");
    }
}

// ---------------------------------------------------------------------------
// Edge case: RequiresManualInput on watch provider
// ---------------------------------------------------------------------------

public class ConfigEndpointsRequiresManualInputTests : IAsyncLifetime, IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";
    private HttpClient _client = null!;

    private WebApplicationFactory<Program> _factory = null!;
    private string _tempConfigPath = null!;

    public async Task InitializeAsync()
    {
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"watchback-test-{Guid.NewGuid()}.json");

        PasswordHasher<string> hasher = new();
        string hash = hasher.HashPassword("", TestPassword);

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

                    services.AddScoped<IWatchStateProvider>(_ =>
                        new TestWatchProvider("ManualProvider", true));

                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(_tempConfigPath));
                });
            });

        _client = _factory.CreateClient();
        var loginBody = new { username = TestUsername, password = TestPassword };
        (await _client.PostAsJsonAsync("/api/auth/login", loginBody)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        if (File.Exists(_tempConfigPath))
        {
            File.Delete(_tempConfigPath);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetConfig_RequiresManualInput_ExposedInWatchProviders()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement provider = doc.RootElement.GetProperty("preferences")
            .GetProperty("watchProviders")
            .EnumerateArray()
            .First(p => p.GetProperty("value").GetString() == "manualprovider");

        provider.GetProperty("requiresManualInput").GetBoolean().Should().BeTrue();
    }
}
