namespace WatchBack.Core.Options;

public class LemmyOptions
{
    public string InstanceUrl { get; init; } = "https://lemmy.world";
    public string? Community { get; init; }
    public int MaxPosts { get; init; } = 3;
    public int MaxComments { get; init; } = 250;
    public int CacheTtlSeconds { get; init; } = 3600;
}
