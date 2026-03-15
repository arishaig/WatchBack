using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// Used for the Time Machine feature where only Thoughts posted
/// near the release date of a show or movie are shown
/// </summary>
public interface ITimeMachineFilter
{
    IReadOnlyList<Thought> Apply(
        IEnumerable<Thought> thoughts,
        DateTimeOffset? airDate,
        int windowDays);
}