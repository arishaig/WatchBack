using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

public record WatchStateDataProviderMetadata(
    string Name,
    string Description,
    string? OverrideDisplayName = null
) : DataProviderMetadata(Name, Description, OverrideDisplayName);

public interface IWatchStateProvider : IDataProvider
{
    Task<MediaContext> GetCurrentMediaContextAsync();
}