using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     Resolves subreddits for a given show and manages the multi-source mapping store.
/// </summary>
public interface ISubredditMappingService
{
    /// <summary>
    ///     Returns the subreddits mapped to the given media context, or an empty list if no mapping
    ///     is found. Matching is attempted by external ID first, then by case-insensitive title.
    ///     Subreddits from all matching sources are merged and deduplicated.
    /// </summary>
    IReadOnlyList<string> GetSubreddits(MediaContext mediaContext);

    /// <summary>Returns all loaded sources in load order (built-in first, local last).</summary>
    IReadOnlyList<SubredditMappingSource> GetSources();

    /// <summary>
    ///     Imports a JSON mapping file, saving it to the user mappings directory and adding it as a new source.
    ///     The JSON must match the exchange format: <c>{ "mappings": [ { "title": ..., "subreddits": [...] } ] }</c>.
    /// </summary>
    Task<SubredditMappingSource> ImportAsync(string name, string json, CancellationToken ct = default);

    /// <summary>
    ///     Deletes an imported source by ID and removes its backing file.
    ///     Cannot delete the built-in source.
    /// </summary>
    Task DeleteSourceAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Adds an entry to the local (user-managed) source.</summary>
    Task AddLocalEntryAsync(SubredditMappingEntry entry, CancellationToken ct = default);

    /// <summary>
    ///     Removes an entry from the local source by case-insensitive title match.
    ///     No-op if no matching entry is found.
    /// </summary>
    Task DeleteLocalEntryAsync(string title, CancellationToken ct = default);

    /// <summary>
    ///     Copies an entry from any source into the local source (merging subreddits if the show
    ///     is already locally mapped). The entry remains in the original source until that source
    ///     is deleted.
    /// </summary>
    Task PromoteEntryAsync(string sourceId, int entryIndex, CancellationToken ct = default);

    /// <summary>Serializes a source back to the exchange JSON format for download.</summary>
    string ExportSource(string sourceId);
}