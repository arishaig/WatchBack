namespace WatchBack.Core.Models;

/// <summary>
/// Well-known external ID type keys for use with <see cref="MediaContext.ExternalIds"/>.
/// Providers may populate any subset of these, and may also include additional keys
/// for ID types not listed here.
/// </summary>
public static class ExternalIdType
{
    public const string Imdb = "imdb";
    public const string Tmdb = "tmdb";
    public const string Tvdb = "tvdb";
}
