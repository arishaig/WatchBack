using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Resources;

using static WatchBack.Core.Models.ExternalIdType;

namespace WatchBack.Infrastructure.ThoughtProviders;

[JsonSerializable(typeof(TraktShowSearchItemDto[]))]
[JsonSerializable(typeof(TraktCommentDto[]))]
internal sealed partial class TraktThoughtJsonContext : JsonSerializerContext { }

public class TraktThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<TraktOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    ILogger<TraktThoughtProvider> logger)
    : IThoughtProvider
{
    private static readonly ThoughtResult Empty =
        new(Source: "Trakt", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);

    private readonly TraktOptions _options = options.Value;

    public DataProviderMetadata Metadata => new ThoughtProviderMetadata(
        Name: "Trakt",
        Description: UiStrings.TraktThoughtProvider_Metadata_Trakt_comments_provider,
        BrandData: new BrandData(
            Color: "#9F42C6",
            LogoSvg:
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Trakt</title><path d=\"m15.082 15.107-.73-.73 9.578-9.583a4.499 4.499 0 0 0-.115-.575L13.662 14.382l1.08 1.08-.73.73-1.81-1.81L23.422 3.144c-.075-.15-.155-.3-.25-.44L11.508 14.377l2.154 2.155-.73.73-7.193-7.199.73-.73 4.309 4.31L22.546 1.86A5.618 5.618 0 0 0 18.362 0H5.635A5.637 5.637 0 0 0 0 5.634V18.37A5.632 5.632 0 0 0 5.635 24h12.732C21.477 24 24 21.48 24 18.37V6.19l-8.913 8.918zm-4.314-2.155L6.814 8.988l.73-.73 3.954 3.96zm1.075-1.084-3.954-3.96.73-.73 3.959 3.96zm9.853 5.688a4.141 4.141 0 0 1-4.14 4.14H6.438a4.144 4.144 0 0 1-4.139-4.14V6.438A4.141 4.141 0 0 1 6.44 2.3h10.387v1.04H6.438c-1.71 0-3.099 1.39-3.099 3.1V17.55c0 1.71 1.39 3.105 3.1 3.105h11.117c1.71 0 3.1-1.395 3.1-3.105v-1.754h1.04v1.754z\"/></svg>"
        )
    );

    public int ExpectedWeight => 1;

    public string? ConfigSection => "Trakt";

    public bool IsConfigured => !string.IsNullOrEmpty(_options.ClientId);

    public string GetCacheKey(MediaContext mediaContext)
    {
        var episode = mediaContext as EpisodeContext;
        return episode != null
            ? $"trakt:thoughts:{mediaContext.Title}:s{episode.SeasonNumber}e{episode.EpisodeNumber}"
            : $"trakt:thoughts:{mediaContext.Title}";
    }

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_options.ClientId))
            {
                logger.LogDebug("Trakt: skipping — no Client ID configured");
                return Empty;
            }

            var episode = mediaContext as EpisodeContext;
            var cacheKey = GetCacheKey(mediaContext);

            if (cache.TryGetValue(cacheKey, out ThoughtResult? cached))
                return cached;

            // Resolve show slug — tries external IDs first (IMDB → TVDB → TMDB), then falls back to title search
            var showId = await ResolveSlugAsync(mediaContext, ct);
            if (showId == null)
            {
                logger.LogWarning("Trakt: no show found for '{Title}'", mediaContext.Title);
                return Empty;
            }

            // Fetch comments
            var commentsUrl = episode != null
                ? $"https://api.trakt.tv/shows/{showId}/seasons/{episode.SeasonNumber}/episodes/{episode.EpisodeNumber}/comments/newest"
                : $"https://api.trakt.tv/shows/{showId}/comments/newest";

            var commentsRequest = new HttpRequestMessage(HttpMethod.Get, commentsUrl);
            ConfigureRequest(commentsRequest);

            var commentsResponse = await httpClient.SendAsync(commentsRequest, ct);
            if (!commentsResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Trakt comments failed: HTTP {Status} for '{Title}'",
                    (int)commentsResponse.StatusCode, mediaContext.Title);
                return Empty;
            }

            var commentsContent = await commentsResponse.Content.ReadAsStringAsync(ct);
            var comments = JsonSerializer.Deserialize<TraktCommentDto[]>(
                commentsContent,
                TraktThoughtJsonContext.Default.TraktCommentDtoArray) ?? [];
            logger.LogDebug("Trakt: fetched {Count} comment(s) for '{Title}'", comments.Length, mediaContext.Title);

            var thoughts = comments
                .Select(c => new Thought(
                    Id: $"trakt:{c.Id}",
                    ParentId: null,
                    Title: null,
                    Content: c.Comment ?? "",
                    Url: null,
                    Images: [],
                    Author: c.User?.Username ?? UiStrings.TraktThoughtProvider_GetThoughtsAsync_Unknown,
                    Score: c.Rating != null ? (int)c.Rating : null,
                    CreatedAt: c.CreatedAt ?? DateTimeOffset.UtcNow,
                    Source: "Trakt",
                    Replies: []))
                .ToList();

            var result = new ThoughtResult(
                Source: "Trakt",
                PostTitle: null,
                PostUrl: null,
                ImageUrl: null,
                Thoughts: treeBuilder.BuildTree(thoughts),
                NextPageToken: null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Trakt thought fetch failed");
            return Empty;
        }
        finally
        {
            progress?.Report(new SyncProgressTick(1, "Trakt"));
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_options.ClientId))
            {
                return new ServiceHealth(
                    IsHealthy: false,
                    Message: UiStrings.TraktThoughtProvider_GetServiceHealthAsync_No_Client_ID_configured,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.trakt.tv/shows/trending?limit=1");
            request.Headers.TryAddWithoutValidation("User-Agent", "WatchBack/1.0");
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", _options.ClientId);

            var response = await httpClient.SendAsync(request, cts.Token);

            return new ServiceHealth(
                IsHealthy: response.IsSuccessStatusCode,
                Message: response.IsSuccessStatusCode ? "OK" : $"HTTP {(int)response.StatusCode}",
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

    /// <summary>
    /// Resolves a Trakt show slug from the media context. Tries external ID lookups in order
    /// (IMDB → TVDB → TMDB) before falling back to a title text search. Result is cached for 24 h.
    /// </summary>
    private async Task<string?> ResolveSlugAsync(MediaContext mediaContext, CancellationToken ct)
    {
        var slugCacheKey = $"trakt:slug:{mediaContext.Title}";
        if (cache.TryGetValue(slugCacheKey, out string? cached) && cached != null)
            return cached;

        // Build the ordered list of ID lookups to attempt before falling back to text search.
        // IMDB is tried first (most canonical), then TVDB (TV-specific), then TMDB.
        var idLookups = new List<(string IdType, string IdValue)>();
        if (mediaContext.ExternalIds?.TryGetValue(Imdb, out var imdbId) == true && imdbId != null)
            idLookups.Add(("imdb", imdbId));
        if (mediaContext.ExternalIds?.TryGetValue(Tvdb, out var tvdbId) == true && tvdbId != null)
            idLookups.Add(("tvdb", tvdbId));
        if (mediaContext.ExternalIds?.TryGetValue(Tmdb, out var tmdbId) == true && tmdbId != null)
            idLookups.Add(("tmdb", tmdbId));

        foreach (var (idType, idValue) in idLookups)
        {
            var url = $"https://api.trakt.tv/search/{idType}/{Uri.EscapeDataString(idValue)}?type=show";
            var slug = await TryResolveSlugFromSearchUrlAsync(url, mediaContext.Title, ct);
            if (slug != null)
            {
                cache.Set(slugCacheKey, slug, TimeSpan.FromHours(24));
                return slug;
            }
        }

        // Fall back to title text search
        var textSearchUrl = $"https://api.trakt.tv/search/show?query={Uri.EscapeDataString(mediaContext.Title)}";
        var textSlug = await TryResolveSlugFromSearchUrlAsync(textSearchUrl, mediaContext.Title, ct);
        if (textSlug != null)
        {
            cache.Set(slugCacheKey, textSlug, TimeSpan.FromHours(24));
            return textSlug;
        }

        return null;
    }

    /// <summary>
    /// Calls a Trakt search endpoint and extracts the slug from the first show result.
    /// Returns null if the request fails or yields no results.
    /// </summary>
    private async Task<string?> TryResolveSlugFromSearchUrlAsync(string url, string title, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureRequest(request);

        var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Trakt search failed: HTTP {Status} for '{Title}'",
                (int)response.StatusCode, title);
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var results = JsonSerializer.Deserialize<TraktShowSearchItemDto[]>(
            content, TraktThoughtJsonContext.Default.TraktShowSearchItemDtoArray);

        var show = results?.FirstOrDefault()?.Show;
        if (show?.Ids?.Slug is null && show?.Ids?.Trakt is null)
            return null;

        var slug = Uri.EscapeDataString(show.Ids?.Slug
            ?? show.Ids!.Trakt!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        logger.LogDebug("Trakt: resolved '{Title}' → slug '{Slug}'", title, slug);
        return slug;
    }

    public void ConfigureRequest(HttpRequestMessage request)
    {
        IDataProvider.ApplyDefaultHeaders(request);
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", _options.ClientId);
    }
}

internal sealed record TraktShowIdsDto(
    [property: JsonPropertyName("trakt")] int? Trakt,
    [property: JsonPropertyName("slug")] string? Slug);

internal sealed record TraktShowDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("ids")] TraktShowIdsDto? Ids);

internal sealed record TraktShowSearchItemDto(
    [property: JsonPropertyName("show")] TraktShowDto? Show);

internal sealed record TraktUserDto(
    [property: JsonPropertyName("username")] string? Username);

internal sealed record TraktCommentDto(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("rating")] double? Rating,
    [property: JsonPropertyName("user")] TraktUserDto? User,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);