using System.Net.Http;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Core.Services;

using Xunit;

namespace WatchBack.Core.Tests;

public class SyncServiceTests
{
    private readonly IOptionsSnapshot<WatchBackOptions> _options;
    private readonly IPrefetchService _prefetchService = Substitute.For<IPrefetchService>();
    private readonly ITimeMachineFilter _timeMachineFilter = Substitute.For<ITimeMachineFilter>();
    private readonly IWatchStateProvider _watchStateProvider = Substitute.For<IWatchStateProvider>();

    public SyncServiceTests()
    {
        _watchStateProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        _options = new OptionsSnapshotStub<WatchBackOptions>(new WatchBackOptions
        {
            TimeMachineDays = 14,
            WatchProvider = "jellyfin"
        });
    }

    [Fact]
    public async Task SyncAsync_WithNoActivePlayback_ReturnsIdleStatus()
    {
        // Arrange
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns((MediaContext?)null);
        IThoughtProvider[] thoughtProviders = [Substitute.For<IThoughtProvider>()];
        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            thoughtProviders, Array.Empty<IRatingsProvider>(), _timeMachineFilter, _prefetchService, _options,
            NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.Status.Should().Be(SyncStatus.Idle);
        result.Title.Should().BeNull();
        result.Metadata.Should().BeNull();
        result.AllThoughts.Should().BeEmpty();
        result.TimeMachineThoughts.Should().BeEmpty();
        result.SourceResults.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAsync_WithActiveEpisode_ReturnsWatchingStatus()
    {
        // Arrange
        EpisodeContext episode = new(
            "Breaking Bad",
            new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        IThoughtProvider? thoughtProvider = Substitute.For<IThoughtProvider>();
        Thought thought = new(
            "1",
            null,
            null,
            "Great episode!",
            null,
            [],
            "TestUser",
            10,
            DateTimeOffset.UtcNow,
            "TestSource",
            []);

        ThoughtResult thoughtResult = new(
            "TestSource",
            "Episode Discussion",
            "https://example.com",
            null,
            [thought],
            null);

        thoughtProvider
            .GetThoughtsAsync(Arg.Is(episode), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(thoughtResult);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), episode.ReleaseDate, 14).Returns([thought]);

        IThoughtProvider[] thoughtProviders = [thoughtProvider];
        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            thoughtProviders, Array.Empty<IRatingsProvider>(), _timeMachineFilter, _prefetchService, _options,
            NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.Status.Should().Be(SyncStatus.Watching);
        result.Title.Should().Be("Breaking Bad");
        result.Metadata.Should().Be(episode);
        result.AllThoughts.Should().HaveCount(1);
        result.TimeMachineThoughts.Should().HaveCount(1);
        result.TimeMachineDays.Should().Be(14);
        result.SourceResults.Should().HaveCount(1);
        result.SourceResults[0].Source.Should().Be("TestSource");
    }

    [Fact]
    public async Task SyncAsync_CallsAllThoughtProvidersInParallel()
    {
        // Arrange
        EpisodeContext episode = new(
            "Test",
            DateTimeOffset.UtcNow,
            "Test",
            1,
            1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        IThoughtProvider? provider1 = Substitute.For<IThoughtProvider>();
        IThoughtProvider? provider2 = Substitute.For<IThoughtProvider>();
        IThoughtProvider? provider3 = Substitute.For<IThoughtProvider>();

        provider1.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Provider1", null, null, null, [], null));
        provider2.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Provider2", null, null, null, [], null));
        provider3.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Provider3", null, null, null, [], null));

        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [provider1, provider2, provider3], Array.Empty<IRatingsProvider>(), _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        await service.SyncAsync();

        // Assert - all providers should be called
        await provider1.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
            Arg.Any<CancellationToken>());
        await provider2.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
            Arg.Any<CancellationToken>());
        await provider3.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_FilterThoughtsUsingTimeMachine()
    {
        // Arrange
        EpisodeContext episode = new(
            "Test",
            new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
            "Test",
            1,
            1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        Thought[] allThoughts =
        [
            new("1", null, null, "Content", null, [], "Author", 0, DateTimeOffset.UtcNow, "Source", []),
            new("2", null, null, "Content", null, [], "Author", 0, DateTimeOffset.UtcNow.AddDays(-20),
                "Source", [])
        ];

        IThoughtProvider? thoughtProvider = Substitute.For<IThoughtProvider>();
        thoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Source", null, null, null, allThoughts, null));

        Thought[] filteredThoughts = [allThoughts[0]]; // Only first thought after filtering
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), episode.ReleaseDate, 14)
            .Returns(filteredThoughts);

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [thoughtProvider], Array.Empty<IRatingsProvider>(), _timeMachineFilter, _prefetchService, _options,
            NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.AllThoughts.Should().HaveCount(2);
        result.TimeMachineThoughts.Should().HaveCount(1);
        _timeMachineFilter.Received(1).Apply(
            Arg.Is<IEnumerable<Thought>>(t => t.Count() == 2),
            episode.ReleaseDate,
            14);
    }

