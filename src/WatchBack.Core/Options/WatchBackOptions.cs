namespace WatchBack.Core.Options;

public class WatchBackOptions
{
    public int TimeMachineDays { get; init; } = 14;
    public string WatchProvider { get; init; } = "";

    /// <summary>Served to the frontend via the config API; not read server-side.</summary>
    public string SearchEngine { get; set; } = "google";

    /// <summary>
    ///     Served to the frontend via the config API; not read server-side. Used when <see cref="SearchEngine" /> is
    ///     "custom".
    /// </summary>
    public string CustomSearchUrl { get; set; } = "";

    public bool SegmentedProgressBar { get; set; }
}
