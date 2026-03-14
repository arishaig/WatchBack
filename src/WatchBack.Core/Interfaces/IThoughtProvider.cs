namespace WatchBack.Core.Interfaces;

using WatchBack.Core.Models;

public record ThoughtProviderMetadata(
    string Name,
    string Description,
    BrandData BrandData,
    string? OverrideDisplayName = null
) : DataProviderMetadata(Name, Description, OverrideDisplayName, BrandData);

/// <summary>
/// The minimal requirement for a Thought provider.
/// </summary>
public interface IThoughtProvider : IDataProvider
{
    Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, CancellationToken ct = default);
}