using FluentAssertions;

using WatchBack.Api;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class SentimentScorerTests
{
    private readonly SentimentScorer _scorer = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Score_ReturnsNull_ForBlankInput(string? input)
    {
        _scorer.Score(input).Should().BeNull();
    }

    [Fact]
    public void Score_ReturnsFloat_ForNonBlankInput()
    {
        float? result = _scorer.Score("This is a great movie!");

        result.Should().NotBeNull();
        result!.Value.Should().BeInRange(-1f, 1f);
    }

    [Fact]
    public void Score_PositiveText_ReturnsPositiveCompound()
    {
        float? result = _scorer.Score("Absolutely wonderful, love it!");

        result.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void Score_NegativeText_ReturnsNegativeCompound()
    {
        float? result = _scorer.Score("Terrible, horrible, awful film.");

        result.Should().BeLessThan(0f);
    }
}