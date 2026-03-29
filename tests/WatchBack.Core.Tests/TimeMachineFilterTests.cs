using FluentAssertions;

using WatchBack.Core.Models;
using WatchBack.Core.Services;

using Xunit;

namespace WatchBack.Core.Tests;

public class TimeMachineFilterTests
{
    private readonly TimeMachineFilter _filter = new();

    private static Thought CreateThought(
        string id,
        DateTimeOffset createdAt,
        string source = "Test",
        string content = "Test")
    {
        return new Thought(
            Id: id,
            ParentId: null,
            Title: null,
            Content: content,
            Url: null,
            Images: [],
            Author: "TestAuthor",
            Score: 0,
            CreatedAt: createdAt,
            Source: source,
            Replies: []);
    }

    [Fact]
    public void Apply_WithNullAirDate_ReturnsAllThoughts()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var thoughts = new[]
        {
            CreateThought("1", now.AddDays(-10)),
            CreateThought("2", now),
            CreateThought("3", now.AddDays(10))
        };

        // Act
        var result = _filter.Apply(thoughts, airDate: null, windowDays: 14);

        // Assert
        result.Should().HaveCount(3);
    }

    [Fact]
    public void Apply_IncludesThoughtsWithinWindow()
    {
        // Arrange
        var airDate = DateTimeOffset.UtcNow;
        var windowDays = 14;

        var thoughts = new[]
        {
            CreateThought("1", airDate),                    // on air date
            CreateThought("2", airDate.AddDays(5)),        // 5 days after
            CreateThought("3", airDate.AddDays(14)),       // 14 days after (edge)
        };

        // Act
        var result = _filter.Apply(thoughts, airDate, windowDays);

        // Assert
        result.Should().HaveCount(3);
    }

    [Fact]
    public void Apply_ExcludesThoughtsAfterWindow()
    {
        // Arrange
        var airDate = DateTimeOffset.UtcNow;
        var windowDays = 14;

        var thoughts = new[]
        {
            CreateThought("1", airDate),
            CreateThought("2", airDate.AddDays(14.01)), // just past window
        };

        // Act
        var result = _filter.Apply(thoughts, airDate, windowDays);

        // Assert
        result.Should().HaveCount(1);
        result[0].Id.Should().Be("1");
    }

    [Fact]
    public void Apply_ExcludesThoughtsBeforeAirDate()
    {
        // Arrange
        var airDate = DateTimeOffset.UtcNow;

        var thoughts = new[]
        {
            CreateThought("1", airDate.AddDays(-100)), // way before
            CreateThought("2", airDate),
        };

        // Act
        var result = _filter.Apply(thoughts, airDate, windowDays: 14);

        // Assert
        result.Should().HaveCount(1);
        result[0].Id.Should().Be("2");
    }

    [Fact]
    public void Apply_WithZeroWindowDays_KeepsOnlyDayOfAirDate()
    {
        // Arrange
        var airDate = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

        var thoughts = new[]
        {
            CreateThought("1", new DateTimeOffset(2024, 3, 14, 23, 59, 59, TimeSpan.Zero)), // before
            CreateThought("2", new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero)),   // same day start
            CreateThought("3", new DateTimeOffset(2024, 3, 15, 23, 59, 59, TimeSpan.Zero)), // same day end
            CreateThought("4", new DateTimeOffset(2024, 3, 16, 0, 0, 0, TimeSpan.Zero)),   // next day
        };

        // Act
        var result = _filter.Apply(thoughts, airDate, windowDays: 0);

        // Assert
        result.Should().HaveCount(2);
        result.Select(t => t.Id).Should().BeEquivalentTo(["2", "3"]);
    }

    [Fact]
    public void Apply_WithEmptyInput_ReturnsEmpty()
    {
        // Arrange
        var airDate = DateTimeOffset.UtcNow;

        // Act
        var result = _filter.Apply([], airDate, windowDays: 14);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Apply_PreservesOrder()
    {
        // Arrange
        var airDate = DateTimeOffset.UtcNow;
        var thoughts = new[]
        {
            CreateThought("1", airDate.AddDays(1)),
            CreateThought("2", airDate.AddDays(5)),
            CreateThought("3", airDate.AddDays(3)),
        };

        // Act
        var result = _filter.Apply(thoughts, airDate, windowDays: 14);

        // Assert
        result.Select(t => t.Id).Should().Equal(["1", "2", "3"]);
    }
}
