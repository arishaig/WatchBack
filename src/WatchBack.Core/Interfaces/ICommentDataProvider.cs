namespace WatchBack.Core.Interfaces;

using WatchBack.Core.Models;

public record CommentDataProviderMetadata (
    string Name,
    string Description,
    BrandData BrandData,
    string? OverrideDisplayName = null
) : DataProviderMetadata(Name, Description, OverrideDisplayName);

public interface ICommentDataProvider : IDataProvider
{
    Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext);
}