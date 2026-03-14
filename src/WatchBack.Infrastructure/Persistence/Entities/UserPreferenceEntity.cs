namespace WatchBack.Infrastructure.Persistence.Entities;

/// <summary>
/// Stores user-configurable preferences.
/// </summary>
public class UserPreferenceEntity
{
    public int Id { get; set; }

    /// <summary>
    /// Preference key (e.g., "TimeMachineDays", "DefaultProvider")
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Preference value as string (parsed by consuming code as needed)
    /// </summary>
    public required string Value { get; set; }

    /// <summary>
    /// When this preference was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional description for admin reference
    /// </summary>
    public string? Description { get; set; }
}
