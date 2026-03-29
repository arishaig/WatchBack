namespace WatchBack.Core.Models;

/// <summary>
/// The minimal context for a piece of media currently being watched.
/// Used by WatchBack to search for Thoughts about that media.
/// </summary>
/// <param name="Title">The title of the movie or show</param>
/// <param name="ReleaseDate">The release or air date of this specific piece of media, used by the Time Machine filter</param>
/// <param name="ExternalIds">
/// A dictionary of external IDs provided by the watch state source.
/// Keys are well-known type strings (see <see cref="ExternalIdType"/>), but providers may include
/// additional keys for IDs not covered by the standard set.
/// </param>
public record MediaContext(
    string Title,
    DateTimeOffset? ReleaseDate,
    IReadOnlyDictionary<string, string>? ExternalIds = null)
{
    public virtual bool Equals(MediaContext? other) =>
        other is not null &&
        EqualityContract == other.EqualityContract &&
        Title == other.Title &&
        ReleaseDate == other.ReleaseDate &&
        ExternalIdsEqual(ExternalIds, other.ExternalIds);

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(EqualityContract, Title, ReleaseDate);
        if (ExternalIds is not null)
        {
            foreach ((string k, string v) in ExternalIds.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
                hash = HashCode.Combine(hash, k, v);
        }
        return hash;
    }

    protected static bool ExternalIdsEqual(
        IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        foreach ((string k, string v) in a)
            if (!b.TryGetValue(k, out var bv) || v != bv) return false;
        return true;
    }
}

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
/// <param name="ExternalIds">
/// A dictionary of external IDs provided by the watch state source.
/// Keys are well-known type strings (see <see cref="ExternalIdType"/>), but providers may include
/// additional keys for IDs not covered by the standard set.
/// </param>
public record EpisodeContext(
    string Title,
    DateTimeOffset? ReleaseDate,
    string EpisodeTitle,
    short SeasonNumber,
    short EpisodeNumber,
    IReadOnlyDictionary<string, string>? ExternalIds = null
) : MediaContext(Title, ReleaseDate, ExternalIds)
{
    public virtual bool Equals(EpisodeContext? other) =>
        other is not null &&
        base.Equals(other) &&
        EpisodeTitle == other.EpisodeTitle &&
        SeasonNumber == other.SeasonNumber &&
        EpisodeNumber == other.EpisodeNumber;

    public override int GetHashCode() =>
        HashCode.Combine(base.GetHashCode(), EpisodeTitle, SeasonNumber, EpisodeNumber);
}
