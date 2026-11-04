namespace WatchBack.Core.Models;

/// <summary>
///     The minimal context for a piece of media currently being watched.
///     Used by WatchBack to search for Thoughts about that media.
/// </summary>
/// <param name="Title">The title of the movie or show</param>
/// <param name="ReleaseDate">The release or air date of this specific piece of media, used by the Time Machine filter</param>
/// <param name="ExternalIds">
///     A dictionary of external IDs provided by the watch state source.
///     Keys are well-known type strings (see <see cref="ExternalIdType" />), but providers may include
///     additional keys for IDs not covered by the standard set.
/// </param>
public record MediaContext(
    string Title,
    DateTimeOffset? ReleaseDate,
    IReadOnlyDictionary<string, string>? ExternalIds = null)
{
    public virtual bool Equals(MediaContext? other)
    {
        return other is not null &&
               EqualityContract == other.EqualityContract &&
               Title == other.Title &&
               ReleaseDate == other.ReleaseDate &&
               ExternalIdsEqual(ExternalIds, other.ExternalIds);
    }

    public override int GetHashCode()
    {
        int hash = HashCode.Combine(EqualityContract, Title, ReleaseDate);
        if (ExternalIds is not null)
        {
            foreach ((string k, string v) in ExternalIds.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                hash = HashCode.Combine(hash, k, v);
            }
        }

        return hash;
    }

    private static bool ExternalIdsEqual(
        IReadOnlyDictionary<string, string>? a,
        IReadOnlyDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        foreach ((string k, string v) in a)
        {
            if (!b.TryGetValue(k, out string? bv) || v != bv)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
///     A more specific MediaContext for TV episodes, adding season and episode
///     information so providers can find discussions about a specific episode
///     rather than the show as a whole.
/// </summary>
/// <param name="Title">The title of the show</param>
/// <param name="ReleaseDate">The air date of this specific episode, used by the Time Machine filter</param>
/// <param name="EpisodeTitle">The title of the episode</param>
/// <param name="SeasonNumber">The season number</param>
/// <param name="EpisodeNumber">The episode number within the season</param>
/// <param name="ExternalIds">
///     A dictionary of external IDs provided by the watch state source.
///     Keys are well-known type strings (see <see cref="ExternalIdType" />), but providers may include
///     additional keys for IDs not covered by the standard set.
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
    public virtual bool Equals(EpisodeContext? other)
    {
        return other is not null &&
               base.Equals(other) &&
               EpisodeTitle == other.EpisodeTitle &&
               SeasonNumber == other.SeasonNumber &&
               EpisodeNumber == other.EpisodeNumber;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(base.GetHashCode(), EpisodeTitle, SeasonNumber, EpisodeNumber);
    }
}

/// <summary>
///     A live broadcast that isn't a known episode or movie: news, sports, a talk show without
///     episode numbers. Discussion of it is happening now, so providers search recent posts by the
///     program's name and the channel's, and results are kept only briefly.
/// </summary>
/// <param name="Title">The program's name from the guide, or the channel's when the guide has none</param>
/// <param name="ChannelName">The channel it airs on</param>
/// <param name="StartsAt">When this airing began, if the guide says</param>
/// <param name="EndsAt">When this airing ends, if the guide says</param>
public record LiveBroadcastContext(
    string Title,
    string ChannelName,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null
) : MediaContext(Title, null)
{
    /// <summary>How long results for a live broadcast stay fresh: the conversation moves while it airs.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How far back posts about a live broadcast count as discussion of this airing.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(1);

    private static readonly string[] s_hostSeparators = [" with ", " w/ "];

    /// <summary>
    ///     The names to search, in order, for the first that finds posts: the program's name as the guide
    ///     gives it; then without the hosts guides append ("NBC News Daily With Morgan Radford" →
    ///     "NBC News Daily"), unless that would cut a title apart ("Dancing with the Stars"); then the channel's.
    /// </summary>
    public IReadOnlyList<string> SearchNames
    {
        get
        {
            List<string> names = [Title.Trim()];
            if (WithoutHosts(names[0]) is { } shorter)
            {
                names.Add(shorter);
            }

            if (!string.IsNullOrWhiteSpace(ChannelName))
            {
                names.Add(ChannelName.Trim());
            }

            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // "X With A and B": the hosts are named people, so what follows starts with a capital and isn't "the".
    private static string? WithoutHosts(string title)
    {
        foreach (string separator in s_hostSeparators)
        {
            int at = title.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (at <= 0)
            {
                continue;
            }

            string hosts = title[(at + separator.Length)..].TrimStart();
            if (hosts.Length > 0 && char.IsUpper(hosts[0]) &&
                !hosts.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            {
                return title[..at].Trim();
            }
        }

        return null;
    }

    public virtual bool Equals(LiveBroadcastContext? other)
    {
        return other is not null &&
               base.Equals(other) &&
               ChannelName == other.ChannelName &&
               StartsAt == other.StartsAt &&
               EndsAt == other.EndsAt;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(base.GetHashCode(), ChannelName, StartsAt, EndsAt);
    }
}
