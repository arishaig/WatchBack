namespace WatchBack.Core.Options;

public class TraktOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public int CacheTtlSeconds { get; set; } = 30;
}