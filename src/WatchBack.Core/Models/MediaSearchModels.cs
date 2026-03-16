namespace WatchBack.Core.Models;

/// <summary>
/// Represents a single result returned from a media search query.
/// </summary>
/// <param name="ImdbId">IMDB identifier for this title, used for season/episode drill-down.</param>
/// <param name="Title">Title of the movie or TV show.</param>
/// <param name="Year">Release year or range (e.g. "2008–2013" for series).</param>
/// <param name="Type">Content type: "movie", "series", or "episode".</param>
/// <param name="PosterUrl">URL to the poster image, or null if unavailable.</param>
/// <param name="ReleaseDate">Exact release or air date, or null if only a year is known.</param>
public record MediaSearchResult(
    string ImdbId,
    string Title,
    string? Year,
    string Type,
    string? PosterUrl,
    DateTimeOffset? ReleaseDate = null);

/// <summary>
/// Summary information about a single season of a TV series.
/// </summary>
/// <param name="SeasonNumber">Season number.</param>
/// <param name="EpisodeCount">Total number of episodes in the season.</param>
public record SeasonInfo(
    int SeasonNumber,
    int EpisodeCount);

/// <summary>
/// Information about a single episode of a TV series.
/// </summary>
/// <param name="ImdbId">IMDB identifier for this episode.</param>
/// <param name="Title">Episode title.</param>
/// <param name="EpisodeNumber">Episode number within the season.</param>
/// <param name="SeasonNumber">Season number this episode belongs to.</param>
/// <param name="AirDate">Air date of the episode, or null if unknown.</param>
public record EpisodeInfo(
    string? ImdbId,
    string Title,
    int EpisodeNumber,
    int SeasonNumber,
    DateTimeOffset? AirDate);
