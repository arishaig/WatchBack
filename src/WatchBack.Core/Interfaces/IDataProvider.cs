using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

public record DataProviderMetadata(
    string Name,
    string Description,
    string? OverrideDisplayName = null
)
{
    public string DisplayName => OverrideDisplayName ?? Name;
}

public interface IDataProvider
{
    DataProviderMetadata Metadata { get; }

    Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default);
}