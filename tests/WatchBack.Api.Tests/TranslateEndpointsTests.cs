using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using WatchBack.Core.Interfaces;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class TranslateEndpointsTests
{
    private const string Username = "admin";
    private const string Password = "test-password-123";

    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client)> StartAsync(
        ITranslator translator, string targetLanguage, bool login = true)
    {
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                string hash = new PasswordHasher<string>().HashPassword("", Password);
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Auth:Username"] = Username,
                        ["Auth:PasswordHash"] = hash,
                        ["Auth:OnboardingComplete"] = "true",
                        ["WatchBack:TranslateTargetLanguage"] = targetLanguage
                    }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ITranslator>();
                    services.AddScoped(_ => translator);
                });
            });
        HttpClient client = factory.CreateClient();
        if (login)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });
        }

        return (factory, client);
    }

    [Fact]
    public async Task Translate_ForeignText_ReturnsTranslationAndSourceLanguage()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync("Bonjour", "en", Arg.Any<CancellationToken>())
            .Returns(new TranslationResult("Hello", "fr", true));
        (WebApplicationFactory<Program> factory, HttpClient client) = await StartAsync(translator, "en");
        using (factory)
        using (client)
        {
            // Act
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/translate", new { text = "Bonjour" });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("text").GetString().Should().Be("Hello");
            json.GetProperty("translatedFrom").GetString().Should().Be("fr");
        }
    }

    [Fact]
    public async Task Translate_TextAlreadyInTargetLanguage_ReturnsNoSourceLanguage()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync("Hello", "en", Arg.Any<CancellationToken>())
            .Returns(new TranslationResult("Hello", "en", false));
        (WebApplicationFactory<Program> factory, HttpClient client) = await StartAsync(translator, "en");
        using (factory)
        using (client)
        {
            // Act
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/translate", new { text = "Hello" });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("translatedFrom").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Translate_TranslatorUnreachable_Returns503()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationResult("Bonjour", null, false, true));
        (WebApplicationFactory<Program> factory, HttpClient client) = await StartAsync(translator, "en");
        using (factory)
        using (client)
        {
            // Act
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/translate", new { text = "Bonjour" });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
    }

    [Fact]
    public async Task Translate_NoTargetLanguageConfigured_Returns400WithoutCallingTheTranslator()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        (WebApplicationFactory<Program> factory, HttpClient client) = await StartAsync(translator, "");
        using (factory)
        using (client)
        {
            // Act
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/translate", new { text = "Bonjour" });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await translator.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default!);
        }
    }

    [Fact]
    public async Task Translate_Unauthenticated_IsRejected()
    {
        // Arrange
        (WebApplicationFactory<Program> factory, HttpClient client) =
            await StartAsync(Substitute.For<ITranslator>(), "en", false);
        using (factory)
        using (client)
        {
            // Act
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/translate", new { text = "Bonjour" });

            // Assert
            response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect,
                HttpStatusCode.Forbidden);
        }
    }
}
