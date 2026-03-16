using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// Provides media search capabilities for finding movies and TV shows.
/// Implementations supply a specific data source (e.g. OMDb, TVDB).
/// </summary>
public interface IMediaSearchProvider
{
    /// <summary>Metadata describing this search provider.</summary>
    MediaSearchProviderMetadata Metadata { get; }

    /// <summary>
    /// Searches for movies and TV shows matching the given query.
    /// When the query contains episode information (e.g. S02E05), implementations
    /// should attempt to resolve the specific episode in a single request.
    /// </summary>
    Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Returns the seasons available for a TV show identified by the given external ID.
    /// </summary>
    Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string externalId, CancellationToken ct = default);

    /// <summary>
    /// Returns the episodes in a specific season of a TV show.
    /// </summary>
    Task<IReadOnlyList<EpisodeInfo>> GetEpisodesAsync(string externalId, int seasonNumber, CancellationToken ct = default);
}

/// <summary>Metadata describing a media search provider.</summary>
public record MediaSearchProviderMetadata(
    string Name,
    string Description,
    string? OverrideDisplayName = null,
    BrandData? BrandData = null)
    : DataProviderMetadata(Name, Description, OverrideDisplayName, BrandData);
