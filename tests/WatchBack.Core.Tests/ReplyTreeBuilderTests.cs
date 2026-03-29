using FluentAssertions;

using WatchBack.Core.Models;
using WatchBack.Core.Services;

using Xunit;

namespace WatchBack.Core.Tests;

public class ReplyTreeBuilderTests
{
    private readonly ReplyTreeBuilder _builder = new();

    private static Thought CreateThought(
        string id,
        string? parentId = null,
        string content = "Test")
    {
        return new Thought(
            id,
            parentId,
            null,
            content,
            null,
            [],
            "Author",
            0,
            DateTimeOffset.UtcNow,
            "Test",
            []);
    }

    [Fact]
    public void BuildTree_WithNoParentIds_AllTopLevel()
    {
        // Arrange
        Thought[] thoughts = [CreateThought("1"), CreateThought("2"), CreateThought("3")];

        // Act
        IReadOnlyList<Thought> result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(3);
        result.All(t => t.ParentId == null).Should().BeTrue();
        result.All(t => t.Replies.Count == 0).Should().BeTrue();
    }

    [Fact]
    public void BuildTree_WithOneLevel_CorrectlyNests()
    {
        // Arrange
        Thought[] thoughts =
        [
            CreateThought("1"), // top-level
            CreateThought("2", "1"), // reply to 1
            CreateThought("3", "1"), // reply to 1
            CreateThought("4") // top-level
        ];

        // Act
        IReadOnlyList<Thought> result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(2);
        result[0].Replies.Should().HaveCount(2);
        result[0].Replies.Select(r => r.Id).Should().BeEquivalentTo("2", "3");
        result[1].Replies.Should().BeEmpty();
    }

    [Fact]
    public void BuildTree_WithDeepNesting_MaintainsChain()
    {
        // Arrange
        Thought[] thoughts =
        [
            CreateThought("1"), // A
            CreateThought("2", "1"), // A -> B
            CreateThought("3", "2"), // A -> B -> C
            CreateThought("4", "3") // A -> B -> C -> D
        ];

        // Act
        IReadOnlyList<Thought> result = _builder.BuildTree(thoughts);

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
        Thought[] thoughts =
        [
            CreateThought("1"), CreateThought("2", "999"), // parent doesn't exist
            CreateThought("3", "1")
        ];

        // Act
        IReadOnlyList<Thought> result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(2);
        List<string> ids = result.Select(t => t.Id).ToList();
        ids.Should().Contain("1");
        ids.Should().Contain("2");
        result.Single(t => t.Id == "1").Replies.Should().ContainSingle(r => r.Id == "3");
    }

    [Fact]
    public void BuildTree_WithEmptyInput_ReturnsEmpty()
    {
        // Act
        IReadOnlyList<Thought> result = _builder.BuildTree([]);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildTree_WithMultipleBranches_BuildsCorrectStructure()
    {
        // Arrange
        Thought[] thoughts =
        [
            CreateThought("1"), // root A
            CreateThought("2", "1"), // A -> B
            CreateThought("3", "2"), // A -> B -> C
            CreateThought("4"), // root D
            CreateThought("5", "4"), // D -> E
            CreateThought("6", "5") // D -> E -> F
        ];

        // Act
        IReadOnlyList<Thought> result = _builder.BuildTree(thoughts);

        // Assert
        result.Should().HaveCount(2);

        Thought first = result[0];
        first.Id.Should().Be("1");
        first.Replies.Should().HaveCount(1);
        first.Replies[0].Id.Should().Be("2");
        first.Replies[0].Replies.Should().HaveCount(1);
        first.Replies[0].Replies[0].Id.Should().Be("3");

        Thought second = result[1];
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
        Thought[] original = [CreateThought("1"), CreateThought("2", "1")];

        // Act
        _builder.BuildTree(original);

        // Assert
        // Input thoughts should still have empty replies (they're immutable records)
        original[0].Replies.Should().BeEmpty();
        original[1].Replies.Should().BeEmpty();
    }
}
