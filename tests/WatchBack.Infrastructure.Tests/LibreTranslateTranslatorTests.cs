using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.Translators;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class LibreTranslateTranslatorTests : IDisposable
{
    private readonly Dictionary<string, string?> _cache = [];
    private readonly RoutableMockHttpHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    private LibreTranslateTranslator CreateTranslator()
    {
        IOptionsMonitor<LibreTranslateOptions> options = Substitute.For<IOptionsMonitor<LibreTranslateOptions>>();
        options.CurrentValue.Returns(new LibreTranslateOptions { BaseUrl = "http://libre.test:5000" });
        IDurableLookupCache cache = Substitute.For<IDurableLookupCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Task.FromResult<LookupAnswer?>(_cache.TryGetValue(ci.ArgAt<string>(0), out string? v) ? new LookupAnswer(v) : null));
        cache.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _cache[ci.ArgAt<string>(0)] = ci.ArgAt<string?>(2);
                return Task.CompletedTask;
            });
        return new LibreTranslateTranslator(new HttpClient(_handler), options, cache,
            NullLogger<LibreTranslateTranslator>.Instance);
    }

    private static string Reply(string detected, string translated) =>
        JsonSerializer.Serialize(new { translatedText = translated, detectedLanguage = new { confidence = 90, language = detected } });

    [Fact]
    public async Task TranslateAsync_ForeignText_ReturnsTranslationAndDetectedLanguage()
    {
        // Arrange
        _handler.RespondTo("/translate", Reply("fr", "Great show"));

        // Act
        TranslationResult result = await CreateTranslator().TranslateAsync("Super série", "en");

        // Assert
        result.WasTranslated.Should().BeTrue();
        result.Text.Should().Be("Great show");
        result.DetectedLanguage.Should().Be("fr");
    }

    [Fact]
    public async Task TranslateAsync_AlreadyInTargetLanguage_IsNotMarkedTranslated()
    {
        // Arrange
        _handler.RespondTo("/translate", Reply("en", "Great show"));

        // Act
        TranslationResult result = await CreateTranslator().TranslateAsync("Great show", "en");

        // Assert
        result.WasTranslated.Should().BeFalse();
    }

    [Fact]
    public async Task TranslateAsync_SecondCall_ComesFromCacheWithoutHttp()
    {
        // Arrange
        _handler.RespondTo("/translate", Reply("fr", "Great show"));
        LibreTranslateTranslator translator = CreateTranslator();
        await translator.TranslateAsync("Super série", "en");

        // Act
        TranslationResult second = await translator.TranslateAsync("Super série", "en");

        // Assert
        second.Text.Should().Be("Great show");
        _handler.RecordedUris.Should().HaveCount(1);
    }

    [Fact]
    public async Task TranslateAsync_UpstreamError_IsTransientAndNotCached()
    {
        // Arrange
        _handler.Default("{}", HttpStatusCode.InternalServerError);

        // Act
        TranslationResult result = await CreateTranslator().TranslateAsync("Super série", "en");

        // Assert
        result.IsTransientFailure.Should().BeTrue();
        _cache.Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnectionAsync_ReachableServer_IsHealthy()
    {
        // Arrange
        _handler.RespondTo("/languages", "[]");

        // Act
        ServiceHealth health = await CreateTranslator().TestConnectionAsync(
            new Dictionary<string, string> { ["LibreTranslate__BaseUrl"] = "http://libre.test:5000" });

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnectionAsync_NotAnHttpUrl_IsRejectedWithoutARequest()
    {
        // Act
        ServiceHealth health = await CreateTranslator().TestConnectionAsync(
            new Dictionary<string, string> { ["LibreTranslate__BaseUrl"] = "ftp://libre" });

        // Assert
        health.IsHealthy.Should().BeFalse();
        _handler.RecordedUris.Should().BeEmpty();
    }
}
