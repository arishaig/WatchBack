namespace WatchBack.Core.Models;

/// <summary>
/// Contains the data about the health of the service, such as whether
/// it is online and whether the configured auth, if any, is valid
/// </summary>
/// <param name="IsHealthy">True if the service is online and configured, false otherwise</param>
/// <param name="Message">The success or error message, if any</param>
/// <param name="CheckedAt">The time at which the health check was initiated</param>
public record ServiceHealth(
    bool IsHealthy,
    string? Message,
    DateTimeOffset CheckedAt);

/// <summary>
/// Data about the branding for a DataProvider
/// A good source for brand logo SVGs is https://simpleicons.org
/// </summary>
/// <param name="Color">The primary color for the brand</param>
/// <param name="LogoSvg">The logo for the brand, should be the full text of an SVG element</param>
public record BrandData(
    string Color,
    string LogoSvg);

/// <summary>Describes a single configuration field shown in a provider's settings panel.</summary>
public record ProviderConfigField(
    string Key,
    string Label,
    string Type,
    string Placeholder,
    bool HasValue,
    string Value,
    string EnvValue,
    bool IsOverridden);
