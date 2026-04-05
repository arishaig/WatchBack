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
            "#5BB559",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Lemmy</title><path d=\"M2.9595 4.2228a3.9132 3.9132 0 0 0-.332.019c-.8781.1012-1.67.5699-2.155 1.3862-.475.8-.5922 1.6809-.35 2.4971.2421.8162.8297 1.5575 1.6982 2.1449.0053.0035.0106.0076.0163.0114.746.4498 1.492.7431 2.2877.8994-.02.3318-.0272.6689-.006 1.0181.0634 1.0432.4368 2.0006.996 2.8492l-2.0061.8189a.4163.4163 0 0 0-.2276.2239.416.416 0 0 0 .0879.455.415.415 0 0 0 .2941.1231.4156.4156 0 0 0 .1595-.0312l2.2093-.9035c.408.4859.8695.9315 1.3723 1.318.0196.0151.0407.0264.0603.0423l-1.2918 1.7103a.416.416 0 0 0 .664.501l1.314-1.7385c.7185.4548 1.4782.7927 2.2294 1.0242.3833.7209 1.1379 1.1871 2.0202 1.1871.8907 0 1.6442-.501 2.0242-1.2072.744-.2347 1.4959-.5729 2.2073-1.0262l1.332 1.7606a.4157.4157 0 0 0 .7439-.1936.4165.4165 0 0 0-.0799-.3074l-1.3099-1.7345c.0083-.0075.0178-.0113.0261-.0188.4968-.3803.9549-.8175 1.3622-1.2939l2.155.8794a.4156.4156 0 0 0 .5412-.2276.4151.4151 0 0 0-.2273-.5432l-1.9438-.7928c.577-.8538.9697-1.8183 1.0504-2.8693.0268-.3507.0242-.6914.0079-1.0262.7905-.1572 1.5321-.4502 2.2737-.8974.0053-.0033.011-.0076.0163-.0113.8684-.5874 1.456-1.3287 1.6982-2.145.2421-.8161.125-1.697-.3501-2.497-.4849-.8163-1.2768-1.2852-2.155-1.3863a3.2175 3.2175 0 0 0-.332-.0189c-.7852-.0151-1.6231.229-2.4286.6942-.5926.342-1.1252.867-1.5433 1.4387-1.1699-.6703-2.6923-1.0476-4.5635-1.0785a15.5768 15.5768 0 0 0-.5111 0c-2.085.034-3.7537.43-5.0142 1.1449-.0033-.0038-.0045-.0114-.008-.0152-.4233-.5916-.973-1.1365-1.5835-1.489-.8055-.465-1.6434-.7083-2.4286-.6941Zm.2858.7365c.5568.042 1.1696.2358 1.7787.5875.485.28.9757.7554 1.346 1.2696a5.6875 5.6875 0 0 0-.4969.4085c-.9201.8516-1.4615 1.9597-1.668 3.2335-.6809-.1402-1.3183-.3945-1.984-.7948-.7553-.5128-1.2159-1.1225-1.4004-1.7445-.1851-.624-.1074-1.2712.2776-1.9196.3743-.63.9275-.9534 1.6118-1.0322a2.796 2.796 0 0 1 .5352-.0076Zm17.5094 0a2.797 2.797 0 0 1 .5353.0075c.6842.0786 1.2374.4021 1.6117 1.0322.385.6484.4627 1.2957.2776 1.9196-.1845.622-.645 1.2317-1.4004 1.7445-.6578.3955-1.2881.6472-1.9598.7888-.1942-1.2968-.7375-2.4338-1.666-3.302a5.5639 5.5639 0 0 0-.4709-.3923c.3645-.49.8287-.9428 1.2938-1.2113.6091-.3515 1.2219-.5454 1.7787-.5875ZM12.006 6.0036a14.832 14.832 0 0 1 .487 0c2.3901.0393 4.0848.67 5.1631 1.678 1.1501 1.0754 1.6423 2.6006 1.499 4.467-.1311 1.7079-1.2203 3.2281-2.652 4.324-.694.5313-1.4626.9354-2.2254 1.2294.0031-.0453.014-.0888.014-.1349.0029-1.1964-.9313-2.2133-2.2918-2.2133-1.3606 0-2.3222 1.0154-2.2918 2.2213.0013.0507.014.0972.0181.1471-.781-.2933-1.5696-.7013-2.2777-1.2456-1.4239-1.0945-2.4997-2.6129-2.6037-4.322-.1129-1.8567.3778-3.3382 1.5212-4.3965C7.5094 6.7 9.352 6.047 12.006 6.0036Zm-3.6419 6.8291c-.6053 0-1.0966.4903-1.0966 1.0966 0 .6063.4913 1.0986 1.0966 1.0986s1.0966-.4923 1.0966-1.0986c0-.6063-.4913-1.0966-1.0966-1.0966zm7.2819.0113c-.5998 0-1.0866.4859-1.0866 1.0866s.4868 1.0885 1.0866 1.0885c.5997 0 1.0865-.4878 1.0865-1.0885s-.4868-1.0866-1.0865-1.0866zM12 16.0835c1.0237 0 1.5654.638 1.5634 1.4829-.0018.7849-.6723 1.485-1.5634 1.485-.9167 0-1.54-.5629-1.5634-1.493-.0212-.8347.5397-1.4749 1.5634-1.4749Z\"/></svg>"
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
        int reported = 0;
        try
        {
            try
            {
                string cacheKey = GetCacheKey(mediaContext);
                if (cache.TryGetValue(cacheKey, out ThoughtResult? cached))
                {
                    return cached;
                }

                string instance = _options.InstanceUrl.TrimEnd('/');

                // Build query variants to try in order. Community posts often omit leading zeros
                // (S1E3 vs S01E03), so if the canonical padded query returns nothing we fall back
                // to the non-padded form before giving up.
                List<string> queries = [IThoughtProvider.BuildTextQuery(mediaContext)];
                if (mediaContext is EpisodeContext { SeasonNumber: > 0, EpisodeNumber: > 0 } ep)
                {
                    string unpadded = $"{mediaContext.Title} S{ep.SeasonNumber}E{ep.EpisodeNumber}";
                    if (unpadded != queries[0])
                        queries.Add(unpadded);
                }

                LemmyPostViewDto[] posts = [];
                string usedQuery = queries[0];
                foreach (string query in queries)
                {
                    string searchUrl =
                        $"{instance}/api/v3/search?q={Uri.EscapeDataString(query)}&type_=Posts&listing_type=All&sort=TopAll&limit={_options.MaxPosts}";
                    if (!string.IsNullOrWhiteSpace(_options.Community))
                        searchUrl += $"&community_name={Uri.EscapeDataString(_options.Community)}";

                    HttpResponseMessage searchResponse = await httpClient.GetAsync(searchUrl, ct);
                    progress?.Report(new SyncProgressTick(1, "Lemmy"));
                    reported++;

                    if (!searchResponse.IsSuccessStatusCode)
                    {
                        logger.LogWarning("Lemmy search failed: HTTP {Status} for query '{Query}'",
                            (int)searchResponse.StatusCode, query);
                        continue;
                    }

                    string searchContent = await searchResponse.Content.ReadAsStringAsync(ct);
                    LemmySearchResponseDto? searchResult = JsonSerializer.Deserialize(
                        searchContent, LemmyJsonContext.Default.LemmySearchResponseDto);

                    posts = searchResult?.Posts ?? [];
                    usedQuery = query;
                    if (posts.Length > 0)
                        break;
                }

                logger.LogInformation("Lemmy: found {Count} post(s) for query '{Query}'", posts.Length, usedQuery);

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
                    reported++;

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
        finally
        {
            for (int i = reported; i < ExpectedWeight; i++)
                progress?.Report(new SyncProgressTick(1, "Lemmy"));
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

    // Lemmy requires no credentials — the default public instance is always functional.
    public bool IsConfigured => true;

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
                isOverridden("Lemmy", "InstanceUrl"),
                _options.InstanceUrl,
                envVal("Lemmy__InstanceUrl"),
                isOverridden("Lemmy", "InstanceUrl")),
            new ProviderConfigField(
                "Lemmy__Community",
                UiStrings.ConfigLemmy_Community,
                "text",
                "",
                !string.IsNullOrEmpty(_options.Community),
                _options.Community ?? "",
                envVal("Lemmy__Community"),
                isOverridden("Lemmy", "Community")),
            new ProviderConfigField(
                "Lemmy__MaxPosts",
                UiStrings.ConfigEndpoints_GetConfig_Max_Posts,
                "number",
                "3",
                isOverridden("Lemmy", "MaxPosts"),
                _options.MaxPosts.ToString(CultureInfo.InvariantCulture),
                envVal("Lemmy__MaxPosts"),
                isOverridden("Lemmy", "MaxPosts")),
            new ProviderConfigField(
                "Lemmy__MaxComments",
                UiStrings.ConfigEndpoints_GetConfig_Max_Comments,
                "number",
                "250",
                isOverridden("Lemmy", "MaxComments"),
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
