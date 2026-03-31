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

        // Build each node iteratively using an explicit stack (post-order traversal)
        // to avoid stack overflow on deeply nested reply chains.
        Dictionary<string, Thought> built = new(flatList.Count);

        foreach (Thought root in flatList)
        {
            if (built.ContainsKey(root.Id))
            {
                continue;
            }

            Stack<(Thought Node, bool ChildrenProcessed)> stack = new();
            stack.Push((root, false));

            while (stack.Count > 0)
            {
                (Thought node, bool childrenProcessed) = stack.Pop();

                if (built.ContainsKey(node.Id))
                {
                    continue;
                }

                bool hasChildren = byParent.TryGetValue(node.Id, out List<Thought>? nodeChildren);

                if (childrenProcessed || !hasChildren)
                {
                    // All children are built — assemble this node
                    List<Thought> replies = hasChildren
                        ? nodeChildren!.Select(c =>
                                built.TryGetValue(c.Id, out Thought? b) ? b : c with { Replies = [] })
                            .ToList()
                        : [];
                    built[node.Id] = node with { Replies = replies };
                }
                else
                {
                    // Re-push this node to be finalized after its children
                    stack.Push((node, true));
                    foreach (Thought child in nodeChildren!)
                    {
                        if (!built.ContainsKey(child.Id))
                        {
                            stack.Push((child, false));
                        }
                    }
                }
            }
        }

        // Top-level: no parent, or parent not in the set
        return flatList
            .Where(t => t.ParentId == null || !byId.ContainsKey(t.ParentId))
            .Select(t => built.TryGetValue(t.Id, out Thought? b) ? b : t)
            .ToList();
    }
}
