using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

public record WatchStateDataProviderMetadata(
    string Name,
    string Description,
    string? OverrideDisplayName = null,
    BrandData? BrandData = null
) : DataProviderMetadata(Name, Description, OverrideDisplayName, BrandData);

public interface IWatchStateProvider : IDataProvider
{
    Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default);
}