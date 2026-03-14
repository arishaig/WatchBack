using FluentAssertions;
using Xunit;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Services;

namespace WatchBack.Core.Tests;

public class ReplyTreeBuilderTests
{
    private readonly IReplyTreeBuilder _builder = new ReplyTreeBuilder();

    private static Thought CreateThought(
        string id,
        string? parentId = null,
        string content = "Test")
    {
        return new Thought(
            Id: id,
            ParentId: parentId,
            Title: null,
            Content: content,
            Url: null,
            Images: [],
            Author: "Author",
            Score: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            Source: "Test",
            Replies: []);
    }

    [Fact]
    public void BuildTree_WithNoParentIds_AllTopLevel()
    {
        // Arrange
        var thoughts = new[]
        {
            CreateThought("1"),
            CreateThought("2"),
            CreateThought("3"),
        };

        // Act
        var result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(3);
        result.All(t => t.ParentId == null).Should().BeTrue();
        result.All(t => t.Replies.Count == 0).Should().BeTrue();
    }

    [Fact]
    public void BuildTree_WithOneLevel_CorrectlyNests()
    {
        // Arrange
        var thoughts = new[]
        {
            CreateThought("1"),                  // top-level
            CreateThought("2", parentId: "1"),  // reply to 1
            CreateThought("3", parentId: "1"),  // reply to 1
            CreateThought("4"),                  // top-level
        };

        // Act
        var result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(2);
        result[0].Replies.Should().HaveCount(2);
        result[0].Replies.Select(r => r.Id).Should().BeEquivalentTo(["2", "3"]);
        result[1].Replies.Should().BeEmpty();
    }

    [Fact]
    public void BuildTree_WithDeepNesting_MaintainsChain()
    {
        // Arrange
        var thoughts = new[]
        {
            CreateThought("1"),                  // A
            CreateThought("2", parentId: "1"),  // A -> B
            CreateThought("3", parentId: "2"),  // A -> B -> C
            CreateThought("4", parentId: "3"),  // A -> B -> C -> D
        };

        // Act
        var result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(1);
        result[0].Id.Should().Be("1");
        result[0].Replies.Should().HaveCount(1);
        result[0].Replies[0].Id.Should().Be("2");
        result[0].Replies[0].Replies.Should().HaveCount(1);
        result[0].Replies[0].Replies[0].Id.Should().Be("3");
        result[0].Replies[0].Replies[0].Replies.Should().HaveCount(1);
        result[0].Replies[0].Replies[0].Replies[0].Id.Should().Be("4");
    }

    [Fact]
    public void BuildTree_WithOrphanedParent_SurfacesAsTopLevel()
    {
        // Arrange
        var thoughts = new[]
        {
            CreateThought("1"),
            CreateThought("2", parentId: "999"), // parent doesn't exist
            CreateThought("3", parentId: "1"),
        };

        // Act
        var result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(2);
        var ids = result.Select(t => t.Id).ToList();
        ids.Should().Contain("1");
        ids.Should().Contain("2");
        result.Single(t => t.Id == "1").Replies.Should().ContainSingle(r => r.Id == "3");
    }

    [Fact]
    public void BuildTree_WithEmptyInput_ReturnsEmpty()
    {
        // Act
        var result = _builder.BuildTree([]);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildTree_WithMultipleBranches_BuildsCorrectStructure()
    {
        // Arrange
        var thoughts = new[]
        {
            CreateThought("1"),                  // root A
            CreateThought("2", parentId: "1"),  // A -> B
            CreateThought("3", parentId: "2"),  // A -> B -> C
            CreateThought("4"),                  // root D
            CreateThought("5", parentId: "4"),  // D -> E
            CreateThought("6", parentId: "5"),  // D -> E -> F
        };

        // Act
        var result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(2);

        var first = result[0];
        first.Id.Should().Be("1");
        first.Replies.Should().HaveCount(1);
        first.Replies[0].Id.Should().Be("2");
        first.Replies[0].Replies.Should().HaveCount(1);
        first.Replies[0].Replies[0].Id.Should().Be("3");

        var second = result[1];
        second.Id.Should().Be("4");
        second.Replies.Should().HaveCount(1);
        second.Replies[0].Id.Should().Be("5");
        second.Replies[0].Replies.Should().HaveCount(1);
        second.Replies[0].Replies[0].Id.Should().Be("6");
    }

    [Fact]
    public void BuildTree_DoesNotModifyInputThoughts()
    {
        // Arrange
        var original = new[]
        {
            CreateThought("1"),
            CreateThought("2", parentId: "1"),
        };

        // Act
        var result = _builder.BuildTree(original);

        // Assert
        // Input thoughts should still have empty replies (they're immutable records)
        original[0].Replies.Should().BeEmpty();
        original[1].Replies.Should().BeEmpty();
    }
}
