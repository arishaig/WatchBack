namespace WatchBack.Api.Models;

/// <summary>The summary shown at the top of the feed.</summary>
/// <param name="Status">ready, pending (ask again shortly), hidden, unavailable (no model set up), empty or failed</param>
/// <param name="Text">The summary when ready</param>
/// <param name="CommentCount">How many comments it was written from</param>
/// <param name="Compared">True when it also compares the early (time machine) reactions with the later ones</param>
public record SummaryResponse(string Status, string? Text, int CommentCount, bool Compared = false);
