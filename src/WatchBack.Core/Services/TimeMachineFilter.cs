using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Core.Services;

public class TimeMachineFilter : ITimeMachineFilter
{
    public IReadOnlyList<Thought> Apply(
        IEnumerable<Thought> thoughts,
        DateTimeOffset? airDate,
        int windowDays)
    {
        if (airDate == null)
        {
            return thoughts.ToList();
        }

        return thoughts
            .Where(t =>
            {
                // For windowDays == 0, include only thoughts on the same calendar day
                if (windowDays == 0)
                {
                    return t.CreatedAt.Date == airDate.Value.Date;
                }

                // For windowDays > 0, include thoughts within the time window
                return (t.CreatedAt - airDate.Value).TotalDays <= windowDays;
            })
            .ToList();
    }
}
