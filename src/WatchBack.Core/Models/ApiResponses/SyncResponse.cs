namespace WatchBack.Core.Models.ApiResponses;

/// <summary>
/// A consolidated collection of  data from various
/// DataProviders and other sources in  a format that
/// is easily parsable by the UI
/// </summary>
/// <param name="Status"></param>
/// <param name="Title"></param>
/// <param name="Metadata">From the WatchStateProvider</param>
/// <param name="AllThoughts">Consolidated collection of all Thoughts from all ThoughtProviders</param>
/// <param name="TimeMachineThoughts">Collection of Thoughts from all ThoughtProviders filtered by the Time Machine service</param>
/// <param name="TimeMachineDays">The setting used by the Time Machine service to filter the results</param>
/// <param name="SourceResults">All thoughts from all ThoughtProviders as they were returned from their sources</param>
public record SyncResponse(
    string Status,
    string? Title,
    MediaContextResponse? Metadata,
    IReadOnlyList<ThoughtResponse> AllThoughts,
    IReadOnlyList<ThoughtResponse> TimeMachineThoughts,
    int TimeMachineDays,
    IReadOnlyList<ThoughtResultResponse> SourceResults);
