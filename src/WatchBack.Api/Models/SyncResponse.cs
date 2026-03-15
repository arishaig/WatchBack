using WatchBack.Core.Models;

namespace WatchBack.Api.Models;

public record SyncResponse(
    string Status,
    string? Title,
    MediaContextResponse? Metadata,
    IReadOnlyList<ThoughtResponse> AllThoughts,
    IReadOnlyList<ThoughtResponse> TimeMachineThoughts,
    int TimeMachineDays,
    IReadOnlyList<SourceResultResponse> SourceResults);

public record MediaContextResponse(
    string Title,
    DateTime? ReleaseDate,
    string? EpisodeTitle,
    int? SeasonNumber,
    int? EpisodeNumber);

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

public record ThoughtImageResponse(
    string Url,
    string? Alt);

public record SourceResultResponse(
    string Source,
    string? PostTitle,
    string? PostUrl,
    string? ImageUrl,
    IReadOnlyList<ThoughtResponse> Thoughts,
    string? NextPageToken);
