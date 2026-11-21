namespace WatchBack.Api.Models;

/// <summary>The summary shown at the top of the feed.</summary>
/// <param name="Status">ready, pending (ask again shortly), hidden, unavailable (no model set up), empty or failed</param>
/// <param name="Text">The summary when ready</param>
/// <param name="CommentCount">How many comments it was written from</param>
public record SummaryResponse(string Status, string? Text, int CommentCount);
