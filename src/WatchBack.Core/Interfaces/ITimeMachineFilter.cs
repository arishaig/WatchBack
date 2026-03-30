using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     Used for the Time Machine feature where only Thoughts posted
///     near the release date of a show or movie are shown
/// </summary>
public interface ITimeMachineFilter
{
    /// <summary>
    ///     Filters the given Thoughts to only those posted within
    ///     <paramref name="windowDays" /> days of the <paramref name="airDate" />.
    ///     Returns all Thoughts unchanged if airDate is null.
    /// </summary>
    /// <param name="thoughts">The full set of Thoughts to filter</param>
    /// <param name="airDate">The air date of the episode; if null, no filtering is applied</param>
    /// <param name="windowDays">How many days after the air date to include</param>
    IReadOnlyList<Thought> Apply(
        IEnumerable<Thought> thoughts,
        DateTimeOffset? airDate,
        int windowDays);
}
