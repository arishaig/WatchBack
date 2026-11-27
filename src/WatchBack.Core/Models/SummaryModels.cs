namespace WatchBack.Core.Models;

/// <summary>How the summary at the top of the feed is shown.</summary>
public static class SummaryModes
{
    /// <summary>No summary is generated or shown.</summary>
    public const string Hidden = "hidden";

    /// <summary>The summary is generated but stays folded until the viewer opens it.</summary>
    public const string Collapsed = "collapsed";

    /// <summary>The summary is generated and shown.</summary>
    public const string Open = "open";

    /// <summary>
    ///     The mode in effect: the saved one, else collapsed when a language model is set up and hidden when not,
    ///     so a summary never appears for someone who hasn't set one up.
    /// </summary>
    public static string Resolve(string? saved, bool modelConfigured) =>
        saved?.Trim().ToLowerInvariant() switch
        {
            Hidden => Hidden,
            Collapsed => Collapsed,
            Open => Open,
            _ => modelConfigured ? Collapsed : Hidden
        };

    public static bool IsValid(string? value) =>
        value is not null && (value.Equals(Hidden, StringComparison.OrdinalIgnoreCase) ||
                              value.Equals(Collapsed, StringComparison.OrdinalIgnoreCase) ||
                              value.Equals(Open, StringComparison.OrdinalIgnoreCase));
}

public enum SummaryStatus
{
    /// <summary>The summary is in <c>Text</c>.</summary>
    Ready,

    /// <summary>It is being written; ask again shortly.</summary>
    Pending,

    /// <summary>No language model is set up.</summary>
    Unavailable,

    /// <summary>There are no comments to summarize.</summary>
    Empty,

    /// <summary>The model couldn't be reached or answered badly; a retry follows after a short pause.</summary>
    Failed
}

/// <param name="Status">Where the summary stands</param>
/// <param name="Text">The summary when <paramref name="Status" /> is Ready</param>
/// <param name="CommentCount">How many comments it was written from</param>
/// <param name="Compared">True when it also compares the early reactions with the later ones</param>
public record ThoughtSummary(SummaryStatus Status, string? Text = null, int CommentCount = 0, bool Compared = false);
