namespace WatchBack.Core.Options;

public class WatchBackOptions
{
    public int TimeMachineDays { get; set; } = 14;
    public string WatchProvider { get; set; } = "";
    /// <summary>Frontend-only: which search engine to use for external title searches.</summary>
    public string SearchEngine { get; set; } = "google";

    /// <summary>Frontend-only: custom search URL template when SearchEngine is "custom".</summary>
    public string CustomSearchUrl { get; set; } = "";
    public bool SegmentedProgressBar { get; set; }
}