using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

public interface ITimeMachineFilter
{
    IReadOnlyList<Thought> Apply(
        IEnumerable<Thought> thoughts,
        DateTimeOffset? airDate,
        int windowDays);
}
