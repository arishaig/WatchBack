using WatchBack.Core.Models;

namespace WatchBack.Api.Models;

/// <summary>
/// Consolidated sync result containing media context, all retrieved thoughts, and time-filtered thoughts
/// </summary>
/// <param name="Status">Current sync status: Idle, Watching, or Error</param>
/// <param name="Title">Title of the media currently being watched, or null if idle</param>
/// <param name="Metadata">Full media context including episode details, or null if idle</param>
/// <param name="AllThoughts">All thoughts retrieved from all configured thought providers</param>
/// <param name="TimeMachineThoughts">Subset of AllThoughts filtered to the configured time window</param>
/// <param name="TimeMachineDays">The number of days included in the time machine filter window</param>
/// <param name="SourceResults">Aggregated results from each thought provider with post metadata</param>
/// <param name="WatchProvider">Name of the watch state provider that supplied the current context</param>
/// <param name="SuppressedProvider">Configured provider that has active context but was overridden by the manual provider</param>
/// <param name="SuppressedTitle">Title the suppressed provider would have shown</param>
public record SyncResponse(
    string Status,
    string? Title,
    MediaContextResponse? Metadata,
    IReadOnlyList<ThoughtResponse> AllThoughts,
    IReadOnlyList<ThoughtResponse> TimeMachineThoughts,
    int TimeMachineDays,
    IReadOnlyList<SourceResultResponse> SourceResults,
    string? WatchProvider = null,
    string? SuppressedProvider = null,
    string? SuppressedTitle = null);

/// <summary>
/// Media context information for the currently watched content
/// </summary>
/// <param name="Title">Title of the show or movie</param>
/// <param name="ReleaseDate">Air date or release date of the content</param>
/// <param name="EpisodeTitle">Episode title (only populated for episodes)</param>
/// <param name="SeasonNumber">Season number (only populated for episodes)</param>
/// <param name="EpisodeNumber">Episode number (only populated for episodes)</param>
public record MediaContextResponse(
    string Title,
    DateTime? ReleaseDate,
    string? EpisodeTitle,
    int? SeasonNumber,
    int? EpisodeNumber);

/// <summary>
/// A single thought (comment, review, post, etc.) from a thought provider
/// </summary>
/// <param name="Id">Unique, deterministic ID derived from the source content</param>
/// <param name="ParentId">ID of the parent thought if this is a reply</param>
/// <param name="Title">Title of the thought (e.g., Reddit post title)</param>
/// <param name="Content">Text content of the thought</param>
/// <param name="Url">Direct URL to the thought in the source</param>
/// <param name="Images">Inline images associated with the thought</param>
/// <param name="Author">Author of the thought</param>
/// <param name="Score">Like/upvote count from the source, if available</param>
/// <param name="CreatedAt">Timestamp when the thought was created in the source</param>
/// <param name="Source">Source provider name (Reddit, Bluesky, Trakt, etc.)</param>
/// <param name="Replies">Child thoughts/replies to this thought</param>
/// <param name="PostTitle">Title of the parent post (e.g., Reddit thread title)</param>
/// <param name="PostUrl">URL of the parent post</param>
/// <param name="PostBody">Body text of the parent post (e.g., Reddit OP selftext)</param>
public record ThoughtResponse(
    string Id,
    string? ParentId,
    string? Title,
    string Content,
    string? Url,
    IReadOnlyList<ThoughtImageResponse> Images,
    string Author,
    int? Score,
    DateTime CreatedAt,
    string Source,
    IReadOnlyList<ThoughtResponse> Replies,
    string? PostTitle = null,
    string? PostUrl = null,
    string? PostBody = null);

/// <summary>
/// An image associated with a thought
/// </summary>
/// <param name="Url">URL to the image</param>
/// <param name="Alt">Alt text for the image</param>
public record ThoughtImageResponse(
    string Url,
    string? Alt);

/// <summary>
/// Aggregated results from a single thought provider
/// </summary>
/// <param name="Source">Name of the thought provider (Reddit, Bluesky, Trakt, etc.)</param>
/// <param name="PostTitle">Title of the post or discussion</param>
/// <param name="PostUrl">URL of the post</param>
/// <param name="ImageUrl">Hero image URL for the post</param>
/// <param name="Thoughts">Collection of thoughts from this source</param>
/// <param name="NextPageToken">Pagination token if results are paginated</param>
public record SourceResultResponse(
    string Source,
    string? PostTitle,
    string? PostUrl,
    string? ImageUrl,
    IReadOnlyList<ThoughtResponse> Thoughts,
    string? NextPageToken);