using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Services;

using Xunit;

namespace WatchBack.Core.Tests;

public sealed class SummaryModesTests
{
    [Theory]
    [InlineData(null, true, "collapsed")]
    [InlineData("", true, "collapsed")]
    [InlineData(null, false, "hidden")]
    [InlineData("  ", false, "hidden")]
    [InlineData("open", false, "open")]
    [InlineData("OPEN", true, "open")]
    [InlineData("hidden", true, "hidden")]
    [InlineData("collapsed", false, "collapsed")]
    [InlineData("nonsense", true, "collapsed")]
    public void Resolve_UsesTheSavedModeElseDependsOnWhetherAModelIsSetUp(string? saved, bool configured, string expected)
    {
        SummaryModes.Resolve(saved, configured).Should().Be(expected);
    }

    [Theory]
    [InlineData("hidden", true)]
    [InlineData("Collapsed", true)]
    [InlineData("open", true)]
    [InlineData("", false)]
    [InlineData("always", false)]
    [InlineData(null, false)]
    public void IsValid_AcceptsOnlyTheThreeModes(string? value, bool expected)
    {
        SummaryModes.IsValid(value).Should().Be(expected);
    }
}

public sealed class ThoughtSummaryServiceTests
{
    private static Thought MakeThought(string content, int? score = null, int ageMinutes = 0, params Thought[] replies) =>
        new(Guid.NewGuid().ToString(), null, null, content, null, [], "a", score,
            DateTimeOffset.UtcNow.AddMinutes(-ageMinutes), "Src", replies);

    private static ISummarizer Summarizer(bool configured = true)
    {
        ISummarizer s = Substitute.For<ISummarizer>();
        s.IsConfigured.Returns(configured);
        return s;
    }

    private static ThoughtSummaryService Service(ISummarizer summarizer, TimeSpan? budget = null) =>
        new(() => summarizer, NullLogger<ThoughtSummaryService>.Instance, budget: budget);

    [Fact]
    public async Task SummarizeAsync_NoModelSetUp_IsUnavailableWithoutCallingIt()
    {
        ISummarizer summarizer = Summarizer(false);

        ThoughtSummary result = await Service(summarizer).SummarizeAsync("Show", [MakeThought("Great")], "en");

        result.Status.Should().Be(SummaryStatus.Unavailable);
        await summarizer.DidNotReceiveWithAnyArgs().SummarizeAsync(default!, default!, default!);
    }

    [Fact]
    public async Task SummarizeAsync_NoComments_IsEmpty()
    {
        ISummarizer summarizer = Summarizer();

        ThoughtSummary result = await Service(summarizer).SummarizeAsync("Show", [MakeThought("  ")], "en");

        result.Status.Should().Be(SummaryStatus.Empty);
        await summarizer.DidNotReceiveWithAnyArgs().SummarizeAsync(default!, default!, default!);
    }

    [Fact]
    public async Task SummarizeAsync_ReturnsTheSummaryAndCommentCount()
    {
        ISummarizer summarizer = Summarizer();
        summarizer.SummarizeAsync("Show", Arg.Any<IReadOnlyList<string>>(), "es", Arg.Any<CancellationToken>())
            .Returns(new SummaryResult("Gustó mucho."));

        ThoughtSummary result = await Service(summarizer)
            .SummarizeAsync("Show", [MakeThought("A"), MakeThought("B", replies: MakeThought("C"))], "es");

        result.Should().Be(new ThoughtSummary(SummaryStatus.Ready, "Gustó mucho.", 3));
    }

