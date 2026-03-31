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

file sealed class AuthTestWatchProvider : IWatchStateProvider
{
    public DataProviderMetadata Metadata =>
        new WatchStateDataProviderMetadata("TestWatch", "Test");

    public string ConfigSection => "TestWatch";
    public bool IsConfigured => true;

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return [];
    }

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ServiceHealth(true, "OK", DateTimeOffset.UtcNow));
    }

    public Task<ServiceHealth> TestConnectionAsync(
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ServiceHealth(true, "OK", DateTimeOffset.UtcNow));
    }

    public string? RevealSecret(string key)
    {
        return null;
    }

    public Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default)
    {
        return Task.FromResult<MediaContext?>(null);
    }
}

file sealed class AuthTestThoughtProvider : IThoughtProvider
{
    public DataProviderMetadata Metadata =>
        new("TestThought", "Test", BrandData: new BrandData("", ""));

    public string ConfigSection => "TestThought";
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

    public int ExpectedWeight => 1;

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
// ChangePassword tests
// ---------------------------------------------------------------------------

public class ChangePasswordTests : IAsyncLifetime, IDisposable
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
                    services.AddScoped<IWatchStateProvider>(_ => new AuthTestWatchProvider());
                    services.AddScoped<IThoughtProvider>(_ => new AuthTestThoughtProvider());
                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(_tempConfigPath));
                });
            });

        _client = _factory.CreateClient();
        await LoginAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

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

    private async Task LoginAsync()
    {
        var loginBody = new { username = TestUsername, password = TestPassword };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", loginBody);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ChangePassword_RejectsEmptyCurrentPassword()
    {
        var body = new { currentPassword = "", newPassword = "NewPass1!@#789" };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/change-password", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ChangePassword_RejectsEmptyNewPassword()
    {
        var body = new { currentPassword = TestPassword, newPassword = "" };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/change-password", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ChangePassword_RejectsIncorrectCurrentPassword()
    {
        var body = new { currentPassword = "WrongPassword123!", newPassword = "NewPass1!@#789" };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/change-password", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ChangePassword_SucceedsWithCorrectCurrentPassword()
    {
        const string newPassword = "NewPass1!@#789";
        var body = new { currentPassword = TestPassword, newPassword };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/change-password", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_PersistsNewHash()
    {
        const string newPassword = "NewPass1!@#789";
        var body = new { currentPassword = TestPassword, newPassword };
        await _client.PostAsJsonAsync("/api/auth/change-password", body);

        // Verify the new hash was persisted and can verify the new password
        string json = await File.ReadAllTextAsync(_tempConfigPath);
        JsonDocument doc = JsonDocument.Parse(json);
        string persistedHash = doc.RootElement.GetProperty("Auth").GetProperty("PasswordHash").GetString()!;

        PasswordHasher<string> hasher = new();
        hasher.VerifyHashedPassword("", persistedHash, newPassword)
            .Should().NotBe(PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task ChangePassword_RequiresAuthentication()
    {
        // Create a new client without logging in
        HttpClient unauthClient = _factory.CreateClient();
        var body = new { currentPassword = TestPassword, newPassword = "NewPass1!@#789" };
        HttpResponseMessage response = await unauthClient.PostAsJsonAsync("/api/auth/change-password", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

// ---------------------------------------------------------------------------
// ResetPassword tests — verifies the reset → login → forced password change flow
// ---------------------------------------------------------------------------

public class ResetPasswordTests : IAsyncLifetime, IDisposable
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
                    services.AddScoped<IWatchStateProvider>(_ => new AuthTestWatchProvider());
                    services.AddScoped<IThoughtProvider>(_ => new AuthTestThoughtProvider());
                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(_tempConfigPath));
                });
            });

        _client = _factory.CreateClient();
        await LoginAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

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

    private async Task LoginAsync()
    {
        var loginBody = new { username = TestUsername, password = TestPassword };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", loginBody);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ResetPassword_ReturnsOkWithMessage()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/auth/reset-password", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("message").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResetPassword_SignsOutCurrentSession()
    {
        await _client.PostAsync("/api/auth/reset-password", null);

        // Subsequent authenticated call should fail (session invalidated)
        HttpResponseMessage response = await _client.GetAsync("/api/config");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResetPassword_PersistsDifferentHash()
    {
        // Capture the original hash
        PasswordHasher<string> hasher = new();
        string originalHash = hasher.HashPassword("", TestPassword);

        await _client.PostAsync("/api/auth/reset-password", null);

        // The persisted hash should differ from the original password's hash
        string json = await File.ReadAllTextAsync(_tempConfigPath);
        JsonDocument doc = JsonDocument.Parse(json);
        string newHash = doc.RootElement.GetProperty("Auth").GetProperty("PasswordHash").GetString()!;

        // The old password should not verify against the new hash
        hasher.VerifyHashedPassword("", newHash, TestPassword)
            .Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task ResetPassword_SetsPasswordResetPending()
    {
        await _client.PostAsync("/api/auth/reset-password", null);

        // Read the persisted config to verify the flag was set
        string json = await File.ReadAllTextAsync(_tempConfigPath);
        JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("Auth")
            .GetProperty("PasswordResetPending")
            .GetString().Should().Be("True");
    }

    [Fact]
    public async Task ResetPassword_PreservesOnboardingComplete()
    {
        await _client.PostAsync("/api/auth/reset-password", null);

        // Onboarding should remain complete (not reset to false)
        string json = await File.ReadAllTextAsync(_tempConfigPath);
        JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("Auth")
            .GetProperty("OnboardingComplete")
            .GetString().Should().Be("True");
    }

    [Fact]
    public async Task ResetPassword_LoginReturnNeedsPasswordChange()
    {
        // Reset sets the flag, then we need the new temp password from the config.
        // Since we can't read stdout, we read the hash and create a new factory
        // with a known password to simulate the flow.
        _tempConfigPath = Path.Combine(Path.GetTempPath(), $"watchback-test-{Guid.NewGuid()}.json");

        const string tempPassword = "TempPass1!@#456";
        PasswordHasher<string> hasher = new();
        string tempHash = hasher.HashPassword("", tempPassword);

        // Write a config file that simulates post-reset state
        string configJson = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            ["Auth"] = new()
            {
                ["Username"] = TestUsername,
                ["PasswordHash"] = tempHash,
                ["OnboardingComplete"] = "True",
                ["PasswordResetPending"] = "True"
            }
        });
        await File.WriteAllTextAsync(_tempConfigPath, configJson);

        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddJsonFile(_tempConfigPath, false, false));

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWatchStateProvider>();
                    services.RemoveAll<IThoughtProvider>();
                    services.AddScoped<IWatchStateProvider>(_ => new AuthTestWatchProvider());
                    services.AddScoped<IThoughtProvider>(_ => new AuthTestThoughtProvider());
                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(_tempConfigPath));
                });
            });

        using HttpClient client = factory.CreateClient();

        // Login with the temp password
        var loginBody = new { username = TestUsername, password = tempPassword };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", loginBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("needsPasswordChange").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("needsOnboarding").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ChangePassword_ClearsResetPendingFlag()
    {
        const string tempPassword = "TempPass1!@#456";
        const string finalPassword = "FinalPass1!@#789";

        PasswordHasher<string> hasher = new();
        string tempHash = hasher.HashPassword("", tempPassword);

        string tempPath = Path.Combine(Path.GetTempPath(), $"watchback-test-{Guid.NewGuid()}.json");
        string configJson = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            ["Auth"] = new()
            {
                ["Username"] = TestUsername,
                ["PasswordHash"] = tempHash,
                ["OnboardingComplete"] = "True",
                ["PasswordResetPending"] = "True"
            }
        });
        await File.WriteAllTextAsync(tempPath, configJson);

        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddJsonFile(tempPath, false, false));

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IWatchStateProvider>();
                    services.RemoveAll<IThoughtProvider>();
                    services.AddScoped<IWatchStateProvider>(_ => new AuthTestWatchProvider());
                    services.AddScoped<IThoughtProvider>(_ => new AuthTestThoughtProvider());
                    services.RemoveAll<UserConfigFile>();
                    services.AddSingleton(new UserConfigFile(tempPath));
                });
            });

        using HttpClient client = factory.CreateClient();

        // Login
        var loginBody = new { username = TestUsername, password = tempPassword };
        await client.PostAsJsonAsync("/api/auth/login", loginBody);

        // Change password
        var changeBody = new { currentPassword = tempPassword, newPassword = finalPassword };
        HttpResponseMessage changeResponse = await client.PostAsJsonAsync("/api/auth/change-password", changeBody);
        changeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify the flag was cleared in the persisted config
        string json = await File.ReadAllTextAsync(tempPath);
        Dictionary<string, Dictionary<string, string>>? persisted =
            JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);

        persisted.Should().NotBeNull();
        persisted!.Should().ContainKey("Auth");
        persisted["Auth"].Should().ContainKey("PasswordResetPending");
        persisted["Auth"]["PasswordResetPending"].Should().Be("False");

        // Also verify the new password hash was persisted
        persisted["Auth"].Should().ContainKey("PasswordHash");
        PasswordHasher<string> verifier = new();
        verifier.VerifyHashedPassword("", persisted["Auth"]["PasswordHash"], finalPassword)
            .Should().NotBe(PasswordVerificationResult.Failed);

        File.Delete(tempPath);
    }

    [Fact]
    public async Task ResetPassword_RequiresAuthentication()
    {
        HttpClient unauthClient = _factory.CreateClient();
        HttpResponseMessage response = await unauthClient.PostAsync("/api/auth/reset-password", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
