using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Core.Services;

public class ReplyTreeBuilder : IReplyTreeBuilder
{
    public IReadOnlyList<Thought> BuildTree(IEnumerable<Thought> flat)
    {
        List<Thought> flatList = flat.ToList();
        if (flatList.Count == 0)
        {
            return [];
        }

        Dictionary<string, Thought> byId = flatList.ToDictionary(t => t.Id);

        // Group children by parent ID in a single pass, excluding self-referential entries
        Dictionary<string, List<Thought>> byParent = flatList
            .Where(t => t.ParentId != null && t.ParentId != t.Id)
            .GroupBy(t => t.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build each node bottom-up via memoization
        Dictionary<string, Thought> built = new(flatList.Count);

        Thought Build(Thought t)
        {
            if (built.TryGetValue(t.Id, out Thought? cached))
            {
                return cached;
            }

            List<Thought> replies = byParent.TryGetValue(t.Id, out List<Thought>? children)
                ? children.Select(Build).ToList()
                : [];

            Thought result = t with { Replies = replies };
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
