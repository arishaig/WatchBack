using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     Used to populate a parent Thought with other Thoughts whose
///     ParentIds are that Thought such that the parent Thought
///     object contains all the children of that Thought
/// </summary>
public interface IReplyTreeBuilder
{
    /// <summary>
    ///     Takes a flat list of Thoughts and returns the top-level Thoughts
    ///     with their Replies fully populated from the same list.
    /// </summary>
    /// <param name="flat">A flat collection of Thoughts, potentially including both parents and replies</param>
    IReadOnlyList<Thought> BuildTree(IEnumerable<Thought> flat);
}
