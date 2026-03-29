using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     Provides ratings data for movies and TV shows from an external aggregator.
///     Implementations supply health checks and branding like other data providers.
/// </summary>
public interface IRatingsProvider : IDataProvider
{
    /// <summary>
    ///     Returns ratings from one or more aggregators for the title identified by the given IMDb ID.
    ///     Returns an empty list when the ID is unknown or the provider is unconfigured.
    /// </summary>
    Task<IReadOnlyList<MediaRating>> GetRatingsAsync(string imdbId, CancellationToken ct = default);
}
