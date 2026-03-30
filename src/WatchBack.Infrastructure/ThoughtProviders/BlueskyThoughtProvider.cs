using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Extensions;
using WatchBack.Resources;

namespace WatchBack.Infrastructure.ThoughtProviders;

[JsonSerializable(typeof(BlueskyAuthResponseDto))]
[JsonSerializable(typeof(BlueskySearchResponseDto))]
internal sealed partial class BlueskyJsonContext : JsonSerializerContext;

public sealed class BlueskyThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<BlueskyOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    ILogger<BlueskyThoughtProvider> logger)
    : IThoughtProvider
{
    private static readonly ThoughtResult s_empty = new("Bluesky", null, null, null, [], null);

    private readonly BlueskyOptions _options = options.Value;

    public DataProviderMetadata Metadata => new(
        "Bluesky",
        UiStrings.BlueskyThoughtProvider_Metadata_Bluesky_skeets,
        BrandData: new BrandData(
            "#1185FE",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Bluesky</title><path d=\"M5.202 2.857C7.954 4.922 10.913 9.11 12 11.358c1.087-2.247 4.046-6.436 6.798-8.501C20.783 1.366 24 .213 24 3.883c0 .732-.42 6.156-.667 7.037-.856 3.061-3.978 3.842-6.755 3.37 4.854.826 6.089 3.562 3.422 6.299-5.065 5.196-7.28-1.304-7.847-2.97-.104-.305-.152-.448-.153-.327 0-.121-.05.022-.153.327-.568 1.666-2.782 8.166-7.847 2.97-2.667-2.737-1.432-5.473 3.422-6.3-2.777.473-5.899-.308-6.755-3.369C.42 10.04 0 4.615 0 3.883c0-3.67 3.217-2.517 5.202-1.026\"/></svg>"
        )
    );

    public int ExpectedWeight => 1;

    public string GetCacheKey(MediaContext mediaContext)
    {
        return mediaContext is EpisodeContext episode
            ? $"bluesky:thoughts:{mediaContext.Title}:S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}"
            : $"bluesky:thoughts:{mediaContext.Title}";
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

            string? token = await GetAccessTokenAsync(ct);

            string query = IThoughtProvider.BuildTextQuery(mediaContext);
            string searchUrl =
                $"https://bsky.social/xrpc/app.bsky.feed.searchPosts?q={Uri.EscapeDataString(query)}&limit=100";

            HttpRequestMessage request = new(HttpMethod.Get, searchUrl);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Add("Authorization", $"Bearer {token}");
            }

            HttpResponseMessage response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return s_empty;
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            BlueskySearchResponseDto? searchResult = JsonSerializer.Deserialize<BlueskySearchResponseDto>(
                content,
                BlueskyJsonContext.Default.BlueskySearchResponseDto);

            BlueskyPostDto[] posts = searchResult?.Posts ?? [];
            HashSet<string> seenTexts = new();
            List<Thought> thoughts = new();

            foreach (BlueskyPostDto post in posts)
            {
                if (post.Record?.Text == null)
                {
                    continue;
                }

                string normalizedText = NormalizeText(post.Record.Text);
                if (!seenTexts.Add(normalizedText))
                {
                    continue;
                }

                Thought postThought = new(
                    $"bluesky:{post.Uri}",
                    null,
                    null,
                    post.Record.Text,
                    ToBlueskyWebUrl(post.Uri, post.Author?.Handle),
                    post.Record.Embed?.Images?.Select(i => new ThoughtImage(i.Image?.Link ?? "", i.Alt))
                        .ToList() ?? [],
                    post.Author?.DisplayName ?? post.Author?.Handle ?? "Unknown",
                    post.LikeCount,
                    post.Record.CreatedAt ?? DateTimeOffset.UtcNow,
                    "Bluesky",
                    []);

                thoughts.Add(postThought);
            }

            IReadOnlyList<Thought> treeThoughts = treeBuilder.BuildTree(thoughts);

            ThoughtResult result = new(
                "Bluesky",
                query,
                null,
                null,
                treeThoughts,
                null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bluesky thought fetch failed");
            return s_empty;
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
            string? token = await GetAccessTokenAsync(ct);

            return new ServiceHealth(
                !string.IsNullOrEmpty(token),
                !string.IsNullOrEmpty(token) ? "OK" : "Failed to authenticate",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ServiceHealth(
                false,
                ex.Message,
                DateTimeOffset.UtcNow);
        }
    }

    public string ConfigSection => "Bluesky";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_options.Handle) && !string.IsNullOrEmpty(_options.AppPassword);

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return
        [
            new ProviderConfigField("Bluesky__Handle",
                UiStrings.ConfigEndpoints_GetConfig_Handle_Email,
                "text",
                "you.bsky.social",
                !string.IsNullOrEmpty(_options.Handle),
                _options.Handle,
                envVal("Bluesky__Handle"),
                isOverridden("Bluesky", "Handle")),
            new ProviderConfigField("Bluesky__AppPassword",
                UiStrings.ConfigEndpoints_GetConfig_Bluesky_App_Password,
                "password",
                "xxxx-xxxx-xxxx-xxxx",
                !string.IsNullOrEmpty(_options.AppPassword),
                "",
                "",
                isOverridden("Bluesky", "AppPassword"))
        ];
    }

    public async Task<ServiceHealth> TestConnectionAsync(
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct = default)
    {
        string handle = formValues.ResolveFormValue("Bluesky__Handle", _options.Handle);
        string password = formValues.ResolveFormValue("Bluesky__AppPassword", _options.AppPassword);

        if (string.IsNullOrEmpty(handle))
        {
            return new ServiceHealth(false, UiStrings.ConfigEndpoints_TestBluesky_Handle_required,
                DateTimeOffset.UtcNow);
        }

        if (string.IsNullOrEmpty(password))
        {
            return new ServiceHealth(true, UiStrings.ConfigEndpoints_TestBluesky_Handle_set__no_app_password_to_verify_,
                DateTimeOffset.UtcNow);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        HttpRequestMessage req = new(HttpMethod.Post, "https://bsky.social/xrpc/com.atproto.server.createSession");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { identifier = handle, password }),
            Encoding.UTF8, "application/json");
        HttpResponseMessage res = await httpClient.SendAsync(req, cts.Token);

        return res.IsSuccessStatusCode
            ? new ServiceHealth(true, UiStrings.ConfigEndpoints_TestJellyfin_Connected, DateTimeOffset.UtcNow)
            : new ServiceHealth(false,
                res.StatusCode == HttpStatusCode.Unauthorized
                    ? UiStrings.ConfigEndpoints_TestBluesky_Invalid_credentials
                    : $"HTTP {(int)res.StatusCode}",
                DateTimeOffset.UtcNow);
    }

    public string? RevealSecret(string key)
    {
        return key == "Bluesky__AppPassword" ? _options.AppPassword : null;
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.Handle) || string.IsNullOrEmpty(_options.AppPassword))
        {
            return null; // Use public API
        }

        // Key by credential hash so a credential change immediately invalidates the cached token
        string credentialHash = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes($"{_options.Handle}:{_options.AppPassword}")))[..16];
        string cacheKey = $"bluesky:auth:token:{credentialHash}";
        if (cache.TryGetValue(cacheKey, out string? cachedToken))
        {
            return cachedToken;
        }

        try
        {
            var authPayload = new { identifier = _options.Handle, password = _options.AppPassword };
            StringContent content = new(
                JsonSerializer.Serialize(authPayload),
                Encoding.UTF8,
                "application/json");

            HttpResponseMessage response = await httpClient.PostAsync(
                "https://bsky.social/xrpc/com.atproto.server.createSession",
                content,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string responseContent = await response.Content.ReadAsStringAsync(ct);
            BlueskyAuthResponseDto? authResponse = JsonSerializer.Deserialize<BlueskyAuthResponseDto>(
                responseContent,
                BlueskyJsonContext.Default.BlueskyAuthResponseDto);

            string? token = authResponse?.AccessJwt;
            if (token != null)
            {
                cache.Set(cacheKey, token, TimeSpan.FromSeconds(_options.TokenCacheTtlSeconds));
            }

            return token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Converts an AT Protocol URI (at://did:plc:xxx/app.bsky.feed.post/rkey)
    ///     to a browsable https://bsky.app URL.
    /// </summary>
    private static string? ToBlueskyWebUrl(string? atUri, string? handle)
    {
        if (string.IsNullOrEmpty(atUri))
        {
            return null;
        }

        // Extract the rkey (last path segment) from the AT URI
        int lastSlash = atUri.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return atUri;
        }

        string rkey = atUri[(lastSlash + 1)..];
        if (string.IsNullOrEmpty(rkey))
        {
            return atUri;
        }

        // Use handle if available, otherwise extract the DID from the URI
        string? authority = handle;
        if (string.IsNullOrEmpty(authority) && atUri.StartsWith("at://", StringComparison.Ordinal))
        {
            string afterScheme = atUri[5..];
            int slashIdx = afterScheme.IndexOf('/');
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
    [property: JsonPropertyName("accessJwt")]
    string? AccessJwt);

internal sealed record BlueskyImageDto(
    [property: JsonPropertyName("link")] string? Link);

internal sealed record BlueskyEmbedImageDto(
    [property: JsonPropertyName("image")] BlueskyImageDto? Image,
    [property: JsonPropertyName("alt")] string? Alt);

internal sealed record BlueskyEmbedDto(
    [property: JsonPropertyName("images")] BlueskyEmbedImageDto[]? Images);

internal sealed record BlueskyRecordDto(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("createdAt")]
    DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("embed")] BlueskyEmbedDto? Embed);

internal sealed record BlueskyAuthorDto(
    [property: JsonPropertyName("handle")] string? Handle,
    [property: JsonPropertyName("displayName")]
    string? DisplayName);

internal sealed record BlueskyPostDto(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("cid")] string? Cid,
    [property: JsonPropertyName("author")] BlueskyAuthorDto? Author,
    [property: JsonPropertyName("record")] BlueskyRecordDto? Record,
    [property: JsonPropertyName("likeCount")]
    int? LikeCount);

internal sealed record BlueskySearchResponseDto(
    [property: JsonPropertyName("posts")] BlueskyPostDto[]? Posts);
