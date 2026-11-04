using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     Finds the external ids (and, for an episode, the air date and numbers) of media known only by
///     name, such as what a live TV guide says is airing. Lookups are remembered, so asking again is cheap.
/// </summary>
public interface IMediaIdentifier
{
    /// <summary>
    ///     Returns <paramref name="media" /> with its ids, air date and episode numbers filled in where a match
    ///     is found, or <paramref name="media" /> unchanged. Never throws for a failed lookup.
    /// </summary>
    /// <param name="media">An episode or movie without external ids</param>
    /// <param name="year">The year the guide gives, to tell same-titled shows and films apart; null if unknown</param>
    /// <param name="ct">Cancellation token</param>
    Task<MediaContext> IdentifyAsync(MediaContext media, int? year, CancellationToken ct = default);
}
