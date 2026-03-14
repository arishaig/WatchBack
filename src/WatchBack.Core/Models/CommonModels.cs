namespace WatchBack.Core.Models;

public record ServiceHealth(
    bool IsHealthy,
    string? Message,
    DateTimeOffset CheckedAt);