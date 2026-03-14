namespace WatchBack.Core.Models;

public record MediaContext(
    string  Title,
    DateTime ReleaseDate
);

public record EpisodeContext(
    string Title,
    DateTime ReleaseDate,
    string EpisodeTitle,
    short SeasonNumber,
    short EpisodeNumber) : MediaContext(Title, ReleaseDate);