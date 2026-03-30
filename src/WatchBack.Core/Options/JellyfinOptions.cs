namespace WatchBack.Core.Options;

public class JellyfinOptions
{
    public string BaseUrl { get; init; } = "http://jellyfin:8096";
    public string ApiKey { get; init; } = string.Empty;
    public int CacheTtlSeconds { get; init; } = 10;
}
