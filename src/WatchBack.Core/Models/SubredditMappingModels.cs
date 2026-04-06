namespace WatchBack.Core.Models;

/// <summary>
///     A single show-to-subreddit mapping entry. Matching is attempted by external ID first
///     (when both the entry and the media context carry IDs), then by case-insensitive title.
/// </summary>
/// <param name="Title">Display title; used for title-based matching when no external ID is present.</param>
/// <param name="ExternalIds">
///     Optional external IDs (e.g. <c>"imdb"</c>, <c>"tmdb"</c>).
///     When present, an ID match on any key takes priority over title matching.
/// </param>
/// <param name="Subreddits">
///     Subreddit names (without the "r/" prefix) that host discussion for this show.
/// </param>
public sealed record SubredditMappingEntry(
    string? Title,
    IReadOnlyDictionary<string, string>? ExternalIds,
    IReadOnlyList<string> Subreddits);

/// <summary>
///     A named collection of subreddit mapping entries loaded from a single source file.
/// </summary>
/// <param name="Id">Stable identifier (filename without extension; "builtin" for the built-in file; "local" for manual entries).</param>
/// <param name="Name">Human-readable display name shown in the UI.</param>
/// <param name="IsBuiltIn">
///     <c>true</c> for the file that ships with the app and is overwritten on updates.
///     Built-in sources are read-only in the UI.
/// </param>
/// <param name="Entries">The mapping entries in this source.</param>
public sealed record SubredditMappingSource(
    string Id,
    string Name,
    bool IsBuiltIn,
    IReadOnlyList<SubredditMappingEntry> Entries);

/// <summary>Paths required by the subreddit mapping service, registered in DI by the API host.</summary>
/// <param name="BuiltInFilePath">
///     Full path to the built-in mappings JSON file that ships with the app.
///     Lives in the app directory (not the data directory) so it is overwritten on container update.
/// </param>
/// <param name="UserMappingsDirectory">
///     Directory that holds user-managed mapping files (local.json + imported files).
///     Lives in the data volume; created on first use.
/// </param>
public sealed record SubredditMappingPaths(string BuiltInFilePath, string UserMappingsDirectory);