    [Fact]
    public async Task SummarizeAsync_ModelTooSlow_IsPendingThenReadyOnTheNextAsk()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISummarizer summarizer = Summarizer();
        summarizer.SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await gate.Task;
            return new SummaryResult("Done.");
        });
        ThoughtSummaryService service = Service(summarizer, TimeSpan.FromMilliseconds(50));
        Thought[] thoughts = [MakeThought("A")];

        ThoughtSummary first = await service.SummarizeAsync("Show", thoughts, "en");
        ThoughtSummary joined = await service.SummarizeAsync("Show", thoughts, "en");
        gate.SetResult();
        await Task.Delay(100);
        ThoughtSummary later = await service.SummarizeAsync("Show", thoughts, "en");

        first.Status.Should().Be(SummaryStatus.Pending);
        joined.Status.Should().Be(SummaryStatus.Pending);
        await summarizer.Received(2).SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>()); // the third ask finds nothing in flight and goes to the (cached) summarizer
        later.Status.Should().Be(SummaryStatus.Ready);
    }

    [Fact]
    public async Task SummarizeAsync_WhileOneIsBeingWritten_JoinsItInsteadOfAskingAgain()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISummarizer summarizer = Summarizer();
        summarizer.SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await gate.Task;
            return new SummaryResult("Done.");
        });
        ThoughtSummaryService service = Service(summarizer, TimeSpan.FromMilliseconds(50));
        Thought[] thoughts = [MakeThought("A")];

        await service.SummarizeAsync("Show", thoughts, "en");
        await service.SummarizeAsync("Show", thoughts, "en");
        gate.SetResult();

        await summarizer.Received(1).SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SummarizeAsync_ModelFails_IsFailedAndNotAskedAgainStraightAway()
    {
        ISummarizer summarizer = Summarizer();
        summarizer.SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns(new SummaryResult(null, true));
        ThoughtSummaryService service = Service(summarizer);
        Thought[] thoughts = [MakeThought("A")];

        ThoughtSummary first = await service.SummarizeAsync("Show", thoughts, "en");
        ThoughtSummary second = await service.SummarizeAsync("Show", thoughts, "en");

        first.Status.Should().Be(SummaryStatus.Failed);
        second.Status.Should().Be(SummaryStatus.Failed);
        await summarizer.Received(1).SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SummarizeAsync_ModelThrows_IsFailed()
    {
        ISummarizer summarizer = Summarizer();
        summarizer.SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns<SummaryResult>(_ => throw new InvalidOperationException("boom"));

        ThoughtSummary result = await Service(summarizer).SummarizeAsync("Show", [MakeThought("A")], "en");

        result.Status.Should().Be(SummaryStatus.Failed);
    }

    [Fact]
    public void SelectComments_PutsLikedThenNewerFirst_AndCountsRepliesAsComments()
    {
        Thought[] thoughts =
        [
            MakeThought("old unliked", 0, 100),
            MakeThought("new unliked", 0, 1),
            MakeThought("liked", 50, 500, MakeThought("reply liked more", 80, 10))
        ];

        IReadOnlyList<string> chosen = ThoughtSummaryService.SelectComments(thoughts);

        chosen.Should().Equal("reply liked more", "liked", "new unliked", "old unliked");
    }

    [Fact]
    public void SelectComments_TrimsLongCommentsAndCapsTheCountAndTotalLength()
    {
        string huge = new('x', ThoughtSummaryService.MaxCommentLength * 3);
        Thought[] many = Enumerable.Range(0, 100).Select(i => MakeThought("c" + i)).ToArray();

        IReadOnlyList<string> trimmed = ThoughtSummaryService.SelectComments([MakeThought(huge)]);
        IReadOnlyList<string> capped = ThoughtSummaryService.SelectComments(many);
        IReadOnlyList<string> bounded = ThoughtSummaryService.SelectComments(
            Enumerable.Range(0, 100).Select(_ => MakeThought(huge)).ToArray());

        trimmed.Single().Length.Should().Be(ThoughtSummaryService.MaxCommentLength + 1);
        capped.Should().HaveCount(ThoughtSummaryService.MaxComments);
        bounded.Sum(c => c.Length).Should().BeLessThanOrEqualTo(ThoughtSummaryService.MaxTotalLength + bounded.Count);
    }
}
