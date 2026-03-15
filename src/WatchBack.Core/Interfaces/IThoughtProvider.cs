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
    /// <summary>
    /// Upper bound on the total tick weight this provider will report via IProgress during a full fetch.
    /// Used to compute the progress bar denominator before fetching begins.
    /// </summary>
    int ExpectedWeight { get; }

    /// <summary>
    /// Fetches Thoughts for the given media context. Providers should report progress
    /// via <paramref name="progress"/> as work completes so the UI can update the progress bar.
    /// Returns null if no relevant content was found.
    /// </summary>
    /// <param name="mediaContext">The media currently being watched</param>
    /// <param name="progress">Optional progress sink; implementations should report one or more ticks summing to ExpectedWeight</param>
    Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default);
}