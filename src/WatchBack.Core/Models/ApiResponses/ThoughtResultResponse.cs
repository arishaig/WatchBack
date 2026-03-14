namespace WatchBack.Core.Models.ApiResponses;

/// <summary>
/// A response from a ThoughtProvider
/// </summary>
/// <param name="Source">The name of the ThoughtProvider, such as Reddit or Bluesky</param>
/// <param name="PostTitle">The title of the post or thread, if any</param>
/// <param name="PostUrl">The URL for the post or thread, if any</param>
/// <param name="ImageUrl">An optional collection of images if they were returned by the source</param>
/// <param name="Thoughts">The Thoughts returned from the source, often comments</param>
/// <param name="NextPageToken">If the response is paginated, the token for the next page</param>
public record ThoughtResultResponse(
    string Source,
    string? PostTitle,
    string? PostUrl,
    string? ImageUrl,
    IReadOnlyCollection<ThoughtResponse>? Thoughts,
    string? NextPageToken);
