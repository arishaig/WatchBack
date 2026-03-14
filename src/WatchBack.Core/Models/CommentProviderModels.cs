namespace WatchBack.Core.Models;

public record ThoughtImage(
    string Url,
    string? Alt);

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
    IReadOnlyList<Thought> Replies);

public record ThoughtResult(
    string Source,
    string? PostTitle,
    string? PostUrl,
    string? ImageUrl,
    IReadOnlyCollection<Thought>? Thoughts,
    string? NextPageToken);