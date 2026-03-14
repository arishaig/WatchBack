namespace WatchBack.Core.Models.ApiResponses;

/// <summary>
/// Used to ensure that a service is online and
/// that any required authentication is valid
/// </summary>
/// <param name="IsHealthy">True if the service is working, false otherwise</param>
/// <param name="Message">Any success or error message returned by the service, may be used for display text in the UI</param>
/// <param name="CheckedAt">When the service health check was initiated</param>
public record ServiceHealthResponse(
    bool IsHealthy,
    string? Message,
    DateTimeOffset CheckedAt);
