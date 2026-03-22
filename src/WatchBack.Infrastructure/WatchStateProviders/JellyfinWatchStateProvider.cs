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

namespace WatchBack.Infrastructure.WatchStateProviders;

[JsonSerializable(typeof(JellyfinSessionDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class JellyfinJsonContext : JsonSerializerContext { }

public class JellyfinWatchStateProvider(
    HttpClient httpClient,
    IOptionsSnapshot<JellyfinOptions> options,
    IMemoryCache cache,
    ILogger<JellyfinWatchStateProvider> logger)
    : IWatchStateProvider
{
    private readonly JellyfinOptions _options = options.Value;

    public DataProviderMetadata Metadata => new WatchStateDataProviderMetadata(
        Name: "Jellyfin",
        Description: UiStrings.JellyfinWatchStateProvider_Metadata_Jellyfin_watch_state_provider,
        BrandData: new BrandData(
            Color: "#5580D0",
            LogoSvg:
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Jellyfin</title><path d=\"M12 .002C8.826.002-1.398 18.537.16 21.666c1.56 3.129 22.14 3.094 23.682 0C25.384 18.573 15.177 0 12 0zm7.76 18.949c-1.008 2.028-14.493 2.05-15.514 0C3.224 16.9 9.92 4.755 12.003 4.755c2.081 0 8.77 12.166 7.759 14.196zM12 9.198c-1.054 0-4.446 6.15-3.93 7.189.518 1.04 7.348 1.027 7.86 0 .511-1.027-2.874-7.19-3.93-7.19z\"/></svg>"
        )
    ) { SupportedExternalIds = new HashSet<string> { Imdb, Tmdb, Tvdb } };

    public async Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default)
    {
        try
        {
            const string cacheKey = "jellyfin:session";
            if (cache.TryGetValue(cacheKey, out MediaContext? cached))
            {
                return cached;
            }

            var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/Sessions");
            request.Headers.Add("X-Emby-Token", _options.ApiKey);
            var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var sessions = JsonSerializer.Deserialize<JellyfinSessionDto[]>(
                content,
                JellyfinJsonContext.Default.JellyfinSessionDtoArray) ?? [];

            var activeSession = sessions.FirstOrDefault(s => s.NowPlayingItem != null);
            if (activeSession?.NowPlayingItem is null)
            {
                return null;
            }

            var item = activeSession.NowPlayingItem;

            var externalIds = new Dictionary<string, string>();
            if (item.ProviderIds?.GetValueOrDefault("Imdb") is { } imdb) externalIds[Imdb] = imdb;
            if (item.ProviderIds?.GetValueOrDefault("Tmdb") is { } tmdb) externalIds[Tmdb] = tmdb;
            if (item.ProviderIds?.GetValueOrDefault("Tvdb") is { } tvdb) externalIds[Tvdb] = tvdb;

            var result = new EpisodeContext(
                Title: item.SeriesName ?? item.Name ?? "Unknown",
                ReleaseDate: item.PremiereDate.HasValue
                    ? new DateTimeOffset(item.PremiereDate.Value, TimeSpan.Zero)
                    : null,
                EpisodeTitle: item.Name ?? "Unknown",
                SeasonNumber: (short)(item.ParentIndexNumber ?? 0),
                EpisodeNumber: (short)(item.IndexNumber ?? 0),
                ExternalIds: externalIds.Count > 0 ? externalIds : null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Jellyfin watch state fetch failed");
            return null;
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/Sessions");
            request.Headers.Add("X-Emby-Token", _options.ApiKey);
            var response = await httpClient.SendAsync(request, cts.Token);

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
}

internal sealed record JellyfinSessionDto(
    [property: JsonPropertyName("Id")] string Id,
    [property: JsonPropertyName("NowPlayingItem")] JellyfinItemDto? NowPlayingItem);

internal sealed record JellyfinItemDto(
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("SeriesName")] string? SeriesName,
    [property: JsonPropertyName("ParentIndexNumber")] int? ParentIndexNumber,
    [property: JsonPropertyName("IndexNumber")] int? IndexNumber,
    [property: JsonPropertyName("PremiereDate")] DateTime? PremiereDate,
    [property: JsonPropertyName("ProviderIds")] Dictionary<string, string>? ProviderIds);