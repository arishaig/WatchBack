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

public sealed class OllamaSummarizerTests : IDisposable
{
    private readonly Dictionary<string, string?> _cache = [];
    private readonly RoutableMockHttpHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    private OllamaSummarizer Create(OllamaOptions? opts = null)
    {
        IOptionsMonitor<OllamaOptions> options = Substitute.For<IOptionsMonitor<OllamaOptions>>();
        options.CurrentValue.Returns(opts ?? new OllamaOptions { BaseUrl = "http://ollama.test", Model = "m" });
        IDurableLookupCache cache = Substitute.For<IDurableLookupCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Task.FromResult<LookupAnswer?>(_cache.TryGetValue(ci.ArgAt<string>(0), out string? v) ? new LookupAnswer(v) : null));
        cache.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _cache[ci.ArgAt<string>(0)] = ci.ArgAt<string?>(2);
                return Task.CompletedTask;
            });
        return new OllamaSummarizer(new HttpClient(_handler), options, cache, NullLogger<OllamaSummarizer>.Instance);
    }

    private static string Reply(string text) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content = text } });

    [Fact]
    public void IsConfigured_NeedsBothAServerAndAModel()
    {
        Create(new OllamaOptions()).IsConfigured.Should().BeFalse();
        Create(new OllamaOptions { BaseUrl = "http://x" }).IsConfigured.Should().BeFalse();
        Create(new OllamaOptions { Model = "m" }).IsConfigured.Should().BeFalse();
        Create().IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task SummarizeAsync_ReturnsTheModelsText_AndSecondCallComesFromCache()
    {
        _handler.RespondTo("/api/chat", Reply("  Viewers loved it.  "));
        OllamaSummarizer summarizer = Create();

        SummaryResult first = await summarizer.SummarizeAsync("Show", ["Great", "Bad"], "en");
        SummaryResult second = await summarizer.SummarizeAsync("Show", ["Great", "Bad"], "en");

        first.Text.Should().Be("Viewers loved it.");
        second.Text.Should().Be("Viewers loved it.");
        _handler.RecordedUris.Should().HaveCount(1);
    }

    [Fact]
    public async Task SummarizeAsync_DifferentCommentsOrLanguage_AreNotServedFromTheSameCacheEntry()
    {
        _handler.RespondTo("/api/chat", Reply("S"));
        OllamaSummarizer summarizer = Create();

        await summarizer.SummarizeAsync("Show", ["Great"], "en");
        await summarizer.SummarizeAsync("Show", ["Great", "New one"], "en");
        await summarizer.SummarizeAsync("Show", ["Great"], "es");

        _handler.RecordedUris.Should().HaveCount(3);
    }

    [Fact]
    public async Task SummarizeAsync_WithEarlyComments_SendsBothGroupsAndAsksForTheComparison()
    {
        // Arrange
        string? sent = null;
        _handler.RespondTo("/api/chat", Reply("Cooled."));
        OllamaSummarizer summarizer = Create();

        // Act
        await summarizer.SummarizeAsync("Show", ["later one", "later two"], "en", ["early one"]);
        sent = _handler.RecordedBodies.Single();

        // Assert
        sent.Should().Contain("Early reactions:").And.Contain("1. early one")
            .And.Contain("Later comments:").And.Contain("1. later one").And.Contain("2. later two")
            .And.Contain("comparing the early reactions with the later ones");
    }

    [Fact]
    public async Task SummarizeAsync_WithoutEarlyComments_AsksNoComparison()
    {
        // Arrange
        _handler.RespondTo("/api/chat", Reply("S"));

        // Act
        await Create().SummarizeAsync("Show", ["one"], "en");

        // Assert
        _handler.RecordedBodies.Single().Should().Contain("Comments:").And.NotContain("Early reactions")
            .And.NotContain("comparing the early");
    }

    [Fact]
    public async Task SummarizeAsync_ComparedAndPlainSummaries_DoNotShareACacheEntry()
    {
        // Arrange
        _handler.RespondTo("/api/chat", Reply("S"));
        OllamaSummarizer summarizer = Create();

        // Act
        await summarizer.SummarizeAsync("Show", ["later"], "en", ["early"]);
        await summarizer.SummarizeAsync("Show", ["later"], "en");
        await summarizer.SummarizeAsync("Show", ["later"], "en", ["early"]);

        // Assert
        _handler.RecordedUris.Should().HaveCount(2);
    }

    [Fact]
    public async Task SummarizeAsync_UpstreamError_IsTransientAndNotCached()
    {
        _handler.Default("{}", HttpStatusCode.InternalServerError);

        SummaryResult result = await Create().SummarizeAsync("Show", ["Great"], "en");

        result.Text.Should().BeNull();
        result.IsTransientFailure.Should().BeTrue();
        _cache.Should().BeEmpty();
    }

    [Fact]
    public async Task SummarizeAsync_EmptyAnswer_IsTransientAndNotCached()
    {
        _handler.RespondTo("/api/chat", Reply("   "));

        SummaryResult result = await Create().SummarizeAsync("Show", ["Great"], "en");

        result.IsTransientFailure.Should().BeTrue();
        _cache.Should().BeEmpty();
    }

    [Fact]
    public async Task SummarizeAsync_NotConfigured_MakesNoRequest()
    {
        SummaryResult result = await Create(new OllamaOptions()).SummarizeAsync("Show", ["Great"], "en");

        result.Text.Should().BeNull();
        result.IsTransientFailure.Should().BeFalse();
        _handler.RecordedUris.Should().BeEmpty();
    }
}
