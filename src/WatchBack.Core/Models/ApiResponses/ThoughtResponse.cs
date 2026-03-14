namespace WatchBack.Core.Models.ApiResponses;

public record ThoughtImageResponse(
    string Url,
    string? Alt);

/// <summary>
/// A response back from a source containing a Thought.
/// A Thought is any piece of content from an
/// integrated ThoughtProvider. Examples of possible Thoughts are:
/// <list type="bullet">
///     <item>
///         <description>Comments</description>
///     </item>
///     <item>
///         <description>Reviews</description>
///     </item>
///     <item>
///         <description>Social media posts</description>
///     </item>
/// </list>
/// </summary>
/// <param name="Id">A unique, deterministic ID for this response derived from the source content</param>
/// <param name="ParentId">If populated, this indicates that this Thought is a The response to another thought (the Parent)</param>
/// <param name="Title">Some Thoughts have titles, such as posts on Reddit or the title of a review</param>
/// <param name="Content">All Thoughts have text content, whether the body of a review or the contents of a Trakt comment</param>
/// <param name="Url">Some Thoughts have a source URL specific to the Thought, such as the link to a specific Reddit comment</param>
/// <param name="Images">Some Thoughts have one or more images, such as inline images in a Bluesky post or a gallery from a review</param>
/// <param name="Author">All Thoughts have an Author, usually a specific human who wrote the content, but potentially an organization</param>
/// <param name="Score">Some Thoughts have scores such as likes on Bluesky or karma on Reddit</param>
/// <param name="CreatedAt">All Thoughts have a date and time at which they were created in the source</param>
/// <param name="Source">The string representation of the source, used for grouping in the UI</param>
/// <param name="Replies">The fully qualified list of ThoughtResponses whose ParentId is this ThoughtResponse, if any</param>
public record ThoughtResponse(
    string Id,
    string? ParentId,
    string? Title,
    string Content,
    string? Url,
    IReadOnlyList<ThoughtImageResponse> Images,
    string Author,
    int? Score,
    DateTimeOffset CreatedAt,
    string Source,
    IReadOnlyList<ThoughtResponse> Replies);
