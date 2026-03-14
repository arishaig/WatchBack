using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

public interface IReplyTreeBuilder
{
    IReadOnlyList<Thought> BuildTree(IEnumerable<Thought> flat);
}
