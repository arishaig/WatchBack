using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// Information about a ThoughtProvider
/// </summary>
/// <param name="Name">Used for grouping and display text in the UI</param>
/// <param name="Description">Used for display text in the UI</param>
/// <param name="BrandData">An object with branding data used in the UI for styling</param>
/// <param name="OverrideDisplayName">If populated sets DisplayName which is used for display text in the UI</param>
public record ThoughtProviderMetadata(
    string Name,
    string Description,
    BrandData BrandData,
    string? OverrideDisplayName = null
) : DataProviderMetadata(Name, Description, OverrideDisplayName, BrandData);

/// <summary>
/// The minimal representation of a Thought provider. This could be
/// a source for comments, social media posts, reviews, or something else.
/// </summary>
public interface IThoughtProvider : IDataProvider
{
    Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, CancellationToken ct = default);
}