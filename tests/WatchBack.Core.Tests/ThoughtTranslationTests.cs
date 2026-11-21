using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Core.Services;

using Xunit;

namespace WatchBack.Core.Tests;

public sealed class ThoughtTranslationServiceTests
{
    private static Thought MakeThought(string id, string content, params Thought[] replies) =>
        new(id, null, null, content, null, [], "author", 1, DateTimeOffset.UtcNow, "Src", replies);

    private static ThoughtResult MakeResult(params Thought[] thoughts) =>
        new("Src", null, null, null, thoughts, null);

    private static ThoughtTranslationService CreateService(ITranslator translator, TimeSpan? budget = null) =>
        new(() => translator, NullLogger<ThoughtTranslationService>.Instance, budget: budget);

    [Fact]
    public async Task TranslateAsync_TranslatesContentAndNestedReplies_AndKeepsTheOriginal()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync("Bonjour", "en", Arg.Any<CancellationToken>())
            .Returns(new TranslationResult("Hello", "fr", true));
        translator.TranslateAsync("Merci", "en", Arg.Any<CancellationToken>())
            .Returns(new TranslationResult("Thanks", "fr", true));
        using ThoughtTranslationService service = CreateService(translator);
        ThoughtResult input = MakeResult(MakeThought("1", "Bonjour", MakeThought("2", "Merci")));

        // Act
        IReadOnlyList<ThoughtResult> output = await service.TranslateAsync([input], "en");

        // Assert
        Thought top = output[0].Thoughts!.Single();
        top.Content.Should().Be("Hello");
        top.OriginalContent.Should().Be("Bonjour");
        top.TranslatedFrom.Should().Be("fr");
        top.Replies.Single().Content.Should().Be("Thanks");
        top.Replies.Single().TranslatedFrom.Should().Be("fr");
    }

    [Fact]
    public async Task TranslateAsync_DoesNotMutateTheInput()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationResult("Hello", "fr", true));
        using ThoughtTranslationService service = CreateService(translator);
        ThoughtResult input = MakeResult(MakeThought("1", "Bonjour"));

        // Act
        await service.TranslateAsync([input], "en");

        // Assert
        input.Thoughts!.Single().Content.Should().Be("Bonjour");
        input.Thoughts!.Single().TranslatedFrom.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_ThoughtAlreadyInTargetLanguage_GetsNoTag()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync("Hello", "en", Arg.Any<CancellationToken>())
            .Returns(new TranslationResult("Hello", "en", false));
        using ThoughtTranslationService service = CreateService(translator);

        // Act
        IReadOnlyList<ThoughtResult> output = await service.TranslateAsync([MakeResult(MakeThought("1", "Hello"))], "en");

        // Assert
        Thought thought = output[0].Thoughts!.Single();
        thought.TranslatedFrom.Should().BeNull();
        thought.OriginalContent.Should().BeNull();
        thought.Content.Should().Be("Hello");
    }

    [Fact]
    public async Task TranslateAsync_IdenticalTextsInOneBatch_AreTranslatedOnce()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TranslationResult("Hello", "fr", true));
        using ThoughtTranslationService service = CreateService(translator);

        // Act
        await service.TranslateAsync([MakeResult(MakeThought("1", "Bonjour"), MakeThought("2", "Bonjour"))], "en");

        // Assert
        await translator.Received(1).TranslateAsync("Bonjour", "en", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TranslateAsync_TranslatorTooSlow_ReturnsOriginalTextWithinBudget_ThenTranslatesLater()
    {
        // Arrange
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync("Bonjour", "en", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await gate.Task;
            return new TranslationResult("Hello", "fr", true);
        });
        using ThoughtTranslationService service = CreateService(translator, TimeSpan.FromMilliseconds(50));
        ThoughtResult input = MakeResult(MakeThought("1", "Bonjour"));

        // Act
        IReadOnlyList<ThoughtResult> first = await service.TranslateAsync([input], "en");
        gate.SetResult();
        IReadOnlyList<ThoughtResult> later = await service.TranslateAsync([input], "en");

        // Assert
        first[0].Thoughts!.Single().Content.Should().Be("Bonjour");
        first[0].Thoughts!.Single().TranslatedFrom.Should().BeNull();
        later[0].Thoughts!.Single().Content.Should().Be("Hello");
    }

    [Fact]
    public async Task TranslateAsync_PollWhileTranslationInFlight_JoinsItInsteadOfRequestingAgain()
    {
        // Arrange
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync("Bonjour", "en", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await gate.Task;
            return new TranslationResult("Hello", "fr", true);
        });
        using ThoughtTranslationService service = CreateService(translator, TimeSpan.FromMilliseconds(50));
        ThoughtResult input = MakeResult(MakeThought("1", "Bonjour"));

        // Act
        await service.TranslateAsync([input], "en");
        await service.TranslateAsync([input], "en");
        gate.SetResult();

        // Assert
        await translator.Received(1).TranslateAsync("Bonjour", "en", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TranslateAsync_TranslatorFailsTransiently_ReturnsOriginalAndBacksOff()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TranslationResult(ci.ArgAt<string>(0), null, false, true));
        using ThoughtTranslationService service = CreateService(translator);

        // Act
        IReadOnlyList<ThoughtResult> first = await service.TranslateAsync([MakeResult(MakeThought("1", "Bonjour"))], "en");
        await service.TranslateAsync([MakeResult(MakeThought("2", "Merci"))], "en");

        // Assert
        first[0].Thoughts!.Single().Content.Should().Be("Bonjour");
        await translator.Received(1).TranslateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TranslateAsync_TranslatorThrows_ReturnsOriginal()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        translator.TranslateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<TranslationResult>(_ => throw new InvalidOperationException("boom"));
        using ThoughtTranslationService service = CreateService(translator);

        // Act
        IReadOnlyList<ThoughtResult> output = await service.TranslateAsync([MakeResult(MakeThought("1", "Bonjour"))], "en");

        // Assert
        output[0].Thoughts!.Single().Content.Should().Be("Bonjour");
    }

    [Fact]
    public async Task TranslateAsync_EmptyTargetLanguage_ReturnsInputUntouched()
    {
        // Arrange
        ITranslator translator = Substitute.For<ITranslator>();
        using ThoughtTranslationService service = CreateService(translator);
        IReadOnlyList<ThoughtResult> input = [MakeResult(MakeThought("1", "Bonjour"))];

        // Act
        IReadOnlyList<ThoughtResult> output = await service.TranslateAsync(input, " ");

        // Assert
        output.Should().BeSameAs(input);
        await translator.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default!);
    }
}

