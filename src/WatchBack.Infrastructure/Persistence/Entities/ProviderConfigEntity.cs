namespace WatchBack.Infrastructure.Persistence.Entities;

/// <summary>
/// Stores provider-specific configuration securely.
/// Encrypted in production.
/// </summary>
public class ProviderConfigEntity
{
    public int Id { get; init; }

    /// <summary>
    /// Provider name (e.g., "Jellyfin", "Trakt", "Bluesky", "Reddit")
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// Configuration key (e.g., "ApiKey", "AccessToken", "Handle")
    /// </summary>
    public required string ConfigKey { get; init; }

    /// <summary>
    /// Configuration value (encrypted in production)
    /// </summary>
    public required string ConfigValue { get; init; }

    /// <summary>
    /// Whether this value is encrypted
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// When this configuration was last updated
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional description for admin reference
    /// </summary>
    public string? Description { get; init; }
}