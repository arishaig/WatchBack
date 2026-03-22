using System.Text;
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

[JsonSerializable(typeof(BlueskyAuthResponseDto))]
[JsonSerializable(typeof(BlueskySearchResponseDto))]
internal sealed partial class BlueskyJsonContext : JsonSerializerContext { }

public class BlueskyThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<BlueskyOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    ILogger<BlueskyThoughtProvider> logger)
    : IThoughtProvider
{
    private static readonly ThoughtResult Empty = new(Source: "Bluesky", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);

    private readonly BlueskyOptions _options = options.Value;

    public DataProviderMetadata Metadata => new ThoughtProviderMetadata(
        Name: "Bluesky",
        Description: UiStrings.BlueskyThoughtProvider_Metadata_Bluesky_skeets,
        BrandData: new BrandData(
            Color: "#1185FE",
            LogoSvg:
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Bluesky</title><path d=\"M5.202 2.857C7.954 4.922 10.913 9.11 12 11.358c1.087-2.247 4.046-6.436 6.798-8.501C20.783 1.366 24 .213 24 3.883c0 .732-.42 6.156-.667 7.037-.856 3.061-3.978 3.842-6.755 3.37 4.854.826 6.089 3.562 3.422 6.299-5.065 5.196-7.28-1.304-7.847-2.97-.104-.305-.152-.448-.153-.327 0-.121-.05.022-.153.327-.568 1.666-2.782 8.166-7.847 2.97-2.667-2.737-1.432-5.473 3.422-6.3-2.777.473-5.899-.308-6.755-3.369C.42 10.04 0 4.615 0 3.883c0-3.67 3.217-2.517 5.202-1.026\"/></svg>"
        )
    );

    public int ExpectedWeight => 1;

    public string GetCacheKey(MediaContext mediaContext)
    {
        var episode = mediaContext as EpisodeContext;
        return episode != null
            ? $"bluesky:thoughts:{mediaContext.Title}:S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}"
            : $"bluesky:thoughts:{mediaContext.Title}";
    }

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (mediaContext is not EpisodeContext episode)
            {
                return Empty;
            }

            var cacheKey = GetCacheKey(mediaContext);
            if (cache.TryGetValue(cacheKey, out ThoughtResult? cached))
            {
                return cached;
            }

            var token = await GetAccessTokenAsync(ct);

            // Search for posts
            var query = $"{mediaContext.Title} S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
            var searchUrl = $"https://bsky.social/xrpc/app.bsky.feed.searchPosts?q={Uri.EscapeDataString(query)}&limit=100";

            var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Add("Authorization", $"Bearer {token}");
            }

            var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Empty;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var searchResult = JsonSerializer.Deserialize<BlueskySearchResponseDto>(
                content,
                BlueskyJsonContext.Default.BlueskySearchResponseDto);

            var posts = searchResult?.Posts ?? [];
            var seenTexts = new HashSet<string>();
            var thoughts = new List<Thought>();

            foreach (var post in posts)
            {
                if (post.Record?.Text == null)
                    continue;

                var normalizedText = NormalizeText(post.Record.Text);
                if (!seenTexts.Add(normalizedText))
                    continue;

                var postThought = new Thought(
                    Id: $"bluesky:{post.Uri}",
                    ParentId: null,
                    Title: null,
                    Content: post.Record.Text,
                    Url: ToBlueskyWebUrl(post.Uri, post.Author?.Handle),
                    Images: post.Record.Embed?.Images?.Select(i => new ThoughtImage(Url: i.Image?.Link ?? "", Alt: i.Alt))
                        .ToList() ?? [],
                    Author: post.Author?.DisplayName ?? post.Author?.Handle ?? "Unknown",
                    Score: post.LikeCount,
                    CreatedAt: post.Record.CreatedAt ?? DateTimeOffset.UtcNow,
                    Source: "Bluesky",
                    Replies: []);

                thoughts.Add(postThought);
            }

            var treeThoughts = treeBuilder.BuildTree(thoughts);

            var result = new ThoughtResult(
                Source: "Bluesky",
                PostTitle: query,
                PostUrl: null,
                ImageUrl: null,
                Thoughts: treeThoughts,
                NextPageToken: null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bluesky thought fetch failed");
            return Empty;
        }
        finally
        {
            progress?.Report(new SyncProgressTick(1, "Bluesky"));
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await GetAccessTokenAsync(ct);

            return new ServiceHealth(
                IsHealthy: !string.IsNullOrEmpty(token),
                Message: !string.IsNullOrEmpty(token) ? "OK" : "Failed to authenticate",
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ServiceHealth(
                IsHealthy: false,
                Message: ex.Message,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.Handle) || string.IsNullOrEmpty(_options.AppPassword))
        {
            return null; // Use public API
        }

        // Key by credential hash so a credential change immediately invalidates the cached token
        var credentialHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"{_options.Handle}:{_options.AppPassword}")))[..16];
        var cacheKey = $"bluesky:auth:token:{credentialHash}";
        if (cache.TryGetValue(cacheKey, out string? cachedToken))
        {
            return cachedToken;
        }

        try
        {
            var authPayload = new { identifier = _options.Handle, password = _options.AppPassword };
            var content = new StringContent(
                JsonSerializer.Serialize(authPayload),
                Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync(
                "https://bsky.social/xrpc/com.atproto.server.createSession",
                content,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync(ct);
            var authResponse = JsonSerializer.Deserialize<BlueskyAuthResponseDto>(
                responseContent,
                BlueskyJsonContext.Default.BlueskyAuthResponseDto);

            var token = authResponse?.AccessJwt;
            if (token != null)
            {
                cache.Set(cacheKey, token, TimeSpan.FromSeconds(_options.TokenCacheTtlSeconds));
            }

            return token;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts an AT Protocol URI (at://did:plc:xxx/app.bsky.feed.post/rkey)
    /// to a browsable https://bsky.app URL.
    /// </summary>
    private static string? ToBlueskyWebUrl(string? atUri, string? handle)
    {
        if (string.IsNullOrEmpty(atUri))
            return null;

        // Extract the rkey (last path segment) from the AT URI
        var lastSlash = atUri.LastIndexOf('/');
        if (lastSlash < 0)
            return atUri;

        var rkey = atUri[(lastSlash + 1)..];
        if (string.IsNullOrEmpty(rkey))
            return atUri;

        // Use handle if available, otherwise extract the DID from the URI
        var authority = handle;
        if (string.IsNullOrEmpty(authority) && atUri.StartsWith("at://", StringComparison.Ordinal))
        {
            var afterScheme = atUri[5..];
            var slashIdx = afterScheme.IndexOf('/');
            authority = slashIdx > 0 ? afterScheme[..slashIdx] : afterScheme;
        }

        return !string.IsNullOrEmpty(authority)
            ? $"https://bsky.app/profile/{authority}/post/{rkey}"
            : atUri;
    }

    private static string NormalizeText(string text)
    {
        return text.ToLowerInvariant().Trim();
    }
}

internal sealed record BlueskyAuthResponseDto(
    [property: JsonPropertyName("accessJwt")] string? AccessJwt);

internal sealed record BlueskyImageDto(
    [property: JsonPropertyName("link")] string? Link);

internal sealed record BlueskyEmbedImageDto(
    [property: JsonPropertyName("image")] BlueskyImageDto? Image,
    [property: JsonPropertyName("alt")] string? Alt);

internal sealed record BlueskyEmbedDto(
    [property: JsonPropertyName("images")] BlueskyEmbedImageDto[]? Images);

internal sealed record BlueskyRecordDto(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("embed")] BlueskyEmbedDto? Embed);

internal sealed record BlueskyAuthorDto(
    [property: JsonPropertyName("handle")] string? Handle,
    [property: JsonPropertyName("displayName")] string? DisplayName);

internal sealed record BlueskyPostDto(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("cid")] string? Cid,
    [property: JsonPropertyName("author")] BlueskyAuthorDto? Author,
    [property: JsonPropertyName("record")] BlueskyRecordDto? Record,
    [property: JsonPropertyName("likeCount")] int? LikeCount);

internal sealed record BlueskySearchResponseDto(
    [property: JsonPropertyName("posts")] BlueskyPostDto[]? Posts);