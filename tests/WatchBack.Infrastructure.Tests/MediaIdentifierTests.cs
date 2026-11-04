using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.Services;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class MediaIdentifierTests
{
    private readonly IMediaSearchProvider _search = Substitute.For<IMediaSearchProvider>();
    private readonly MemoryLookups _lookups = new();
    private WatchBackOptions _watchBack = new();

    public MediaIdentifierTests()
    {
        _search.IsConfigured.Returns(true);
    }

    private MediaIdentifier Identifier()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => _search);
        IOptionsMonitor<WatchBackOptions> monitor = Substitute.For<IOptionsMonitor<WatchBackOptions>>();
        monitor.CurrentValue.Returns(_ => _watchBack);
        return new MediaIdentifier(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _lookups, monitor, NullLogger<MediaIdentifier>.Instance);
    }

    private static MediaSearchResult Series(string id, string title, string years) =>
        new(id, title, years, "series", null);

    [Theory]
    [InlineData("1959–1964", 1960, true, true)]
    [InlineData("2019–2020", 1960, true, false)]
    [InlineData("2018–", 2026, true, true)]
    [InlineData("1987–1994", 1987, true, true)]
    [InlineData("1995", 1995, false, true)]
    [InlineData("1995", 1997, false, false)]
    [InlineData(null, 1995, false, false)]
    public void YearFits_TheShowsRunOrTheFilmsYear(string? span, int year, bool isSeries, bool fits)
    {
        MediaIdentifier.YearFits(span, year, isSeries).Should().Be(fits);
    }

    // Pluto's Twilight Zone, S1E14 (1960): the 1959 series, not the 2019 one that shares its title.
    [Fact]
    public async Task Episode_ByNumbers_PicksTheShowWhoseRunHoldsTheGuidesYear()
    {
        _search.SearchAsync("The Twilight Zone", Arg.Any<CancellationToken>()).Returns(
        [
            Series("tt2583620", "The Twilight Zone", "2019–2020"),
            Series("tt0052520", "The Twilight Zone", "1959–1964"),
            new MediaSearchResult("tt0086491", "Twilight Zone: The Movie", "1983", "movie", null)
        ]);
        _search.GetEpisodesAsync("tt0052520", 1, Arg.Any<CancellationToken>()).Returns(
        [
            new EpisodeInfo("tt0734655", "Third from the Sun", 14, 1, new DateTimeOffset(1960, 1, 8, 0, 0, 0, TimeSpan.Zero))
        ]);
        EpisodeContext airing = new("The Twilight Zone", null, "Third From the Sun", 1, 14) { LiveChannel = "Pluto TV" };

        MediaContext result = await Identifier().IdentifyAsync(airing, 1960);

        result.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0734655");
        result.ReleaseDate.Should().Be(new DateTimeOffset(1960, 1, 8, 0, 0, 0, TimeSpan.Zero));
        result.LiveChannel.Should().Be("Pluto TV");
    }

    [Fact]
    public async Task Episode_ByTitle_ScansSeasonsForItAndTakesItsNumbers()
    {
        _search.SearchAsync("Columbo", Arg.Any<CancellationToken>()).Returns([Series("tt1466074", "Columbo", "1971–2003")]);
        _search.GetSeasonsAsync("tt1466074", Arg.Any<CancellationToken>())
            .Returns([new SeasonInfo(1, 7), new SeasonInfo(2, 8), new SeasonInfo(7, 5)]);
        _search.GetEpisodesAsync("tt1466074", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _search.GetEpisodesAsync("tt1466074", 7, Arg.Any<CancellationToken>()).Returns(
        [
            new EpisodeInfo("tt0077256", "Murder Under Glass", 4, 7, new DateTimeOffset(1978, 1, 30, 0, 0, 0, TimeSpan.Zero))
        ]);

        MediaContext result = await Identifier().IdentifyAsync(
            new EpisodeContext("Columbo", null, "Murder Under Glass", 0, 0), null);

        EpisodeContext episode = result.Should().BeOfType<EpisodeContext>().Subject;
        (episode.SeasonNumber, episode.EpisodeNumber).Should().Be(((short)7, (short)4));
        episode.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0077256");
    }

    [Fact]
    public async Task Episode_ByTitle_ReadsAtMostTenSeasons()
    {
        _search.SearchAsync("Bonanza", Arg.Any<CancellationToken>()).Returns([Series("tt0052451", "Bonanza", "1959–1973")]);
        _search.GetSeasonsAsync("tt0052451", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 14).Select(n => new SeasonInfo(n, 30)).ToList());
        _search.GetEpisodesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        MediaContext result = await Identifier().IdentifyAsync(
            new EpisodeContext("Bonanza", null, "Not An Episode", 0, 0), null);

        await _search.ReceivedWithAnyArgs(MediaIdentifier.MaxSeasonsScanned).GetEpisodesAsync(default!, default, default);
        result.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0052451", "the show is known even if the episode isn't");
    }

    [Fact]
    public async Task Movie_ByTitleAndYear()
    {
        _search.SearchAsync("Grease 2", Arg.Any<CancellationToken>()).Returns(
        [
            new MediaSearchResult("tt0084021", "Grease 2", "1982", "movie", null),
            new MediaSearchResult("tt9999999", "Grease 2", "2030", "movie", null)
        ]);

        MediaContext result = await Identifier().IdentifyAsync(new MediaContext("Grease 2", null), 1982);

        result.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0084021");
    }

    // "Top Gear UK" isn't OMDb's "Top Gear": a near title is a different show as far as anyone can tell.
    [Fact]
    public async Task NoExactTitle_IsAMiss_RememberedForADay()
    {
        _search.SearchAsync("Top Gear UK", Arg.Any<CancellationToken>()).Returns([Series("tt1628033", "Top Gear", "2002–")]);
        EpisodeContext airing = new("Top Gear UK", null, "Episode 8", 0, 0);

        MediaContext first = await Identifier().IdentifyAsync(airing, null);
        MediaContext second = await Identifier().IdentifyAsync(airing, null);

        first.ExternalIds.Should().BeNull();
        second.Should().Be(airing);
        await _search.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _lookups.Lifetimes.Should().ContainSingle().Which.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task Found_IsRemembered_SoTheNextPollAsksNobody()
    {
        _search.SearchAsync("Grease 2", Arg.Any<CancellationToken>())
            .Returns([new MediaSearchResult("tt0084021", "Grease 2", "1982", "movie", null)]);
        MediaIdentifier identifier = Identifier();

        await identifier.IdentifyAsync(new MediaContext("Grease 2", null), 1982);
        MediaContext again = await Identifier().IdentifyAsync(new MediaContext("Grease 2", null), 1982);

        again.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0084021");
        await _search.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Adding an OMDb key later must take effect: with none, nothing is looked up or remembered.
    [Fact]
    public async Task WithoutAConfiguredSearchProvider_NothingIsRemembered()
    {
        _search.IsConfigured.Returns(false);

        MediaContext result = await Identifier().IdentifyAsync(new MediaContext("Grease 2", null), 1982);

        result.ExternalIds.Should().BeNull();
        _lookups.Lifetimes.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheSearchFails_ItIsNotRetriedOnEveryPoll()
    {
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<MediaSearchResult>>>(_ => throw new HttpRequestException("down"));

        MediaContext result = await Identifier().IdentifyAsync(new MediaContext("Grease 2", null), 1982);
        await Identifier().IdentifyAsync(new MediaContext("Grease 2", null), 1982);

        result.ExternalIds.Should().BeNull();
        await _search.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _lookups.Lifetimes.Should().ContainSingle().Which.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ConcurrentAskers_ShareOneLookup()
    {
        TaskCompletionSource<IReadOnlyList<MediaSearchResult>> gate = new();
        _search.SearchAsync("Grease 2", Arg.Any<CancellationToken>()).Returns(gate.Task);
        MediaIdentifier identifier = Identifier();

        Task<MediaContext> a = identifier.IdentifyAsync(new MediaContext("Grease 2", null), 1982);
        Task<MediaContext> b = identifier.IdentifyAsync(new MediaContext("Grease 2", null), 1982);
        gate.SetResult([new MediaSearchResult("tt0084021", "Grease 2", "1982", "movie", null)]);
        await Task.WhenAll(a, b);

        await _search.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await b).ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0084021");
    }

    [Fact]
    public async Task AlreadyIdentified_OrLive_IsLeftAlone()
    {
        MediaContext known = new("Interstellar", null,
            new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0816692" });
        LiveBroadcastContext news = new("NBC News Daily", "NBC News NOW");

        (await Identifier().IdentifyAsync(known, 2014)).Should().BeSameAs(known);
        (await Identifier().IdentifyAsync(news, null)).Should().BeSameAs(news);
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }

    // Turning OMDb off (to save its quota, say) must stop live TV from spending it too.
    [Fact]
    public async Task SearchProviderTurnedOffInSettings_IsNotAsked()
    {
        _search.ConfigSection.Returns("Omdb");
        _watchBack = new WatchBackOptions { DisabledProviders = "Reddit, Omdb" };

        MediaContext result = await Identifier().IdentifyAsync(new MediaContext("Grease 2", null), 1982);

        result.ExternalIds.Should().BeNull();
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
        _lookups.Lifetimes.Should().BeEmpty("turning it back on must take effect at once");
    }

    [Theory]
    [InlineData("Episode 8")]
    [InlineData("Episodio 44")]
    [InlineData("Part 2")]
    public async Task Episode_WithAGenericTitle_IsNotSearchedForSeasonBySeason(string generic)
    {
        _search.SearchAsync("Top Gear", Arg.Any<CancellationToken>()).Returns([Series("tt1628033", "Top Gear", "2002–")]);

        MediaContext result = await Identifier().IdentifyAsync(new EpisodeContext("Top Gear", null, generic, 0, 0), null);

        result.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt1628033");
        await _search.DidNotReceiveWithAnyArgs().GetSeasonsAsync(default!, default);
    }

    private sealed class MemoryLookups : IDurableLookupCache
    {
        private readonly Dictionary<string, string?> _values = new();
        public List<TimeSpan> Lifetimes { get; } = [];

        public Task<LookupAnswer?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out string? v) ? new LookupAnswer(v) : (LookupAnswer?)null);

        public Task SetAsync(string key, string kind, string? value, TimeSpan lifetime, CancellationToken ct = default)
        {
            _values[key] = value;
            Lifetimes.Add(lifetime);
            return Task.CompletedTask;
        }
    }
}
