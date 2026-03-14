namespace WatchBack.Core.Models;

public record BrandData(
    string Color,
    string LogoSvg);

public record Thought(
    string Id,
    string? ParentId,
    string? Title,
    string Content,
    string? Url,
    string? ImageUrl,
    string Author,
    int? Score,
    DateTimeOffset CreatedAt);

public record ThoughtResult(
    string? PostTitle,
    string? PostUrl,
    string? ImageUrl,
    IReadOnlyCollection<Thought>? Thoughts,
    string? NextPageToken);