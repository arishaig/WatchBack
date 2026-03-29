namespace WatchBack.Core.Options;

public class TraktOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public int CacheTtlSeconds { get; init; } = 300;
}
