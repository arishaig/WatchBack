namespace WatchBack.Core.Options;

public class RedditOptions
{
    public int MaxThreads { get; init; } = 3;
    public int MaxComments { get; init; } = 250;
    public int CacheTtlSeconds { get; init; } = 86400;

    // Reddit archives to search. Both are queried and merged; either can be turned off if it goes dark.
    public bool UsePullPush { get; init; } = true;
    public bool UseArcticShift { get; init; } = true;
}
