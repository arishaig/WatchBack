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
        if (airDate is null)
        {
            return thoughts.ToList();
        }

        return thoughts
            .Where(t =>
            {
                // Zero means same calendar day (UTC); positive means a rolling window after air date
                if (windowDays == 0)
                {
                    return t.CreatedAt.UtcDateTime.Date == airDate.Value.UtcDateTime.Date;
                }

                double deltaDays = (t.CreatedAt - airDate.Value).TotalDays;
                return deltaDays >= 0 && deltaDays <= windowDays;
            })
            .ToList();
    }
}
