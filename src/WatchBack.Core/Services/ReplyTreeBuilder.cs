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

        Dictionary<string, List<Thought>> byParent = flatList
            .Where(t => t.ParentId != null && t.ParentId != t.Id)
            .GroupBy(t => t.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Build each node iteratively using the "two-color" iterative post-order DFS:
        // push (node, false), then on first pop push (node, true) before the children.
        // Avoids stack overflow on deeply nested reply chains — Reddit threads can be hundreds of levels deep.
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
                    List<Thought> replies = hasChildren
                        ? nodeChildren!.Select(c =>
                                built.TryGetValue(c.Id, out Thought? b) ? b : c with { Replies = [] })
                            .ToList()
                        : [];
                    built[node.Id] = node with { Replies = replies };
                }
                else
                {
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

        return flatList
            .Where(t => t.ParentId == null || !byId.ContainsKey(t.ParentId))
            .Select(t => built.TryGetValue(t.Id, out Thought? b) ? b : t)
            .ToList();
    }
}
