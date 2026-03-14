namespace WatchBack.Core.Models;

public record MediaContext(
    string Title,
    DateTimeOffset? ReleaseDate
);

public record EpisodeContext(
    string Title,
    DateTimeOffset? ReleaseDate,
    string EpisodeTitle,
    short SeasonNumber,
    short EpisodeNumber) : MediaContext(Title, ReleaseDate);