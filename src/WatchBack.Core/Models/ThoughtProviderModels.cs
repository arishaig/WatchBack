namespace WatchBack.Core.Models;

/// <summary>
/// A single image from a Thought, such as a Bluesky post image
/// </summary>
/// <param name="Url">The URL for the image, directly from the source</param>
/// <param name="Alt">Alt text for the image as provided by the source</param>
public record ThoughtImage(
    string Url,
    string? Alt);

/// <summary>
/// Represents a Thought. A Thought is any piece of content from an
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
/// <param name="PostTitle">The title of the post, such as the thread title for Reddit or the article title for a review</param>
/// <param name="PostUrl">The URL for the post as served by the source</param>
/// <param name="PostBody">The body/selftext of the original post (e.g. Reddit OP selftext), distinct from comment content</param>
public record Thought(
    string Id,
    string? ParentId,
    string? Title,
    string Content,
    string? Url,
    IReadOnlyList<ThoughtImage> Images,
    string Author,
    int? Score,
    DateTimeOffset CreatedAt,
    string Source,
    IReadOnlyList<Thought> Replies,
    string? PostTitle = null,
    string? PostUrl = null,
    string? PostBody = null);

/// <summary>
/// A collection of one or more Thoughts related to a given post
/// </summary>
/// <param name="Source">The name of the source, such as Bluesky or Reddit, used for grouping</param>
/// <param name="PostTitle">The title of the post, such as the thread title for Reddit or the article title for a review</param>
/// <param name="PostUrl">The URL for the post as served by the source</param>
/// <param name="ImageUrl">The hero image for the post, if returned from the source</param>
/// <param name="Thoughts">A collection of Thoughts</param>
/// <param name="NextPageToken">If the response is paginated, the token for the next page</param>
public record ThoughtResult(
    string Source,
    string? PostTitle,
    string? PostUrl,
    string? ImageUrl,
    IReadOnlyCollection<Thought>? Thoughts,
    string? NextPageToken);