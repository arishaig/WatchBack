namespace WatchBack.Api.Mcp;

/// <summary>What can be configured, and how, for the settings UI and the MCP setup tools alike.</summary>
public static class IntegrationCatalog
{
    /// <summary>
    ///     The app preferences that may be changed through the config API and MCP. An explicit list, not
    ///     every property of WatchBackOptions: a new option is only writable once it is added here.
    /// </summary>
    public static readonly IReadOnlyList<string> PreferenceKeys =
    [
        "WatchBack__WatchProvider",
        "WatchBack__TimeMachineDays",
        "WatchBack__SearchEngine",
        "WatchBack__CustomSearchUrl",
        "WatchBack__SegmentedProgressBar",
        "WatchBack__EnableSentimentAnalysis",
        "WatchBack__DisabledProviders"
    ];
}
