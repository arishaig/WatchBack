using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.Translators;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class OllamaTranslatorTests : IDisposable
{
    private readonly FakeLookupCache _cache = new();
    private readonly RoutableMockHttpHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    private OllamaTranslator CreateTranslator()
    {
        IOptionsMonitor<OllamaOptions> options = Substitute.For<IOptionsMonitor<OllamaOptions>>();
        options.CurrentValue.Returns(new OllamaOptions { BaseUrl = "http://ollama.test", Model = "test-model" });
        HttpClient http = new(_handler);
        return new OllamaTranslator(http, options, _cache, NullLogger<OllamaTranslator>.Instance);
    }

    private static string Reply(string detected, string translation) =>
        JsonSerializer.Serialize(new
        {
            message = new
            {
                role = "assistant",
                content = JsonSerializer.Serialize(new { detectedLanguage = detected, translation })
            }
        });

    [Fact]
    public async Task TranslateAsync_ForeignText_ReturnsTranslationAndDetectedLanguage()
    {
        // Arrange
        _handler.RespondTo("/api/chat", Reply("fr", "Great show"));

        // Act
        TranslationResult result = await CreateTranslator().TranslateAsync("Super série", "en");

        // Assert
        result.WasTranslated.Should().BeTrue();
        result.Text.Should().Be("Great show");
        result.DetectedLanguage.Should().Be("fr");
        result.IsTransientFailure.Should().BeFalse();
    }

    [Fact]
    public async Task TranslateAsync_AlreadyInTargetLanguage_IsNotMarkedTranslated()
    {
        // Arrange
        _handler.RespondTo("/api/chat", Reply("en", "Great show"));

        // Act
        TranslationResult result = await CreateTranslator().TranslateAsync("Great show", "en");

        // Assert
        result.WasTranslated.Should().BeFalse();
        result.Text.Should().Be("Great show");
    }

    [Fact]
    public async Task TranslateAsync_ModelEchoesTextWithWrongDetectedLanguage_IsNotMarkedTranslated()
    {
        // Arrange: small models sometimes mislabel the language but return the text unchanged
        _handler.RespondTo("/api/chat", Reply("de", "Great show"));

        // Act
        TranslationResult result = await CreateTranslator().TranslateAsync("Great show", "en");

        // Assert
        result.WasTranslated.Should().BeFalse();
    }

    [Fact]
    public async Task TranslateAsync_SecondCallForSameText_ComesFromCacheWithoutHttp()
    {
        // Arrange
        _handler.RespondTo("/api/chat", Reply("fr", "Great show"));
        OllamaTranslator translator = CreateTranslator();
        await translator.TranslateAsync("Super série", "en");

        // Act
        TranslationResult second = await translator.TranslateAsync("Super série", "en");

        // Assert
        second.Text.Should().Be("Great show");
        second.WasTranslated.Should().BeTrue();
        _handler.RecordedUris.Should().HaveCount(1);
    }

    [Fact]
    public async Task TranslateAsync_AlreadyTargetLanguageAnswer_IsRememberedToo()
    {
        // Arrange
        _handler.RespondTo("/api/chat", Reply("en", "Great show"));
        OllamaTranslator translator = CreateTranslator();
        await translator.TranslateAsync("Great show", "en");

        // Act
        TranslationResult second = await translator.TranslateAsync("Great show", "en");

        // Assert
        second.WasTranslated.Should().BeFalse();
        _handler.RecordedUris.Should().HaveCount(1);
    }

    [Fact]
    public async Task TranslateAsync_UpstreamError_IsTransientAndNotCached()
    {
        // Arrange
        _handler.Default("{}", HttpStatusCode.InternalServerError);
        OllamaTranslator translator = CreateTranslator();

        // Act
        TranslationResult result = await translator.TranslateAsync("Super série", "en");

        // Assert
        result.IsTransientFailure.Should().BeTrue();
        result.WasTranslated.Should().BeFalse();
        result.Text.Should().Be("Super série");
        _cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task TranslateAsync_MalformedModelOutput_IsTransientAndNotCached()
    {
        // Arrange
        _handler.RespondTo("/api/chat", JsonSerializer.Serialize(new { message = new { content = "not json" } }));

        // Act
        TranslationResult result = await CreateTranslator().TranslateAsync("Super série", "en");

        // Assert
        result.IsTransientFailure.Should().BeTrue();
        _cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task TranslateAsync_TextOverLimit_IsLeftAloneWithoutHttp()
    {
        // Arrange
        string longText = new('a', OllamaTranslator.MaxTextLength + 1);

        // Act
        TranslationResult result = await CreateTranslator().TranslateAsync(longText, "en");

        // Assert
        result.WasTranslated.Should().BeFalse();
        result.IsTransientFailure.Should().BeFalse();
        _handler.RecordedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task TranslateAsync_DifferentTargetLanguages_DoNotShareCacheEntries()
    {
        // Arrange
        _handler.RespondTo("/api/chat", Reply("fr", "translated"));
        OllamaTranslator translator = CreateTranslator();
        await translator.TranslateAsync("Super série", "en");

        // Act
        await translator.TranslateAsync("Super série", "es");

        // Assert
        _handler.RecordedUris.Should().HaveCount(2);
    }

    private sealed class FakeLookupCache : IDurableLookupCache
    {
        private readonly Dictionary<string, string?> _items = [];

        public int Count => _items.Count;

        public Task<LookupAnswer?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<LookupAnswer?>(_items.TryGetValue(key, out string? v) ? new LookupAnswer(v) : null);

        public Task SetAsync(string key, string kind, string? value, TimeSpan lifetime,
            CancellationToken ct = default)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }
    }
}
