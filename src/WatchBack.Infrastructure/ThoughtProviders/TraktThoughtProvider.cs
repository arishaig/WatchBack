using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Infrastructure.Thoughts;

[JsonSerializable(typeof(TraktSearchResultDto))]
[JsonSerializable(typeof(TraktCommentDto[]))]
internal sealed partial class TraktThoughtJsonContext : JsonSerializerContext { }

public class TraktThoughtProvider : IThoughtProvider
{
    private readonly HttpClient _httpClient;
    private readonly TraktOptions _options;
    private readonly IMemoryCache _cache;
    private readonly IReplyTreeBuilder _treeBuilder;

    public TraktThoughtProvider(
        HttpClient httpClient,
        IOptions<TraktOptions> options,
        IMemoryCache cache,
        IReplyTreeBuilder treeBuilder)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _cache = cache;
        _treeBuilder = treeBuilder;
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
            var cacheKey = $"trakt:thoughts:{mediaContext.Title}";
            if (_cache.TryGetValue(cacheKey, out ThoughtResult? cached))
            {
                return cached;
            }

            // Search for the show
            var searchRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.trakt.tv/search/shows?query={Uri.EscapeDataString(mediaContext.Title)}");
            ConfigureRequestHeaders(searchRequest);

            var searchResponse = await _httpClient.SendAsync(searchRequest, ct);
            if (!searchResponse.IsSuccessStatusCode)
            {
                return new ThoughtResult(Source: "Trakt", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
            }

            var searchContent = await searchResponse.Content.ReadAsStringAsync(ct);
            var searchResult = JsonSerializer.Deserialize<TraktSearchResultDto>(
                searchContent,
                TraktThoughtJsonContext.Default.TraktSearchResultDto);

            var show = searchResult?.Shows?.FirstOrDefault()?.Show;
            if (show?.Ids?.Trakt == null)
            {
                return new ThoughtResult(Source: "Trakt", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
            }

            // Get comments
            var commentsRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.trakt.tv/shows/{show.Ids.Trakt}/comments");
            ConfigureRequestHeaders(commentsRequest);

            var commentsResponse = await _httpClient.SendAsync(commentsRequest, ct);
            if (!commentsResponse.IsSuccessStatusCode)
            {
                return new ThoughtResult(Source: "Trakt", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
            }

            var commentsContent = await commentsResponse.Content.ReadAsStringAsync(ct);
            var comments = JsonSerializer.Deserialize<TraktCommentDto[]>(
                commentsContent,
                TraktThoughtJsonContext.Default.TraktCommentDtoArray) ?? [];

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

            var treeThoughts = _treeBuilder.BuildTree(thoughts);

            var result = new ThoughtResult(
                Source: "Trakt",
                PostTitle: show.Title,
                PostUrl: null,
                ImageUrl: null,
                Thoughts: treeThoughts,
                NextPageToken: null);

            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch
        {
            return new ThoughtResult(Source: "Trakt", PostTitle: null, PostUrl: null, ImageUrl: null, Thoughts: [], NextPageToken: null);
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.trakt.tv/users/me/settings");
            ConfigureRequestHeaders(request);

            var response = await _httpClient.SendAsync(request, ct);

            return new ServiceHealth(
                IsHealthy: response.IsSuccessStatusCode,
                Message: response.IsSuccessStatusCode ? "OK" : response.StatusCode.ToString(),
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

    private void ConfigureRequestHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", _options.ClientId);
        request.Headers.Add("Authorization", $"Bearer {_options.AccessToken}");
    }
}

internal sealed record TraktShowIdsDto(
    [property: JsonPropertyName("trakt")] int? Trakt);

internal sealed record TraktShowDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("ids")] TraktShowIdsDto? Ids);

internal sealed record TraktShowSearchItemDto(
    [property: JsonPropertyName("show")] TraktShowDto? Show);

internal sealed record TraktSearchResultDto(
    [property: JsonPropertyName("shows")] TraktShowSearchItemDto[]? Shows);

internal sealed record TraktUserDto(
    [property: JsonPropertyName("username")] string? Username);

internal sealed record TraktCommentDto(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("rating")] double? Rating,
    [property: JsonPropertyName("user")] TraktUserDto? User,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);
