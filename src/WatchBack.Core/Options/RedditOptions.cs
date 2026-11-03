namespace WatchBack.Core.Options;

public class RedditOptions
{
    public int MaxThreads { get; init; } = 3;
    public int MaxComments { get; init; } = 250;
    public int CacheTtlSeconds { get; init; } = 86400;

    // Arctic Shift is the primary archive (it covers Reddit's whole history).
    public bool UseArcticShift { get; init; } = true;

    // PullPush is only a fallback: it is asked when Arctic Shift found nothing (or failed), and only for
    // episodes that aired before PullPushCutoff, because it stopped archiving around then.
    public bool UsePullPush { get; init; } = true;
    public DateTimeOffset PullPushCutoff { get; init; } = new(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
}
