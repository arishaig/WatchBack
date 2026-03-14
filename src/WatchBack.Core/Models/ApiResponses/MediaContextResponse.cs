namespace WatchBack.Core.Models.ApiResponses;

/// <summary>
/// Used to search for Thoughts about this media
/// from any configured ThoughtProviders
/// </summary>
/// <param name="Title">The name of the movie or other media</param>
/// <param name="ReleaseDate">The date the media was released or premiered</param>
public record MediaContextResponse(
    string Title,
    DateTimeOffset? ReleaseDate);

/// <summary>
/// Used to search for Thoughts about this episode
/// </summary>
/// <param name="Title">The title of the TV show</param>
/// <param name="ReleaseDate">The date the episode aired</param>
/// <param name="EpisodeTitle">The title of the episode</param>
/// <param name="SeasonNumber">The season number of the episode</param>
/// <param name="EpisodeNumber">The episode number within the season</param>
public record EpisodeContextResponse(
    string Title,
    DateTimeOffset? ReleaseDate,
    string EpisodeTitle,
    short SeasonNumber,
    short EpisodeNumber) : MediaContextResponse(Title, ReleaseDate);