    [Fact]
    public async Task SyncAsync_IncludesSourceResultsFromAllProviders()
    {
        // Arrange
        EpisodeContext episode = new(
            "Test",
            DateTimeOffset.UtcNow,
            "Test",
            1,
            1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        IThoughtProvider? thoughtProvider1 = Substitute.For<IThoughtProvider>();
        IThoughtProvider? thoughtProvider2 = Substitute.For<IThoughtProvider>();

        ThoughtResult result1 = new("Reddit", "Episode Discussion", "https://reddit.com/...", null, [], null);
        ThoughtResult result2 = new("Trakt", "Show Comments", "https://trakt.tv/...", null, [], null);

        thoughtProvider1.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
            Arg.Any<CancellationToken>()).Returns(result1);
        thoughtProvider2.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
            Arg.Any<CancellationToken>()).Returns(result2);

        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [thoughtProvider1, thoughtProvider2], Array.Empty<IRatingsProvider>(), _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        SyncResult syncResult = await service.SyncAsync();

        // Assert
        syncResult.SourceResults.Should().HaveCount(2);
        syncResult.SourceResults.Should().Contain(r => r.Source == "Reddit");
        syncResult.SourceResults.Should().Contain(r => r.Source == "Trakt");
    }

    [Fact]
    public async Task SyncAsync_WithNoProviders_ReturnsErrorStatus()
    {
        // Arrange
        SyncService service = new(
            Array.Empty<IWatchStateProvider>(),
            Array.Empty<IManualWatchStateProvider>(),
            Array.Empty<IThoughtProvider>(),
            Array.Empty<IRatingsProvider>(),
            _timeMachineFilter,
            _prefetchService,
            _options,
            NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.Status.Should().Be(SyncStatus.Error);
    }

    [Fact]
    public async Task SyncAsync_SortsThoughtsByCreatedAtDescending()
    {
        // Arrange
        EpisodeContext episode = new(
            "Test",
            DateTimeOffset.UtcNow,
            "Test",
            1,
            1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Thought[] thoughts =
        [
            new("1", null, null, "Content", null, [], "Author", 0, now.AddHours(-1), "Source", []),
            new("2", null, null, "Content", null, [], "Author", 0, now, "Source", []),
            new("3", null, null, "Content", null, [], "Author", 0, now.AddHours(-2), "Source", [])
        ];

        IThoughtProvider? thoughtProvider = Substitute.For<IThoughtProvider>();
        thoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Source", null, null, null, thoughts, null));

        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns(thoughts);

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [thoughtProvider], Array.Empty<IRatingsProvider>(), _timeMachineFilter, _prefetchService, _options,
            NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.AllThoughts.Should().HaveCount(3);
        result.AllThoughts[0].Id.Should().Be("2"); // Most recent
        result.AllThoughts[1].Id.Should().Be("1");
        result.AllThoughts[2].Id.Should().Be("3"); // Oldest
    }

    [Fact]
    public async Task SyncAsync_ForwardsProgressToAllProviders()
    {
        // Arrange
        EpisodeContext episode = new("Test", DateTimeOffset.UtcNow, "Ep", 1, 1);
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        IThoughtProvider? provider1 = Substitute.For<IThoughtProvider>();
        IThoughtProvider? provider2 = Substitute.For<IThoughtProvider>();
        provider1.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("P1", null, null, null, [], null));
        provider2.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("P2", null, null, null, [], null));
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        IProgress<SyncProgressTick>? capturedProgress = Substitute.For<IProgress<SyncProgressTick>>();
        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [provider1, provider2], Array.Empty<IRatingsProvider>(), _timeMachineFilter, _prefetchService,
            _options, NullLogger<SyncService>.Instance);

        // Act
        await service.SyncAsync(capturedProgress);

        // Assert — both providers must receive the same progress instance
        await provider1.Received(1)
            .GetThoughtsAsync(Arg.Any<MediaContext>(), capturedProgress, Arg.Any<CancellationToken>());
        await provider2.Received(1)
            .GetThoughtsAsync(Arg.Any<MediaContext>(), capturedProgress, Arg.Any<CancellationToken>());
    }

    // ---- Manual provider priority / suppression (T1) ----

    [Fact]
    public async Task SyncAsync_WithActiveManualProvider_ReturnsManualContext()
    {
        // Arrange
        MediaContext manualContext = new("Manual Movie", DateTimeOffset.UtcNow);
        IManualWatchStateProvider manual = Substitute.For<IManualWatchStateProvider>();
        manual.Metadata.Returns(new WatchStateDataProviderMetadata("Manual", "Manual"));
        manual.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(manualContext);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns((MediaContext?)null);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        SyncService service = new([_watchStateProvider], [manual],
            Array.Empty<IThoughtProvider>(), Array.Empty<IRatingsProvider>(), _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.Status.Should().Be(SyncStatus.Watching);
        result.Title.Should().Be("Manual Movie");
        result.WatchProvider.Should().Be("Manual");
    }

    [Fact]
    public async Task SyncAsync_WithBothManualAndConfiguredActive_PopulatesSuppressedFields()
    {
        // Arrange
        MediaContext manualContext = new("Manual Movie", DateTimeOffset.UtcNow);
        MediaContext configuredContext = new("Jellyfin Show", DateTimeOffset.UtcNow);

        IManualWatchStateProvider manual = Substitute.For<IManualWatchStateProvider>();
        manual.Metadata.Returns(new WatchStateDataProviderMetadata("Manual", "Manual"));
        manual.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(manualContext);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(configuredContext);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        SyncService service = new([_watchStateProvider], [manual],
            Array.Empty<IThoughtProvider>(), Array.Empty<IRatingsProvider>(), _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert — manual wins, but configured context is surfaced as suppressed
        result.Title.Should().Be("Manual Movie");
        result.SuppressedProvider.Should().Be("Jellyfin");
        result.SuppressedTitle.Should().Be("Jellyfin Show");
    }

    [Fact]
    public async Task SyncAsync_WithManualProviderReturningNull_FallsThroughToConfiguredProvider()
    {
        // Arrange
        MediaContext configuredContext = new("Jellyfin Show", DateTimeOffset.UtcNow);

        IManualWatchStateProvider manual = Substitute.For<IManualWatchStateProvider>();
        manual.Metadata.Returns(new WatchStateDataProviderMetadata("Manual", "Manual"));
        manual.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns((MediaContext?)null);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(configuredContext);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        SyncService service = new([_watchStateProvider], [manual],
            Array.Empty<IThoughtProvider>(), Array.Empty<IRatingsProvider>(), _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert — manual returned null, configured provider result is used
        result.Status.Should().Be(SyncStatus.Watching);
        result.Title.Should().Be("Jellyfin Show");
        result.SuppressedProvider.Should().BeNull();
    }

    // ---- Ratings provider integration (T2) ----

    [Fact]
    public async Task SyncAsync_WithImdbIdAndRatingsProvider_ReturnRatings()
    {
        // Arrange
        EpisodeContext episode = new(
            "Breaking Bad",
            new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1,
            ExternalIds: new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0903747" });

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        MediaRating rating = new("IMDb", "9.5");
        IRatingsProvider ratingsProvider = Substitute.For<IRatingsProvider>();
        ratingsProvider.Metadata.Returns(new DataProviderMetadata("OMDb", "OMDb ratings"));
        ratingsProvider.GetRatingsAsync("tt0903747", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MediaRating>>([rating]));

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            Array.Empty<IThoughtProvider>(), [ratingsProvider], _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.Ratings.Should().NotBeNull();
        result.Ratings.Should().ContainSingle(r => r.Source == "IMDb" && r.Value == "9.5");
        result.RatingsProvider.Should().Be("OMDb");
    }

    [Fact]
    public async Task SyncAsync_WithNoExternalIds_DoesNotCallRatingsProvider()
    {
        // Arrange
        EpisodeContext episode = new(
            "Breaking Bad",
            DateTimeOffset.UtcNow,
            "Pilot",
            1,
            1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        IRatingsProvider ratingsProvider = Substitute.For<IRatingsProvider>();
        ratingsProvider.Metadata.Returns(new DataProviderMetadata("OMDb", "OMDb ratings"));

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            Array.Empty<IThoughtProvider>(), [ratingsProvider], _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        await service.SyncAsync();

        // Assert — no IMDB ID means ratings provider should never be called
        await ratingsProvider.DidNotReceive().GetRatingsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_WhenSecondRatingsProviderHasResults_AttributesToSecondProvider()
    {
        // Arrange
        EpisodeContext episode = new(
            "Breaking Bad",
            DateTimeOffset.UtcNow,
            "Pilot",
            1,
            1,
            ExternalIds: new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0903747" });

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        IRatingsProvider emptyProvider = Substitute.For<IRatingsProvider>();
        emptyProvider.Metadata.Returns(new DataProviderMetadata("Empty", "No ratings"));
        emptyProvider.GetRatingsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MediaRating>>([]));

        IRatingsProvider secondProvider = Substitute.For<IRatingsProvider>();
        secondProvider.Metadata.Returns(new DataProviderMetadata("OMDb", "OMDb ratings"));
        secondProvider.GetRatingsAsync("tt0903747", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MediaRating>>([new MediaRating("IMDb", "9.5")]));

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            Array.Empty<IThoughtProvider>(), [emptyProvider, secondProvider], _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert — attribution should be the first provider that returned results
        result.RatingsProvider.Should().Be("OMDb");
    }

    // ---- Throwing thought provider → Error result (T7) ----

    [Fact]
    public async Task SyncAsync_WhenThoughtProviderThrows_ReturnsErrorStatus()
    {
        // Arrange
        EpisodeContext episode = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        IThoughtProvider throwingProvider = Substitute.For<IThoughtProvider>();
        throwingProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(),
                Arg.Any<CancellationToken>())
            .Returns<ThoughtResult?>(_ => throw new HttpRequestException("Connection refused"));

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [throwingProvider], Array.Empty<IRatingsProvider>(), _timeMachineFilter,
            _prefetchService, _options, NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.Status.Should().Be(SyncStatus.Error);
    }

    // ---- Content normalization (NormalizeContent) ----

    [Fact]
    public async Task SyncAsync_CallsNormalizeContentOnThoughtContent()
    {
        // Arrange
        EpisodeContext episode = new("Test", DateTimeOffset.UtcNow, "Ep", 1, 1);
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        Thought rawThought = new("1", null, null, ">!spoiler!<", null, [], "Author", 0, DateTimeOffset.UtcNow, "Test", []);
        IThoughtProvider provider = Substitute.For<IThoughtProvider>();
        provider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Test", null, null, null, [rawThought], null));
        provider.NormalizeContent(">!spoiler!<").Returns("<spoiler>spoiler</spoiler>");
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>()).Returns([]);

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [provider], Array.Empty<IRatingsProvider>(), _timeMachineFilter, _prefetchService, _options,
            NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.SourceResults[0].Thoughts!.Should().ContainSingle(t => t.Content == "<spoiler>spoiler</spoiler>");
    }

    [Fact]
    public async Task SyncAsync_CallsNormalizeContentOnPostBody()
    {
        // Arrange
        EpisodeContext episode = new("Test", DateTimeOffset.UtcNow, "Ep", 1, 1);
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        Thought rawThought = new("1", null, null, "content", null, [], "Author", 0, DateTimeOffset.UtcNow, "Test", [],
            PostBody: ">!body spoiler!<");
        IThoughtProvider provider = Substitute.For<IThoughtProvider>();
        provider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Test", null, null, null, [rawThought], null));
        provider.NormalizeContent("content").Returns("content");
        provider.NormalizeContent(">!body spoiler!<").Returns("<spoiler>body spoiler</spoiler>");
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>()).Returns([]);

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [provider], Array.Empty<IRatingsProvider>(), _timeMachineFilter, _prefetchService, _options,
            NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.SourceResults[0].Thoughts!.Should().ContainSingle(t => t.PostBody == "<spoiler>body spoiler</spoiler>");
    }

    [Fact]
    public async Task SyncAsync_DefaultNormalizeContent_LeavesContentUnchanged()
    {
        // Arrange
        EpisodeContext episode = new("Test", DateTimeOffset.UtcNow, "Ep", 1, 1);
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        Thought rawThought = new("1", null, null, "plain content", null, [], "Author", 0, DateTimeOffset.UtcNow, "Test", []);
        // NSubstitute's default for NormalizeContent (a default interface method) returns empty string,
        // so we explicitly configure it to return the input unchanged — matching the interface default.
        IThoughtProvider provider = Substitute.For<IThoughtProvider>();
        provider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Test", null, null, null, [rawThought], null));
        provider.NormalizeContent(Arg.Any<string>()).Returns(x => (string)x[0]);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>()).Returns([]);

        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [provider], Array.Empty<IRatingsProvider>(), _timeMachineFilter, _prefetchService, _options,
            NullLogger<SyncService>.Instance);

        // Act
        SyncResult result = await service.SyncAsync();

        // Assert
        result.SourceResults[0].Thoughts!.Should().ContainSingle(t => t.Content == "plain content");
    }

    [Fact]
    public async Task SyncAsync_DisabledProviders_SkipsTheirTasks()
    {
        // Arrange
        EpisodeContext episode = new(
            "The Bear",
            new DateTimeOffset(2022, 6, 23, 0, 0, 0, TimeSpan.Zero),
            "Brigade",
            1,
            3);
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        IThoughtProvider activeProvider = Substitute.For<IThoughtProvider>();
        activeProvider.ConfigSection.Returns("Reddit");
        activeProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Reddit", null, null, null, [], null));

        IThoughtProvider disabledProvider = Substitute.For<IThoughtProvider>();
        disabledProvider.ConfigSection.Returns("Lemmy");

        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        IOptionsSnapshot<WatchBackOptions> options = new OptionsSnapshotStub<WatchBackOptions>(new WatchBackOptions
        {
            TimeMachineDays = 14,
            WatchProvider = "jellyfin",
            DisabledProviders = "Lemmy"
        });
        SyncService service = new([_watchStateProvider], Array.Empty<IManualWatchStateProvider>(),
            [activeProvider, disabledProvider], Array.Empty<IRatingsProvider>(), _timeMachineFilter,
            _prefetchService, options, NullLogger<SyncService>.Instance);

        // Act
        await service.SyncAsync();

        // Assert
        await activeProvider.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>());
        await disabledProvider.DidNotReceive().GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>());
    }
}
