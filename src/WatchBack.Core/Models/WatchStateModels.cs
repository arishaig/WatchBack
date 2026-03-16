namespace WatchBack.Core.Models;

/// <summary>
/// The minimal context for a piece of media currently being watched.
/// Used by WatchBack to search for Thoughts about that media.
/// </summary>
/// <param name="Title">The title of the movie or show</param>
/// <param name="ReleaseDate">The release or air date of this specific piece of media, used by the Time Machine filter</param>
public record MediaContext(
    string Title,
    DateTimeOffset? ReleaseDate
);

/// <summary>
/// A more specific MediaContext for TV episodes, adding season and episode
/// information so providers can find discussions about a specific episode
/// rather than the show as a whole.
/// </summary>
/// <param name="Title">The title of the show</param>
/// <param name="ReleaseDate">The air date of this specific episode, used by the Time Machine filter</param>
/// <param name="EpisodeTitle">The title of the episode</param>
/// <param name="SeasonNumber">The season number</param>
/// <param name="EpisodeNumber">The episode number within the season</param>
public record EpisodeContext(
    string Title,
    DateTimeOffset? ReleaseDate,
    string EpisodeTitle,
    short SeasonNumber,
    short EpisodeNumber) : MediaContext(Title, ReleaseDate);