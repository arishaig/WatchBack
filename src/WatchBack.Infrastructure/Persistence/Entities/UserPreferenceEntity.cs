namespace WatchBack.Infrastructure.Persistence.Entities;

/// <summary>
/// Stores user-configurable preferences.
/// </summary>
public class UserPreferenceEntity
{
    public int Id { get; init; }

    /// <summary>
    /// Preference key (e.g., "TimeMachineDays", "DefaultProvider")
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Preference value as string (parsed by consuming code as needed)
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// When this preference was last updated
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional description for admin reference
    /// </summary>
    public string? Description { get; init; }
}