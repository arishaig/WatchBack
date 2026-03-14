namespace WatchBack.Core.Options;

public class WatchBackOptions
{
    public int TimeMachineDays { get; set; } = 14;
    public string WatchProvider { get; set; } = "jellyfin";
}
