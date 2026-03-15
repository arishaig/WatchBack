namespace WatchBack.Core.Options;

public class JellyfinOptions
{
    public string BaseUrl { get; set; } = "http://jellyfin:8096";
    public string ApiKey { get; set; } = string.Empty;
    public int CacheTtlSeconds { get; set; } = 10;
}