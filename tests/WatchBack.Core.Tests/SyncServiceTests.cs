using FluentAssertions;

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
    private readonly IWatchStateProvider _watchStateProvider = Substitute.For<IWatchStateProvider>();
    private readonly ITimeMachineFilter _timeMachineFilter = Substitute.For<ITimeMachineFilter>();
    private readonly IPrefetchService _prefetchService = Substitute.For<IPrefetchService>();
    private readonly IOptionsSnapshot<WatchBackOptions> _options;

    public SyncServiceTests()
    {
        _watchStateProvider.Metadata.Returns(new WatchStateDataProviderMetadata("Jellyfin", "Test"));
        _options = new OptionsSnapshotStub<WatchBackOptions>(new WatchBackOptions { TimeMachineDays = 14, WatchProvider = "jellyfin" });
    }

    [Fact]
    public async Task SyncAsync_WithNoActivePlayback_ReturnsIdleStatus()
    {
        // Arrange
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns((MediaContext?)null);
        var thoughtProviders = new[] { Substitute.For<IThoughtProvider>() };
        var service = new SyncService(new[] { _watchStateProvider }, Array.Empty<IManualWatchStateProvider>(), thoughtProviders, _timeMachineFilter, _prefetchService, _options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncService>.Instance);

        // Act
        var result = await service.SyncAsync();

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
        var episode = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        var thoughtProvider = Substitute.For<IThoughtProvider>();
        var thought = new Thought(
            Id: "1",
            ParentId: null,
            Title: null,
            Content: "Great episode!",
            Url: null,
            Images: [],
            Author: "TestUser",
            Score: 10,
            CreatedAt: DateTimeOffset.UtcNow,
            Source: "TestSource",
            Replies: []);

        var thoughtResult = new ThoughtResult(
            Source: "TestSource",
            PostTitle: "Episode Discussion",
            PostUrl: "https://example.com",
            ImageUrl: null,
            Thoughts: [thought],
            NextPageToken: null);

        thoughtProvider.GetThoughtsAsync(Arg.Is(episode), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>()).Returns(thoughtResult);
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), episode.ReleaseDate, 14).Returns([thought]);

        var thoughtProviders = new[] { thoughtProvider };
        var service = new SyncService(new[] { _watchStateProvider }, Array.Empty<IManualWatchStateProvider>(), thoughtProviders, _timeMachineFilter, _prefetchService, _options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncService>.Instance);

        // Act
        var result = await service.SyncAsync();

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
        var episode = new EpisodeContext(
            Title: "Test",
            ReleaseDate: DateTimeOffset.UtcNow,
            EpisodeTitle: "Test",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        var provider1 = Substitute.For<IThoughtProvider>();
        var provider2 = Substitute.For<IThoughtProvider>();
        var provider3 = Substitute.For<IThoughtProvider>();

        provider1.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Provider1", null, null, null, [], null));
        provider2.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Provider2", null, null, null, [], null));
        provider3.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Provider3", null, null, null, [], null));

        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns([]);

        var service = new SyncService(new[] { _watchStateProvider }, Array.Empty<IManualWatchStateProvider>(), new[] { provider1, provider2, provider3 }, _timeMachineFilter, _prefetchService, _options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncService>.Instance);

        // Act
        await service.SyncAsync();

        // Assert - all providers should be called
        await provider1.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>());
        await provider2.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>());
        await provider3.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_FilterThoughtsUsingTimeMachine()
    {
        // Arrange
        var episode = new EpisodeContext(
            Title: "Test",
            ReleaseDate: new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Test",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        var allThoughts = new[]
        {
            new Thought("1", null, null, "Content", null, [], "Author", 0, DateTimeOffset.UtcNow, "Source", []),
            new Thought("2", null, null, "Content", null, [], "Author", 0, DateTimeOffset.UtcNow.AddDays(-20), "Source", []),
        };

        var thoughtProvider = Substitute.For<IThoughtProvider>();
        thoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Source", null, null, null, allThoughts, null));

        var filteredThoughts = new[] { allThoughts[0] }; // Only first thought after filtering
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), episode.ReleaseDate, 14)
            .Returns(filteredThoughts);

        var service = new SyncService(new[] { _watchStateProvider }, Array.Empty<IManualWatchStateProvider>(), new[] { thoughtProvider }, _timeMachineFilter, _prefetchService, _options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncService>.Instance);

        // Act
        var result = await service.SyncAsync();

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
        var episode = new EpisodeContext(
            Title: "Test",
            ReleaseDate: DateTimeOffset.UtcNow,
            EpisodeTitle: "Test",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        var thoughtProvider1 = Substitute.For<IThoughtProvider>();
        var thoughtProvider2 = Substitute.For<IThoughtProvider>();

        var result1 = new ThoughtResult("Reddit", "Episode Discussion", "https://reddit.com/...", null, [], null);
        var result2 = new ThoughtResult("Trakt", "Show Comments", "https://trakt.tv/...", null, [], null);

        thoughtProvider1.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>()).Returns(result1);
        thoughtProvider2.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>()).Returns(result2);

        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>()).Returns([]);

        var service = new SyncService(new[] { _watchStateProvider }, Array.Empty<IManualWatchStateProvider>(), new[] { thoughtProvider1, thoughtProvider2 }, _timeMachineFilter, _prefetchService, _options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncService>.Instance);

        // Act
        var syncResult = await service.SyncAsync();

        // Assert
        syncResult.SourceResults.Should().HaveCount(2);
        syncResult.SourceResults.Should().Contain(r => r.Source == "Reddit");
        syncResult.SourceResults.Should().Contain(r => r.Source == "Trakt");
    }

    [Fact]
    public async Task SyncAsync_WithNoProviders_ReturnsErrorStatus()
    {
        // Arrange
        var service = new SyncService(
            Array.Empty<IWatchStateProvider>(),
            Array.Empty<IManualWatchStateProvider>(),
            Array.Empty<IThoughtProvider>(),
            _timeMachineFilter,
            _prefetchService,
            _options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncService>.Instance);

        // Act
        var result = await service.SyncAsync();

        // Assert
        result.Status.Should().Be(SyncStatus.Error);
    }

    [Fact]
    public async Task SyncAsync_SortsThoughtsByCreatedAtDescending()
    {
        // Arrange
        var episode = new EpisodeContext(
            Title: "Test",
            ReleaseDate: DateTimeOffset.UtcNow,
            EpisodeTitle: "Test",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        var now = DateTimeOffset.UtcNow;
        var thoughts = new[]
        {
            new Thought("1", null, null, "Content", null, [], "Author", 0, now.AddHours(-1), "Source", []),
            new Thought("2", null, null, "Content", null, [], "Author", 0, now, "Source", []),
            new Thought("3", null, null, "Content", null, [], "Author", 0, now.AddHours(-2), "Source", []),
        };

        var thoughtProvider = Substitute.For<IThoughtProvider>();
        thoughtProvider.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("Source", null, null, null, thoughts, null));

        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>())
            .Returns(thoughts);

        var service = new SyncService(new[] { _watchStateProvider }, Array.Empty<IManualWatchStateProvider>(), new[] { thoughtProvider }, _timeMachineFilter, _prefetchService, _options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncService>.Instance);

        // Act
        var result = await service.SyncAsync();

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
        var episode = new EpisodeContext("Test", DateTimeOffset.UtcNow, "Ep", 1, 1);
        _watchStateProvider.GetCurrentMediaContextAsync(Arg.Any<CancellationToken>()).Returns(episode);

        var provider1 = Substitute.For<IThoughtProvider>();
        var provider2 = Substitute.For<IThoughtProvider>();
        provider1.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("P1", null, null, null, [], null));
        provider2.GetThoughtsAsync(Arg.Any<MediaContext>(), Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<CancellationToken>())
            .Returns(new ThoughtResult("P2", null, null, null, [], null));
        _timeMachineFilter.Apply(Arg.Any<IEnumerable<Thought>>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>()).Returns([]);

        var capturedProgress = Substitute.For<IProgress<SyncProgressTick>>();
        var service = new SyncService(new[] { _watchStateProvider }, Array.Empty<IManualWatchStateProvider>(), new[] { provider1, provider2 }, _timeMachineFilter, _prefetchService, _options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncService>.Instance);

        // Act
        await service.SyncAsync(capturedProgress);

        // Assert — both providers must receive the same progress instance
        await provider1.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), capturedProgress, Arg.Any<CancellationToken>());
        await provider2.Received(1).GetThoughtsAsync(Arg.Any<MediaContext>(), capturedProgress, Arg.Any<CancellationToken>());
    }
}