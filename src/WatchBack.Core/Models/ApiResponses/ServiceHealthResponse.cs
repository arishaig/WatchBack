namespace WatchBack.Core.Models.ApiResponses;

public record ServiceHealthResponse(
    bool IsHealthy,
    string? Message,
    DateTimeOffset CheckedAt);
