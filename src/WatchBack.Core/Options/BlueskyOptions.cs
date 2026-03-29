namespace WatchBack.Core.Options;

public class BlueskyOptions
{
    public string Handle { get; set; } = string.Empty;
    public string? AppPassword { get; set; }
    public int TokenCacheTtlSeconds { get; set; } = 5400;
    public int CacheTtlSeconds { get; set; } = 3600;
}
