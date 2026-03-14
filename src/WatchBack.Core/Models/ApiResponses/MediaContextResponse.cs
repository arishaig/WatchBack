namespace WatchBack.Core.Models.ApiResponses;

public record MediaContextResponse(
    string Title,
    DateTimeOffset? ReleaseDate);

public record EpisodeContextResponse(
    string Title,
    DateTimeOffset? ReleaseDate,
    string EpisodeTitle,
    short SeasonNumber,
    short EpisodeNumber) : MediaContextResponse(Title, ReleaseDate);
