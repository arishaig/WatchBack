using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Infrastructure.ThoughtProviders;

[JsonSerializable(typeof(TraktShowSearchItemDto[]))]
[JsonSerializable(typeof(TraktCommentDto[]))]
internal sealed partial class TraktThoughtJsonContext : JsonSerializerContext { }

public class TraktThoughtProvider : IThoughtProvider
{
    private readonly HttpClient _httpClient;
    private readonly TraktOptions _options;
    private readonly IMemoryCache _cache;
    private readonly IReplyTreeBuilder _treeBuilder;

    private readonly ILogger<TraktThoughtProvider> _logger;

    public TraktThoughtProvider(
        HttpClient httpClient,
        IOptionsSnapshot<TraktOptions> options,
        IMemoryCache cache,
        IReplyTreeBuilder treeBuilder,
        ILogger<TraktThoughtProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _cache = cache;
        _treeBuilder = treeBuilder;
        _logger = logger;
    }

    public DataProviderMetadata Metadata =>
        new ThoughtProviderMetadata(
            Name: "Trakt",
            Description: "Trakt comments provider",
            BrandData: new BrandData(
                    Color: "#E81828",
                    LogoSvg: "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Trakt</title><path d=\"m15.082 15.107-.73-.73 9.578-9.583a4.499 4.499 0 0 0-.115-.575L13.662 14.382l1.08 1.08-.73.73-1.81-1.81L23.422 3.144c-.075-.15-.155-.3-.25-.44L11.508 14.377l2.154 2.155-.73.73-7.193-7.199.73-.73 4.309 4.31L22.546 1.86A5.618 5.618 0 0 0 18.362 0H5.635A5.637 5.637 0 0 0 0 5.634V18.37A5.632 5.632 0 0 0 5.635 24h12.732C21.477 24 24 21.48 24 18.37V6.19l-8.913 8.918zm-4.314-2.155L6.814 8.988l.73-.73 3.954 3.96zm1.075-1.084-3.954-3.96.73-.73 3.959 3.96zm9.853 5.688a4.141 4.141 0 0 1-4.14 4.14H6.438a4.144 4.144 0 0 1-4.139-4.14V6.438A4.141 4.141 0 0 1 6.44 2.3h10.387v1.04H6.438c-1.71 0-3.099 1.39-3.099 3.1V17.55c0 1.71 1.39 3.105 3.1 3.105h11.117c1.71 0 3.1-1.395 3.1-3.105v-1.754h1.04v1.754z\"/></svg>"
                )
            );

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext, CancellationToken ct = default)
    {
        try
        {
            var episode = mediaContext as EpisodeContext;
            var cacheKey = episode != null
                ? $"trakt:thoughts:{mediaContext.Title}:s{episode.SeasonNumber}e{episode.EpisodeNumber}"
                : $"trakt:thoughts:{mediaContext.Title}";

            if (_cache.TryGetValue(cacheKey, out ThoughtResult? cached))
                return cached;

            // Resolve show slug — cached separately for 24 h since it never changes
            var slugCacheKey = $"trakt:slug:{mediaContext.Title}";
            if (!_cache.TryGetValue(slugCacheKey, out string? showId) || showId == null)
            {
                var searchRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.trakt.tv/search/show?query={Uri.EscapeDataString(mediaContext.Title)}");
                ConfigureRequestHeaders(searchRequest);

                var searchResponse = await _httpClient.SendAsync(searchRequest, ct);
                if (!searchResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trakt search failed: HTTP {Status} for '{Title}'",
                        (int)searchResponse.StatusCode, mediaContext.Title);
                    return Empty;
                }

                var searchContent = await searchResponse.Content.ReadAsStringAsync(ct);
                var searchResults = JsonSerializer.Deserialize<TraktShowSearchItemDto[]>(
                    searchContent,
                    TraktThoughtJsonContext.Default.TraktShowSearchItemDtoArray);

                var show = searchResults?.FirstOrDefault()?.Show;
                if (show?.Ids?.Slug == null && show?.Ids?.Trakt == null)
                {
                    _logger.LogWarning("Trakt: no show found for '{Title}'", mediaContext.Title);
                    return Empty;
                }

                showId = Uri.EscapeDataString(show.Ids?.Slug
                    ?? show.Ids!.Trakt!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

                _logger.LogDebug("Trakt: resolved '{Title}' → slug '{Slug}'", mediaContext.Title, showId);
                _cache.Set(slugCacheKey, showId, TimeSpan.FromHours(24));
            }

            // Fetch comments
            var commentsUrl = episode != null
                ? $"https://api.trakt.tv/shows/{showId}/seasons/{episode.SeasonNumber}/episodes/{episode.EpisodeNumber}/comments/newest"
                : $"https://api.trakt.tv/shows/{showId}/comments/newest";

            var commentsRequest = new HttpRequestMessage(HttpMethod.Get, commentsUrl);
            ConfigureRequestHeaders(commentsRequest);

            var commentsResponse = await _httpClient.SendAsync(commentsRequest, ct);
            if (!commentsResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Trakt comments failed: HTTP {Status} for '{Title}'",
                    (int)commentsResponse.StatusCode, mediaContext.Title);
                return Empty;
            }

            var commentsContent = await commentsResponse.Content.ReadAsStringAsync(ct);
            var comments = JsonSerializer.Deserialize<TraktCommentDto[]>(
                commentsContent,
                TraktThoughtJsonContext.Default.TraktCommentDtoArray) ?? [];
            _logger.LogDebug("Trakt: fetched {Count} comment(s) for '{Title}'", comments.Length, mediaContext.Title);

            var thoughts = comments
                .Select(c => new Thought(
                    Id: $"trakt:{c.Id}",
                    ParentId: null,
                    Title: null,
                    Content: c.Comment ?? "",
                    Url: null,
                    Images: [],
                    Author: c.User?.Username ?? "Unknown",
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
                Thoughts: _treeBuilder.BuildTree(thoughts),
                NextPageToken: null);

            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trakt thought fetch failed");
            return Empty;
        }
    }

    private static readonly ThoughtResult Empty =
        new(Source: "Trakt", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_options.ClientId))
            {
                return new ServiceHealth(
                    IsHealthy: false,
                    Message: "No Client ID configured",
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.trakt.tv/shows/trending?limit=1");
            request.Headers.TryAddWithoutValidation("User-Agent", "WatchBack/1.0");
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", _options.ClientId);

            var response = await _httpClient.SendAsync(request, cts.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                return new ServiceHealth(IsHealthy: true, Message: "API key valid", CheckedAt: DateTimeOffset.UtcNow);
            }

            return new ServiceHealth(
                IsHealthy: false,
                Message: $"HTTP {(int)response.StatusCode}",
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

    private void ConfigureRequestHeaders(HttpRequestMessage request, bool auth = false)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "WatchBack/1.0");
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", _options.ClientId);
        if (auth && !string.IsNullOrEmpty(_options.AccessToken))
            request.Headers.Add("Authorization", $"Bearer {_options.AccessToken}");
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