public sealed class SyncServiceTranslationTests
{
    private readonly ITimeMachineFilter _timeMachineFilter = Substitute.For<ITimeMachineFilter>();
    private readonly IThoughtTranslationService _translation = Substitute.For<IThoughtTranslationService>();
    private readonly IWatchStateProvider _watchState = Substitute.For<IWatchStateProvider>();

    private static readonly EpisodeContext s_episode = new("Show", DateTimeOffset.UtcNow, "Pilot", 1, 1);

    private static readonly Thought s_original =
        new("1", null, null, "Bonjour", null, [], "a", 1, DateTimeOffset.UtcNow, "Src", []);

    private SyncService CreateService(WatchBackOptions options)
    {
        _watchState.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        _watchState.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(s_episode);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns(ci => ci.Arg<IEnumerable<Thought>>().ToList());

        IThoughtProvider provider = Substitute.For<IThoughtProvider>();
        provider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Src", null, null, null, [s_original], null));

        return new SyncService([_watchState], Array.Empty<IManualWatchStateProvider>(), [provider],
            Array.Empty<IRatingsProvider>(), _timeMachineFilter, Substitute.For<IPrefetchService>(),
            new PassThroughResultCache(), new OptionsSnapshotStub<WatchBackOptions>(options),
            NullLogger<SyncService>.Instance, _translation);
    }

    private void TranslationReturnsHello() =>
        _translation.TranslateAsync(Arg.Any<IReadOnlyList<ThoughtResult>>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<ThoughtResult>)
            [
                new ThoughtResult("Src", null, null, null,
                    [s_original with { Content = "Hello", OriginalContent = "Bonjour", TranslatedFrom = "fr" }], null)
            ]);

    [Fact]
    public async Task SyncAsync_AutoTranslateOn_UsesTranslatedThoughtsAndEmitsNoProgressTicks()
    {
        // Arrange
        TranslationReturnsHello();
        SyncService service = CreateService(new WatchBackOptions { AutoTranslate = true, TranslateTargetLanguage = "en" });
        List<SyncProgressTick> ticks = [];

        // Act
        SyncResult result = await service.SyncAsync(new SynchronousProgress(ticks));

        // Assert
        result.AllThoughts.Single().Content.Should().Be("Hello");
        result.AllThoughts.Single().TranslatedFrom.Should().Be("fr");
        result.SourceResults.Single().Thoughts!.Single().Content.Should().Be("Hello");
        await _translation.Received(1).TranslateAsync(Arg.Any<IReadOnlyList<ThoughtResult>>(), "en",
            Arg.Any<CancellationToken>());
        ticks.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAsync_AutoTranslateOff_LeavesThoughtsInTheirOriginalLanguage()
    {
        // Arrange
        SyncService service = CreateService(new WatchBackOptions { AutoTranslate = false, TranslateTargetLanguage = "en" });

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.AllThoughts.Single().Content.Should().Be("Bonjour");
        await _translation.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default!);
    }

    [Fact]
    public async Task SyncAsync_AutoTranslateOnButNoTargetLanguage_DoesNotTranslate()
    {
        // Arrange
        SyncService service = CreateService(new WatchBackOptions { AutoTranslate = true, TranslateTargetLanguage = "" });

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.AllThoughts.Single().Content.Should().Be("Bonjour");
        await _translation.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default!);
    }

    private sealed class SynchronousProgress(List<SyncProgressTick> sink) : IProgress<SyncProgressTick>
    {
        public void Report(SyncProgressTick value) => sink.Add(value);
    }
}
