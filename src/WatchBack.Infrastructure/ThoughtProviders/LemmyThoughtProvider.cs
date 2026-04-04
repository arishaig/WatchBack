using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Resources;

namespace WatchBack.Infrastructure.ThoughtProviders;

[JsonSerializable(typeof(LemmySearchResponseDto))]
[JsonSerializable(typeof(LemmyCommentListResponseDto))]
internal sealed partial class LemmyJsonContext : JsonSerializerContext;

public sealed class LemmyThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<LemmyOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    ILogger<LemmyThoughtProvider> logger)
    : IThoughtProvider
{
    private static readonly ThoughtResult s_empty = new("Lemmy", null, null, null, [], null);

    private readonly LemmyOptions _options = options.Value;

    public DataProviderMetadata Metadata => new(
        "Lemmy",
        UiStrings.LemmyThoughtProvider_Metadata_Posts_from_Lemmy,
        BrandData: new BrandData(
            "#00BC8C",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Lemmy</title><path d=\"M11.993 0C5.375 0 0 5.375 0 11.993S5.375 24 11.993 24 24 18.618 24 11.993 18.618 0 11.993 0zm4.196 6.3a1.657 1.657 0 1 1 .001 3.315A1.657 1.657 0 0 1 16.189 6.3zm-8.39 0a1.657 1.657 0 1 1 0 3.315A1.657 1.657 0 0 1 7.8 6.3zm4.193 11.087a5.959 5.959 0 0 1-4.635-2.202l1.283-1.043a4.287 4.287 0 0 0 3.352 1.587 4.287 4.287 0 0 0 3.352-1.587l1.283 1.043a5.959 5.959 0 0 1-4.635 2.202z\"/></svg>"
        )
    );

    public int ExpectedWeight => 1 + _options.MaxPosts;

    public string GetCacheKey(MediaContext mediaContext)
    {
        string host = GetInstanceHost();
        return mediaContext is EpisodeContext episode
            ? $"lemmy:{host}:thoughts:{mediaContext.Title}:S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}"
            : $"lemmy:{host}:thoughts:{mediaContext.Title}";
    }

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        try
        {
            string cacheKey = GetCacheKey(mediaContext);
            if (cache.TryGetValue(cacheKey, out ThoughtResult? cached))
            {
                return cached;
            }

            string instance = _options.InstanceUrl.TrimEnd('/');
            string query = IThoughtProvider.BuildTextQuery(mediaContext);

            string searchUrl =
                $"{instance}/api/v3/search?q={Uri.EscapeDataString(query)}&type_=Posts&listing_type=All&sort=TopAll&limit={_options.MaxPosts}";
            if (!string.IsNullOrWhiteSpace(_options.Community))
            {
                searchUrl += $"&community_name={Uri.EscapeDataString(_options.Community)}";
            }

            HttpResponseMessage searchResponse = await httpClient.GetAsync(searchUrl, ct);
            progress?.Report(new SyncProgressTick(1, "Lemmy"));

            if (!searchResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Lemmy search failed: HTTP {Status} for query '{Query}'",
                    (int)searchResponse.StatusCode, query);
                return s_empty;
            }

            string searchContent = await searchResponse.Content.ReadAsStringAsync(ct);
            LemmySearchResponseDto? searchResult = JsonSerializer.Deserialize(
                searchContent, LemmyJsonContext.Default.LemmySearchResponseDto);

            LemmyPostViewDto[] posts = searchResult?.Posts ?? [];

            if (posts.Length == 0)
            {
                ThoughtResult emptyResult = s_empty;
                cache.Set(cacheKey, emptyResult, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
                return emptyResult;
            }

            List<Thought> allThoughts = [];
            LemmyPostViewDto? topPost = null;

            foreach (LemmyPostViewDto postView in posts)
            {
                if (postView.Post is null)
                {
                    continue;
                }

                topPost ??= postView;

                string commentsUrl =
                    $"{instance}/api/v3/comment/list?post_id={postView.Post.Id}&sort=Top&max_depth=8&limit={_options.MaxComments}";

                HttpResponseMessage commentsResponse = await httpClient.GetAsync(commentsUrl, ct);
                progress?.Report(new SyncProgressTick(1, "Lemmy"));

                if (!commentsResponse.IsSuccessStatusCode)
                {
                    logger.LogWarning("Lemmy comment fetch failed: HTTP {Status} for post {PostId}",
                        (int)commentsResponse.StatusCode, postView.Post.Id);
                    continue;
                }

                string commentsContent = await commentsResponse.Content.ReadAsStringAsync(ct);
                LemmyCommentListResponseDto? commentList = JsonSerializer.Deserialize(
                    commentsContent, LemmyJsonContext.Default.LemmyCommentListResponseDto);

                LemmyCommentViewDto[] comments = commentList?.Comments ?? [];
                logger.LogInformation("Lemmy: post {PostId} ({Title}) returned {Count} comment(s)",
                    postView.Post.Id, postView.Post.Name, comments.Length);

                allThoughts.AddRange(comments
                    .Where(cv => cv.Comment is not null)
                    .Select(cv => MapCommentToThought(cv, postView.Post.Name, postView.Post.ApId, postView.Post.Body)));
            }

            IReadOnlyList<Thought> treeThoughts = treeBuilder.BuildTree(allThoughts);

            ThoughtResult result = new(
                "Lemmy",
                topPost?.Post?.Name,
                topPost?.Post?.ApId,
                null,
                treeThoughts,
                null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lemmy thought fetch failed");
            return s_empty;
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            string instance = _options.InstanceUrl.TrimEnd('/');
            HttpResponseMessage response = await httpClient.GetAsync($"{instance}/api/v3/site", cts.Token);

            return new ServiceHealth(
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "OK" : response.StatusCode.ToString(),
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ServiceHealth(false, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    public string ConfigSection => "Lemmy";

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return
        [
            new ProviderConfigField(
                "Lemmy__InstanceUrl",
                UiStrings.ConfigLemmy_InstanceUrl,
                "text",
                "https://lemmy.world",
                true,
                _options.InstanceUrl,
                envVal("Lemmy__InstanceUrl"),
                isOverridden("Lemmy", "InstanceUrl")),
            new ProviderConfigField(
                "Lemmy__Community",
                UiStrings.ConfigLemmy_Community,
                "text",
                "",
                true,
                _options.Community ?? "",
                envVal("Lemmy__Community"),
                isOverridden("Lemmy", "Community")),
            new ProviderConfigField(
                "Lemmy__MaxPosts",
                UiStrings.ConfigEndpoints_GetConfig_Max_Posts,
                "number",
                "3",
                true,
                _options.MaxPosts.ToString(CultureInfo.InvariantCulture),
                envVal("Lemmy__MaxPosts"),
                isOverridden("Lemmy", "MaxPosts")),
            new ProviderConfigField(
                "Lemmy__MaxComments",
                UiStrings.ConfigEndpoints_GetConfig_Max_Comments,
                "number",
                "250",
                true,
                _options.MaxComments.ToString(CultureInfo.InvariantCulture),
                envVal("Lemmy__MaxComments"),
                isOverridden("Lemmy", "MaxComments"))
        ];
    }

    private static Thought MapCommentToThought(LemmyCommentViewDto commentView, string? postTitle, string? postUrl,
        string? postBody)
    {
        LemmyCommentDto comment = commentView.Comment!;
        string? parentId = ParseParentId(comment.Path);

        DateTimeOffset createdAt = comment.Published is not null &&
                                   DateTimeOffset.TryParse(comment.Published, out DateTimeOffset dt)
            ? dt
            : DateTimeOffset.UtcNow;

        return new Thought(
            $"lemmy:{comment.Id}",
            parentId,
            null,
            comment.Content ?? "",
            comment.ApId,
            [],
            commentView.Creator?.Name ?? "Unknown",
            commentView.Counts?.Score,
            createdAt,
            "Lemmy",
            [],
            postTitle,
            postUrl,
            postBody);
    }

    // Lemmy encodes parent relationships via a dot-separated path rooted at virtual node "0".
    // e.g. "0.100" = top-level comment (parent is the virtual root)
    //      "0.100.200" = comment 200 is a reply to comment 100
    private static string? ParseParentId(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string[] segments = path.Split('.');

        // Path length < 3 means "0.commentId" — top-level, no parent
        if (segments.Length < 3)
        {
            return null;
        }

        return $"lemmy:{segments[^2]}";
    }

    private string GetInstanceHost()
    {
        try
        {
            return new Uri(_options.InstanceUrl).Host;
        }
        catch
        {
            return _options.InstanceUrl;
        }
    }
}

internal sealed record LemmySearchResponseDto(
    [property: JsonPropertyName("posts")] LemmyPostViewDto[]? Posts);

internal sealed record LemmyPostViewDto(
    [property: JsonPropertyName("post")] LemmyPostDto? Post,
    [property: JsonPropertyName("creator")] LemmyPersonDto? Creator,
    [property: JsonPropertyName("counts")] LemmyCountsDto? Counts);

internal sealed record LemmyPostDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("ap_id")] string? ApId,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("published")] string? Published);

internal sealed record LemmyCommentListResponseDto(
    [property: JsonPropertyName("comments")] LemmyCommentViewDto[]? Comments);

internal sealed record LemmyCommentViewDto(
    [property: JsonPropertyName("comment")] LemmyCommentDto? Comment,
    [property: JsonPropertyName("creator")] LemmyPersonDto? Creator,
    [property: JsonPropertyName("counts")] LemmyCountsDto? Counts);

internal sealed record LemmyCommentDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("published")] string? Published,
    [property: JsonPropertyName("ap_id")] string? ApId);

internal sealed record LemmyPersonDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("actor_id")] string? ActorId);

internal sealed record LemmyCountsDto(
    [property: JsonPropertyName("score")] int Score);
