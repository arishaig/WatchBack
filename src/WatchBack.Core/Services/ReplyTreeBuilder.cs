using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Core.Services;

public class ReplyTreeBuilder : IReplyTreeBuilder
{
    public IReadOnlyList<Thought> BuildTree(IEnumerable<Thought> flat)
    {
        var flatList = flat.ToList();
        if (flatList.Count == 0)
        {
            return [];
        }

        // Create a dictionary for quick parent lookup
        var thoughtsById = flatList.ToDictionary(t => t.Id);

        // Build replies for each thought
        var thoughtsWithReplies = new Dictionary<string, Thought>();
        foreach (var thought in flatList)
        {
            var replies = flatList
                .Where(t => t.ParentId == thought.Id)
                .Select(t => BuildTreeForThought(t, thoughtsById))
                .ToList();

            thoughtsWithReplies[thought.Id] = thought with { Replies = replies };
        }

        // Find top-level thoughts (no ParentId or parent not found)
        var topLevel = flatList
            .Where(t => t.ParentId == null || !thoughtsById.ContainsKey(t.ParentId))
            .Select(t => thoughtsWithReplies[t.Id])
            .ToList();

        return topLevel;
    }

    private static Thought BuildTreeForThought(Thought thought, Dictionary<string, Thought> allThoughts)
    {
        var replies = allThoughts.Values
            .Where(t => t.ParentId == thought.Id)
            .Select(t => BuildTreeForThought(t, allThoughts))
            .ToList();

        return thought with { Replies = replies };
    }
}
