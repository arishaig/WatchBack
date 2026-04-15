using System.Globalization;

using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     The minimal representation of a Thought provider. This could be
///     a source for comments, social media posts, reviews, or something else.
/// </summary>
public interface IThoughtProvider : IDataProvider
{
    /// <summary>
    ///     Upper bound on the total tick weight this provider will report via IProgress during a full fetch.
    ///     Used to compute the progress bar denominator before fetching begins.
    /// </summary>
    int ExpectedWeight { get; }

    /// <summary>
    ///     Returns the cache key this provider uses for the given media context.
    ///     Used by PrefetchService to evict stale entries without duplicating key formats.
    /// </summary>
    string GetCacheKey(MediaContext mediaContext);

    /// <summary>
    ///     Fetches Thoughts for the given media context. Providers should report progress
    ///     via <paramref name="progress" /> as work completes so the UI can update the progress bar.
    ///     Returns null if no relevant content was found.
    /// </summary>
    /// <param name="mediaContext">The media currently being watched</param>
    /// <param name="progress">
    ///     Optional progress sink; implementations should report one or more ticks summing to
    ///     ExpectedWeight
    /// </param>
    /// <param name="ct">Optional cancellation token.</param>
    Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, IProgress<SyncProgressTick>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the earliest creation date a post or comment must have to be considered relevant
    ///     for the given media context. Content older than 7 days before the air date is almost
    ///     certainly an unrelated false positive. Returns <c>null</c> when no release date is
    ///     available, meaning no floor is applied.
    /// </summary>
    static DateTimeOffset? GetDateFloor(MediaContext context) =>
        context.ReleaseDate?.AddDays(-7);

    /// <summary>
    ///     Builds the canonical text search query for a media context.
    ///     Encodes the business rules for how movies, normal episodes, and season-0 specials
    ///     are represented in text searches, independently of any specific API:
    ///     <list type="bullet">
    ///         <item>Movie (plain <see cref="MediaContext" />): "{title} movie"</item>
    ///         <item>Episode with valid season/episode numbers: "{title} S{ss}E{ee}"</item>
    ///         <item>Episode with season/episode 0 and a premiere date: "{title} {date}"</item>
    ///         <item>Episode with season/episode 0 and an episode title: "{title} {episodeTitle}"</item>
    ///         <item>Episode with season/episode 0 and neither: "{title}"</item>
    ///     </list>
    ///     Text-search providers should call this rather than duplicating the branching logic.
    /// </summary>
    static string BuildTextQuery(MediaContext context)
    {
        if (context is EpisodeContext episode && episode.SeasonNumber > 0 && episode.EpisodeNumber > 0)
        {
            return $"{context.Title} S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
        }

        string? qualifier = GetTextSearchQualifier(context);
        return qualifier is null ? context.Title : $"{context.Title} {qualifier}";
    }

    /// <summary>
    ///     Returns the qualifier to append to the show/movie title in a text search,
    ///     or <c>null</c> for normal episodes where the caller should supply their own
    ///     episode code format (e.g. S01E01, NxNN).
    ///     <list type="bullet">
    ///         <item>Movie: "movie"</item>
    ///         <item>Episode with valid season/episode numbers: null</item>
    ///         <item>Episode with season/episode 0 and a premiere date: locale-neutral date string</item>
    ///         <item>Episode with season/episode 0 and an episode title: the episode title</item>
    ///         <item>Episode with season/episode 0 and neither: null</item>
    ///     </list>
    ///     Providers that build multiple search specs (e.g. one global + one per-subreddit) can use
    ///     this to get the qualifier and compose their own spec list.
    /// </summary>
    static string? GetTextSearchQualifier(MediaContext context)
    {
        if (context is not EpisodeContext episode)
        {
            return "movie";
        }

        if (episode.SeasonNumber > 0 && episode.EpisodeNumber > 0)
        {
            return null; // Caller provides episode code(s) in their preferred format(s)
        }

        // Season 0 or episode 0 (specials, daily shows, etc.): fall back to premiere date or title
        if (context.ReleaseDate.HasValue)
        {
            return context.ReleaseDate.Value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        }

        return !string.IsNullOrWhiteSpace(episode.EpisodeTitle) ? episode.EpisodeTitle : null;
    }
}
