namespace WatchBack.Infrastructure.Persistence.Entities;

/// <summary>Stores a hashed MCP API key for bearer token authentication on the /mcp endpoint.</summary>
public class ApiKeyEntity
{
    public int Id { get; init; }

    public required string Name { get; set; }

    /// <summary>SHA-256 of the full plaintext key, stored as lowercase hex. Indexed for fast lookup.</summary>
    public required string KeyHash { get; init; }

    /// <summary>First 12 characters of the key (e.g. "wb_AbCdEf123") shown in the UI to identify which key is which.</summary>
    public required string Prefix { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
