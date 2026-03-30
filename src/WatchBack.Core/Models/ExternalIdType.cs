namespace WatchBack.Core.Models;

/// <summary>
///     Well-known external ID type keys for use with <see cref="MediaContext.ExternalIds" />.
///     Providers may populate any subset of these, and may also include additional keys
///     for ID types not listed here.
/// </summary>
public static class ExternalIdType
{
    public const string Imdb = "imdb";
    public const string Tmdb = "tmdb";
    public const string Tvdb = "tvdb";

    /// <summary>
    ///     Returns the external ID lookups to try for a TV show, in priority order:
    ///     IMDB (most canonical across all databases), TVDB (TV-specific, high quality for shows),
    ///     then TMDB. Only IDs present in <paramref name="externalIds" /> are yielded.
    /// </summary>
    public static IEnumerable<(string Type, string Value)> GetShowLookupPriority(
        IReadOnlyDictionary<string, string>? externalIds)
    {
        if (externalIds is null)
        {
            yield break;
        }

        if (externalIds.TryGetValue(Imdb, out string? imdb))
        {
            yield return (Imdb, imdb);
        }

        if (externalIds.TryGetValue(Tvdb, out string? tvdb))
        {
            yield return (Tvdb, tvdb);
        }

        if (externalIds.TryGetValue(Tmdb, out string? tmdb))
        {
            yield return (Tmdb, tmdb);
        }
    }

    /// <summary>
    ///     Returns the external ID lookups to try for a movie, in priority order:
    ///     IMDB then TMDB. TVDB is TV-only and is deliberately excluded.
    ///     Only IDs present in <paramref name="externalIds" /> are yielded.
    /// </summary>
    public static IEnumerable<(string Type, string Value)> GetMovieLookupPriority(
        IReadOnlyDictionary<string, string>? externalIds)
    {
        if (externalIds is null)
        {
            yield break;
        }

        if (externalIds.TryGetValue(Imdb, out string? imdb))
        {
            yield return (Imdb, imdb);
        }

        if (externalIds.TryGetValue(Tmdb, out string? tmdb))
        {
            yield return (Tmdb, tmdb);
        }
    }
}
