using System.Globalization;

using FluentAssertions;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Core.Tests;

public class ThoughtProviderQueryTests
{
    // ---- BuildTextQuery ----

    [Fact]
    public void BuildTextQuery_Movie_AppendsSuffix()
    {
        MediaContext movie = new("Interstellar", new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero));

        IThoughtProvider.BuildTextQuery(movie).Should().Be("Interstellar movie");
    }

    [Fact]
    public void BuildTextQuery_NormalEpisode_UsesDefaultEpisodeCode()
    {
        EpisodeContext episode = new("Breaking Bad", null, "Pilot", 1, 1);

        IThoughtProvider.BuildTextQuery(episode).Should().Be("Breaking Bad S01E01");
    }

    [Fact]
    public void BuildTextQuery_SeasonZeroEpisode_WithDate_UsesDate()
    {
        EpisodeContext episode = new(
            "The Daily Show",
            new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "January 20, 2026",
            0,
            1);

        IThoughtProvider.BuildTextQuery(episode).Should().Be("The Daily Show January 20, 2026");
    }

    [Fact]
    public void BuildTextQuery_SeasonZeroEpisode_NoDate_UsesEpisodeTitle()
    {
        EpisodeContext episode = new("The Daily Show", null, "Special Report", 0, 1);

        IThoughtProvider.BuildTextQuery(episode).Should().Be("The Daily Show Special Report");
    }

    [Fact]
    public void BuildTextQuery_SeasonZeroEpisode_NoDateNoTitle_ReturnsTitleOnly()
    {
        EpisodeContext episode = new("The Daily Show", null, "", 0, 1);

        IThoughtProvider.BuildTextQuery(episode).Should().Be("The Daily Show");
    }

    // ---- GetTextSearchQualifier ----

    [Fact]
    public void GetTextSearchQualifier_Movie_ReturnsMovieSuffix()
    {
        MediaContext movie = new("Interstellar", null);

        IThoughtProvider.GetTextSearchQualifier(movie).Should().Be("movie");
    }

    [Fact]
    public void GetTextSearchQualifier_NormalEpisode_ReturnsNull()
    {
        EpisodeContext episode = new("Breaking Bad", null, "Pilot", 2, 5);

        IThoughtProvider.GetTextSearchQualifier(episode).Should().BeNull();
    }

    [Fact]
    public void GetTextSearchQualifier_SeasonZeroWithDate_ReturnsFormattedDate()
    {
        EpisodeContext episode = new(
            "The Daily Show",
            new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Ep",
            0,
            5);

        IThoughtProvider.GetTextSearchQualifier(episode).Should().Be("January 20, 2026");
    }

    [Fact]
    public void GetTextSearchQualifier_SeasonZeroNoDate_ReturnsEpisodeTitle()
    {
        EpisodeContext episode = new("The Daily Show", null, "Special Report", 0, 1);

        IThoughtProvider.GetTextSearchQualifier(episode).Should().Be("Special Report");
    }

    [Fact]
    public void GetTextSearchQualifier_SeasonZeroNoDateNoTitle_ReturnsNull()
    {
        EpisodeContext episode = new("The Daily Show", null, "", 0, 1);

        IThoughtProvider.GetTextSearchQualifier(episode).Should().BeNull();
    }

    // ---- Live broadcasts ----

    [Fact]
    public void BuildTextQuery_LiveBroadcast_IsTheBareProgramName()
    {
        LiveBroadcastContext live = new("NBC News Daily", "NBC News NOW");

        IThoughtProvider.BuildTextQuery(live).Should().Be("NBC News Daily");
    }

    [Theory]
    [InlineData("NBC News Daily With Morgan Radford and Vicky Nguyen", "NBC News NOW",
        new[] { "NBC News Daily With Morgan Radford and Vicky Nguyen", "NBC News Daily", "NBC News NOW" })]
    [InlineData("Meet the Press w/ Kristen Welker", "NBC", new[] { "Meet the Press w/ Kristen Welker", "Meet the Press", "NBC" })]
    [InlineData("Dancing with the Stars", "ABC", new[] { "Dancing with the Stars", "ABC" })]
    [InlineData("Cooking with gas", "PBS", new[] { "Cooking with gas", "PBS" })]
    [InlineData("NBC News NOW", "NBC News NOW", new[] { "NBC News NOW" })]
    [InlineData("Live at Five", "", new[] { "Live at Five" })]
    public void LiveBroadcast_SearchNames_FullTitleThenWithoutHostsThenChannel(string title, string channel, string[] expected)
    {
        new LiveBroadcastContext(title, channel).SearchNames.Should().Equal(expected);
    }

    [Fact]
    public void LiveBroadcast_NextProgramOnTheSameChannel_IsADifferentContext()
    {
        DateTimeOffset six = new(2026, 11, 4, 18, 0, 0, TimeSpan.Zero);
        LiveBroadcastContext first = new("NBC News Daily", "NBC News NOW", six, six.AddHours(1));
        LiveBroadcastContext same = new("NBC News Daily", "NBC News NOW", six, six.AddHours(1));
        LiveBroadcastContext next = new("Top Story", "NBC News NOW", six.AddHours(1), six.AddHours(2));

        first.Should().Be(same);
        first.GetHashCode().Should().Be(same.GetHashCode());
        first.Should().NotBe(next);
        first.Should().NotBe(new MediaContext("NBC News Daily", null));
    }

    [Theory]
    [InlineData("UFC Main Event", "UFC Main Event: Oliveira vs Chandler 2", "Oliveira vs Chandler 2")]
    [InlineData("UCL Match Replay", "Feyenoord vs. Inter Milan", "Feyenoord vs. Inter Milan")]
    [InlineData("Premier League", "Arsenal v. Chelsea", "Arsenal v. Chelsea")]
    public void LiveBroadcast_SearchNames_StartWithTheMatch(string title, string subtitle, string match)
    {
        new LiveBroadcastContext(title, "Sports Channel", Subtitle: subtitle).SearchNames
            .Should().Equal(match, title, "Sports Channel");
    }

    [Theory]
    [InlineData("10/29/26")]
    [InlineData("Week 10: Magnificent Six")]
    [InlineData("Alamodome -- San Antonio, TX")]
    public void LiveBroadcast_SearchNames_IgnoreSubtitlesThatNameNoMatch(string subtitle)
    {
        new LiveBroadcastContext("Poker After Dark", "Sports Channel", Subtitle: subtitle).SearchNames
            .Should().Equal("Poker After Dark", "Sports Channel");
    }

    [Fact]
    public void LiveChannel_IsNotPartOfWhatAnEpisodeIs()
    {
        EpisodeContext library = new("Star Trek: The Next Generation", null, "The Naked Now", 1, 2);

        (library with { LiveChannel = "Trek Time" }).Should().Be(library);
    }
}
