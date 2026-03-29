namespace WatchBack.Core.Options;

public class RedditOptions
{
    public int MaxThreads { get; init; } = 3;
    public int MaxComments { get; init; } = 250;
    public int CacheTtlSeconds { get; init; } = 86400;
}
