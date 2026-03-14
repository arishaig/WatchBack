namespace WatchBack.Core.Models.ApiResponses;

public record ThoughtResultResponse(
    string Source,
    string? PostTitle,
    string? PostUrl,
    string? ImageUrl,
    IReadOnlyCollection<ThoughtResponse>? Thoughts,
    string? NextPageToken);
