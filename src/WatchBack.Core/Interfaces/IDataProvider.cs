using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// Information about a DataProvider
/// </summary>
/// <param name="Name">Used for grouping and display text in the UI if there is no DisplayName</param>
/// <param name="Description">Used for display text in the UI</param>
/// <param name="OverrideDisplayName">If populated sets DisplayName which is used for display text in the UI</param>
/// <param name="BrandData">An object with branding data used in the UI for styling</param>
public record DataProviderMetadata(
    string Name,
    string Description,
    string? OverrideDisplayName = null,
    BrandData? BrandData = null
)
{
    public string DisplayName => OverrideDisplayName ?? Name;
}

/// <summary>
/// A data provider could be any source for information. Generally other interfaces derive from this one to describe
/// implementations for specific types of DataProviders such as a WatchStateProvider
/// </summary>
public interface IDataProvider
{
    DataProviderMetadata Metadata { get; }

    Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default);
}