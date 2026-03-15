namespace WatchBack.Core.Options;

public class RedditOptions
{
    public int MaxThreads { get; set; } = 3;
    public int MaxComments { get; set; } = 250;
    public int CacheTtlSeconds { get; set; } = 86400;
}