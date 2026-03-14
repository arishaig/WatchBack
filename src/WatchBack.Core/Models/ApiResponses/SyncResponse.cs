namespace WatchBack.Core.Models.ApiResponses;

public record SyncResponse(
    string Status,
    string? Title,
    MediaContextResponse? Metadata,
    IReadOnlyList<ThoughtResponse> AllThoughts,
    IReadOnlyList<ThoughtResponse> TimeMachineThoughts,
    int TimeMachineDays,
    IReadOnlyList<ThoughtResultResponse> SourceResults);
