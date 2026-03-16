using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Core.Services;

public class ReplyTreeBuilder : IReplyTreeBuilder
{
    public IReadOnlyList<Thought> BuildTree(IEnumerable<Thought> flat)
    {
        var flatList = flat.ToList();
        if (flatList.Count == 0)
            return [];

        var byId = flatList.ToDictionary(t => t.Id);

        // Group children by parent ID in a single pass
        var byParent = flatList
            .Where(t => t.ParentId != null)
            .GroupBy(t => t.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build each node bottom-up via memoization
        var built = new Dictionary<string, Thought>(flatList.Count);

        Thought Build(Thought t)
        {
            if (built.TryGetValue(t.Id, out var cached))
                return cached;

            var replies = byParent.TryGetValue(t.Id, out var children)
                ? children.Select(Build).ToList()
                : [];

            var result = t with { Replies = replies };
            built[t.Id] = result;
            return result;
        }

        // Top-level: no parent, or parent not in the set
        return flatList
            .Where(t => t.ParentId == null || !byId.ContainsKey(t.ParentId))
            .Select(Build)
            .ToList();
    }
}
