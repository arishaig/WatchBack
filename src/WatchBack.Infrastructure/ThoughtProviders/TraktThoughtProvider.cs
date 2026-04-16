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

[JsonSerializable(typeof(TraktShowSearchItemDto[]))]
[JsonSerializable(typeof(TraktMovieSearchItemDto[]))]
[JsonSerializable(typeof(TraktCommentDto[]))]
internal sealed partial class TraktThoughtJsonContext : JsonSerializerContext;

public sealed class TraktThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<TraktOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    ILogger<TraktThoughtProvider> logger)
    : IThoughtProvider
{
    private static readonly ThoughtResult s_empty =
        new("Trakt", null, null, null, [], null);

    private readonly TraktOptions _options = options.Value;

    public DataProviderMetadata Metadata => new(
        "Trakt",
        UiStrings.TraktThoughtProvider_Metadata_Trakt_comments_provider,
        BrandData: new BrandData(
            "#9F42C6",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Trakt</title><path d=\"m15.082 15.107-.73-.73 9.578-9.583a4.499 4.499 0 0 0-.115-.575L13.662 14.382l1.08 1.08-.73.73-1.81-1.81L23.422 3.144c-.075-.15-.155-.3-.25-.44L11.508 14.377l2.154 2.155-.73.73-7.193-7.199.73-.73 4.309 4.31L22.546 1.86A5.618 5.618 0 0 0 18.362 0H5.635A5.637 5.637 0 0 0 0 5.634V18.37A5.632 5.632 0 0 0 5.635 24h12.732C21.477 24 24 21.48 24 18.37V6.19l-8.913 8.918zm-4.314-2.155L6.814 8.988l.73-.73 3.954 3.96zm1.075-1.084-3.954-3.96.73-.73 3.959 3.96zm9.853 5.688a4.141 4.141 0 0 1-4.14 4.14H6.438a4.144 4.144 0 0 1-4.139-4.14V6.438A4.141 4.141 0 0 1 6.44 2.3h10.387v1.04H6.438c-1.71 0-3.099 1.39-3.099 3.1V17.55c0 1.71 1.39 3.105 3.1 3.105h11.117c1.71 0 3.1-1.395 3.1-3.105v-1.754h1.04v1.754z\"/></svg>"
        )
    );

    public int ExpectedWeight => 1;

    public string ConfigSection => "Trakt";

    public bool IsConfigured => !string.IsNullOrEmpty(_options.ClientId);

    public string GetCacheKey(MediaContext mediaContext)
    {
        return mediaContext is EpisodeContext episode
            ? $"trakt:thoughts:{mediaContext.Title}:s{episode.SeasonNumber}e{episode.EpisodeNumber}"
            : $"trakt:thoughts:{mediaContext.Title}";
    }

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_options.ClientId))
            {
                logger.LogDebug("Trakt: skipping — no Client ID configured");
                return s_empty;
            }

            EpisodeContext? episode = mediaContext as EpisodeContext;
            string cacheKey = GetCacheKey(mediaContext);

            if (cache.TryGetValue(cacheKey, out ThoughtResult? cached))
            {
                return cached;
            }

            // Resolve Trakt slug and build the comments URL
            string commentsUrl;
            if (episode == null)
            {
                // Movie: resolve via external IDs (IMDB → TMDB; TVDB is TV-only) then title search
                string? movieId = await ResolveMovieSlugAsync(mediaContext, ct);
                if (movieId == null)
                {
                    logger.LogWarning("Trakt: no movie found for '{Title}'", mediaContext.Title);
                    return s_empty;
                }

                commentsUrl = $"https://api.trakt.tv/movies/{movieId}/comments/newest";
            }
            else
            {
                // TV show: resolve via external IDs (IMDB → TVDB → TMDB) then title search
                string? showId = await ResolveSlugAsync(mediaContext, ct);
                if (showId == null)
                {
                    logger.LogWarning("Trakt: no show found for '{Title}'", mediaContext.Title);
                    return s_empty;
                }

                commentsUrl =
                    $"https://api.trakt.tv/shows/{showId}/seasons/{episode.SeasonNumber}/episodes/{episode.EpisodeNumber}/comments/newest";
            }

            HttpRequestMessage commentsRequest = new(HttpMethod.Get, commentsUrl);
            ConfigureRequest(commentsRequest);

            HttpResponseMessage commentsResponse = await httpClient.SendAsync(commentsRequest, ct);
            if (!commentsResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Trakt comments failed: HTTP {Status} for '{Title}'",
                    (int)commentsResponse.StatusCode, mediaContext.Title);
                return s_empty;
            }

            string commentsContent = await commentsResponse.Content.ReadAsStringAsync(ct);
            TraktCommentDto[] comments = JsonSerializer.Deserialize<TraktCommentDto[]>(
                commentsContent,
                TraktThoughtJsonContext.Default.TraktCommentDtoArray) ?? [];
            logger.LogDebug("Trakt: fetched {Count} comment(s) for '{Title}'", comments.Length, mediaContext.Title);

            List<Thought> thoughts = comments
                .Select(c => new Thought(
                    $"trakt:{c.Id}",
                    null,
                    null,
                    c.Comment ?? "",
                    c.Id != null ? $"https://trakt.tv/comments/{c.Id}" : null,
                    [],
                    c.User?.Username ?? UiStrings.TraktThoughtProvider_GetThoughtsAsync_Unknown,
                    c.Rating != null ? (int)c.Rating : null,
                    c.CreatedAt ?? DateTimeOffset.UtcNow,
                    "Trakt",
                    []))
                .ToList();

            ThoughtResult result = new(
                "Trakt",
                null,
                null,
                null,
                treeBuilder.BuildTree(thoughts),
                null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Trakt thought fetch failed");
            return s_empty;
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
                    false,
                    UiStrings.TraktThoughtProvider_GetServiceHealthAsync_No_Client_ID_configured,
                    DateTimeOffset.UtcNow);
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            HttpRequestMessage request = new(HttpMethod.Get, "https://api.trakt.tv/shows/trending?limit=1");
            request.Headers.TryAddWithoutValidation("User-Agent", "WatchBack/1.0");
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", _options.ClientId);

            HttpResponseMessage response = await httpClient.SendAsync(request, cts.Token);

            return new ServiceHealth(
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "OK" : $"HTTP {(int)response.StatusCode}",
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

    void IDataProvider.ConfigureRequest(HttpRequestMessage request)
    {
        ConfigureRequest(request);
    }

    /// <summary>
    ///     Resolves a Trakt show slug from the media context. Tries external ID lookups in order
    ///     (IMDB → TVDB → TMDB) before falling back to a title text search. Result is cached for 24 h.
    /// </summary>
    private async Task<string?> ResolveSlugAsync(MediaContext mediaContext, CancellationToken ct)
    {
        string slugCacheKey = $"trakt:slug:{mediaContext.Title}";
        if (cache.TryGetValue(slugCacheKey, out string? cached) && cached != null)
        {
            return cached;
        }

        foreach ((string idType, string idValue) in ExternalIdType.GetShowLookupPriority(mediaContext.ExternalIds))
        {
            string url = $"https://api.trakt.tv/search/{idType}/{Uri.EscapeDataString(idValue)}?type=show";
            string? slug = await TryResolveSlugFromSearchUrlAsync(url, mediaContext.Title, ct);
            if (slug != null)
            {
                cache.Set(slugCacheKey, slug, TimeSpan.FromHours(24));
                return slug;
            }
        }

        // Fall back to title text search
        string textSearchUrl = $"https://api.trakt.tv/search/show?query={Uri.EscapeDataString(mediaContext.Title)}";
        string? textSlug = await TryResolveSlugFromSearchUrlAsync(textSearchUrl, mediaContext.Title, ct);
        if (textSlug != null)
        {
            cache.Set(slugCacheKey, textSlug, TimeSpan.FromHours(24));
            return textSlug;
        }

        return null;
    }

    /// <summary>
    ///     Calls a Trakt search endpoint and extracts the slug from the first show result.
    ///     Returns null if the request fails or yields no results.
    /// </summary>
    private async Task<string?> TryResolveSlugFromSearchUrlAsync(string url, string title, CancellationToken ct)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        ConfigureRequest(request);

        HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Trakt search failed: HTTP {Status} for '{Title}'",
                (int)response.StatusCode, title);
            return null;
        }

        string content = await response.Content.ReadAsStringAsync(ct);
        TraktShowSearchItemDto[]? results = JsonSerializer.Deserialize<TraktShowSearchItemDto[]>(
            content, TraktThoughtJsonContext.Default.TraktShowSearchItemDtoArray);

        TraktShowDto? show = results?.FirstOrDefault()?.Show;
        if (show?.Ids?.Slug is null && show?.Ids?.Trakt is null)
        {
            return null;
        }

        string slug = Uri.EscapeDataString(show.Ids?.Slug
                                           ?? show.Ids!.Trakt!.Value.ToString(CultureInfo.InvariantCulture));
        logger.LogDebug("Trakt: resolved '{Title}' → slug '{Slug}'", title, slug);
        return slug;
    }

    /// <summary>
    ///     Resolves a Trakt movie slug from the media context. Tries IMDB then TMDB external ID
    ///     lookups before falling back to a title text search. Result is cached for 24 h.
    /// </summary>
    private async Task<string?> ResolveMovieSlugAsync(MediaContext mediaContext, CancellationToken ct)
    {
        string slugCacheKey = $"trakt:movie-slug:{mediaContext.Title}";
        if (cache.TryGetValue(slugCacheKey, out string? cached) && cached != null)
        {
            return cached;
        }

        foreach ((string idType, string idValue) in ExternalIdType.GetMovieLookupPriority(mediaContext.ExternalIds))
        {
            string url = $"https://api.trakt.tv/search/{idType}/{Uri.EscapeDataString(idValue)}?type=movie";
            string? slug = await TryResolveMovieSlugFromSearchUrlAsync(url, mediaContext.Title, ct);
            if (slug != null)
            {
                cache.Set(slugCacheKey, slug, TimeSpan.FromHours(24));
                return slug;
            }
        }

        // Fall back to title text search
        string textSearchUrl =
            $"https://api.trakt.tv/search/movie?query={Uri.EscapeDataString(mediaContext.Title)}";
        string? textSlug = await TryResolveMovieSlugFromSearchUrlAsync(textSearchUrl, mediaContext.Title, ct);
        if (textSlug != null)
        {
            cache.Set(slugCacheKey, textSlug, TimeSpan.FromHours(24));
            return textSlug;
        }

        return null;
    }

    /// <summary>
    ///     Calls a Trakt movie search endpoint and extracts the slug from the first movie result.
    ///     Returns null if the request fails or yields no results.
    /// </summary>
    private async Task<string?> TryResolveMovieSlugFromSearchUrlAsync(string url, string title,
        CancellationToken ct)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        ConfigureRequest(request);

        HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Trakt movie search failed: HTTP {Status} for '{Title}'",
                (int)response.StatusCode, title);
            return null;
        }

        string content = await response.Content.ReadAsStringAsync(ct);
        TraktMovieSearchItemDto[]? results = JsonSerializer.Deserialize<TraktMovieSearchItemDto[]>(
            content, TraktThoughtJsonContext.Default.TraktMovieSearchItemDtoArray);

        TraktMovieDto? movie = results?.FirstOrDefault()?.Movie;
        if (movie?.Ids?.Slug is null && movie?.Ids?.Trakt is null)
        {
            return null;
        }

        string slug = Uri.EscapeDataString(movie.Ids?.Slug
                                           ?? movie.Ids!.Trakt!.Value.ToString(CultureInfo.InvariantCulture));
        logger.LogDebug("Trakt: resolved movie '{Title}' → slug '{Slug}'", title, slug);
        return slug;
    }

    private void ConfigureRequest(HttpRequestMessage request)
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

internal sealed record TraktMovieDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("ids")] TraktShowIdsDto? Ids);

internal sealed record TraktMovieSearchItemDto(
    [property: JsonPropertyName("movie")] TraktMovieDto? Movie);

internal sealed record TraktUserDto(
    [property: JsonPropertyName("username")]
    string? Username);

internal sealed record TraktCommentDto(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("comment")]
    string? Comment,
    [property: JsonPropertyName("rating")] double? Rating,
    [property: JsonPropertyName("user")] TraktUserDto? User,
    [property: JsonPropertyName("created_at")]
    DateTimeOffset? CreatedAt);
