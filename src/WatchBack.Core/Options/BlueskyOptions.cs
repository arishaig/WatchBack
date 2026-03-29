namespace WatchBack.Core.Options;

public class BlueskyOptions
{
    public string Handle { get; init; } = string.Empty;
    public string? AppPassword { get; init; }
    public int TokenCacheTtlSeconds { get; init; } = 5400;
    public int CacheTtlSeconds { get; init; } = 3600;
}
