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
}
