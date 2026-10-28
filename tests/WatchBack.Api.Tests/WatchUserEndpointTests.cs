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

public class WatchUserEndpointTests : IDisposable
{
    private const string TestUsername = "testadmin";
    private const string TestPassword = "TestPass1!@#456";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
        {
            d.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<HttpClient> CreateClientAsync(IWatchStateProvider provider)
    {
        string hash = new PasswordHasher<string>().HashPassword("", TestPassword);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
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
                    services.AddScoped(_ => provider);
                });
            });
        HttpClient client = factory.CreateClient();
        _disposables.Add(factory);
        _disposables.Add(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { username = TestUsername, password = TestPassword }))
            .EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task GetUsers_WithUserDirectoryProvider_ReturnsUsers()
    {
        IWatchStateProvider provider = Substitute.For<IWatchStateProvider, IWatchUserDirectory>();
        provider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        provider.IsConfigured.Returns(true);
        ((IWatchUserDirectory)provider).GetUsersAsync(Arg.Any<CancellationToken>())
            .Returns([new WatchUser("a", "Alice"), new WatchUser("b", "Bob")]);
        HttpClient client = await CreateClientAsync(provider);

        HttpResponseMessage response = await client.GetAsync("/api/watchstate/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        WatchUser[]? users = await response.Content.ReadFromJsonAsync<WatchUser[]>(
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        users.Should().Equal(new WatchUser("a", "Alice"), new WatchUser("b", "Bob"));
    }

    [Fact]
    public async Task GetUsers_WithProviderLackingUserDirectory_ReturnsEmptyList()
    {
        IWatchStateProvider provider = Substitute.For<IWatchStateProvider>();
        provider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        provider.IsConfigured.Returns(true);
        HttpClient client = await CreateClientAsync(provider);

        HttpResponseMessage response = await client.GetAsync("/api/watchstate/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("[]");
    }
}
