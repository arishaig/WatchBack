using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     Optional interface for <see cref="IThoughtProvider" /> implementations that produce
///     content using a provider-specific syntax that must be converted to the canonical
///     WatchBack format before display (e.g. spoiler tags, excessive whitespace).
///     <para>
///         Implement this alongside <see cref="IThoughtProvider" /> to have
///         <see cref="WatchBack.Core.Services.SyncService" /> automatically apply the
///         transformation to every <see cref="Thought.Content" /> and
///         <see cref="Thought.PostBody" /> after fetching. Providers whose content is already
///         in canonical form do not need to implement this interface.
///     </para>
/// </summary>
public interface IContentNormalizer
{
    /// <summary>
    ///     Converts provider-specific content syntax to the canonical WatchBack format
    ///     (e.g. convert spoiler syntax, collapse excessive whitespace).
    /// </summary>
    string NormalizeContent(string content);
}