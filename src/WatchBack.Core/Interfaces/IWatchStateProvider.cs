using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// Information about a WatchStateProvider
/// </summary>
/// <param name="Name">Used for grouping and display text in the UI</param>
/// <param name="Description">Used for display text in the UI</param>
/// <param name="BrandData">An object with branding data used in the UI for styling</param>
/// <param name="OverrideDisplayName">If populated sets DisplayName which is used for display text in the UI</param>
public record WatchStateDataProviderMetadata(
    string Name,
    string Description,
    string? OverrideDisplayName = null,
    BrandData? BrandData = null
) : DataProviderMetadata(Name, Description, OverrideDisplayName, BrandData)
{
    /// <summary>
    /// The set of external ID types this provider is capable of supplying.
    /// Use well-known keys from <see cref="Models.ExternalIdType"/> where applicable.
    /// An empty set means the provider does not populate <see cref="MediaContext.ExternalIds"/>.
    /// </summary>
    public IReadOnlySet<string> SupportedExternalIds { get; init; } = new HashSet<string>();
}

/// <summary>
/// A WatchStateProvider is any service that provides metadata for a specific
/// movie or TV show so that WatchBack can look online for Thoughts about
/// that piece of media. WatchBack can support any number of WatchStateProviders
/// but only one is active at any given time.
/// </summary>
public interface IWatchStateProvider : IDataProvider
{
    /// <summary>
    /// Returns the media currently being watched, or null if nothing is playing.
    /// </summary>
    Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default);
}