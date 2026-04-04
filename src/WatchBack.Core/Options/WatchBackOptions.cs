using System.ComponentModel.DataAnnotations;

namespace WatchBack.Core.Options;

public class WatchBackOptions
{
    [Range(0, 365)]
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

    /// <summary>Comma-separated list of provider config-section names to skip during sync (e.g. "Lemmy,Bluesky").</summary>
    public string DisabledProviders { get; init; } = "";
}
