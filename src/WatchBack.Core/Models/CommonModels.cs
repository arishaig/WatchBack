namespace WatchBack.Core.Models;

public record ServiceHealth(
    bool IsHealthy,
    string? Message,
    DateTimeOffset CheckedAt);

// A good source for brand logo SVGs is https://simpleicons.org
public record BrandData(
    string Color,
    string LogoSvg);