using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
/// Used to populate a parent Thought with other Thoughts whose
/// ParentIds are that Thought such that the parent Thought
/// object contains all the children of that Thought
/// </summary>
public interface IReplyTreeBuilder
{
    IReadOnlyList<Thought> BuildTree(IEnumerable<Thought> flat);
}