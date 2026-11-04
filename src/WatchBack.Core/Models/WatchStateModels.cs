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
    /// <summary>
    ///     The channel this is airing on, when it is being watched as live TV (an episode or movie in a
    ///     channel's schedule, or a <see cref="LiveBroadcastContext" />). Not part of its identity: the same
    ///     episode is the same whether it airs live or plays from a library.
    /// </summary>
    public string? LiveChannel { get; init; }

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
/// <param name="Subtitle">The guide's episode title for this airing, e.g. the match ("Feyenoord vs. Inter Milan")</param>
/// <param name="IsMusic">Music programming (a music video channel, a radio stream): nothing anyone discusses by name</param>
public record LiveBroadcastContext(
    string Title,
    string ChannelName,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    string? Subtitle = null,
    bool IsMusic = false
) : MediaContext(Title, null)
{
    /// <summary>How long results for a live broadcast stay fresh: the conversation moves while it airs.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How far back posts about a live broadcast count as discussion of this airing.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(1);

    private static readonly string[] s_hostSeparators = [" with ", " w/ "];

    private static readonly string[] s_segmentSeparators = [": ", " -- ", " - ", " | "];

    /// <summary>
    ///     The names to search, in order, for the first that finds posts: the match, when the guide names
    ///     one ("2026 World Series - Rays vs. Brewers" → "Rays vs. Brewers"); the program's name as the guide
    ///     gives it; then without the hosts guides append ("NBC News Daily With Morgan Radford" →
    ///     "NBC News Daily"), when what's left is still distinctive; then the channel's. A single word ("Today",
    ///     "UFC") would match far more than this airing, so it is never searched, and music programming has
    ///     no names at all.
    /// </summary>
    public IReadOnlyList<string> SearchNames
    {
        get
        {
            if (IsMusic)
            {
                return [];
            }

            List<string> names = [];
            if (Matchup(Subtitle) is { } matchup)
            {
                names.Add(matchup);
            }

            names.Add(Title.Trim());
            if (WithoutHosts(Title) is { } shorter)
            {
                names.Add(shorter);
            }

            if (!string.IsNullOrWhiteSpace(ChannelName))
            {
                names.Add(ChannelName.Trim());
            }

            return names
                .Where(n => n.Contains(' ', StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // The part of the subtitle that names "A vs. B" or "A v. B", without the event, round or week around it.
    // Other subtitles ("Week 10", "10/29/26") name nothing people would search for.
    private static string? Matchup(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return null;
        }

        return subtitle
            .Split(s_segmentSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment => System.Text.RegularExpressions.Regex.IsMatch(segment, @"\s(vs\.?|v\.)\s",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
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

            // "The Herd With Colin Cowherd" → "The Herd" is too generic to mean the show.
            string hosts = title[(at + separator.Length)..].TrimStart();
            string program = title[..at].Trim();
            if (hosts.Length > 0 && char.IsUpper(hosts[0]) &&
                !hosts.StartsWith("The ", StringComparison.OrdinalIgnoreCase) &&
                program.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 &&
                !program.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            {
                return program;
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
               EndsAt == other.EndsAt &&
               Subtitle == other.Subtitle &&
               IsMusic == other.IsMusic;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(base.GetHashCode(), ChannelName, StartsAt, EndsAt, Subtitle, IsMusic);
    }
}
