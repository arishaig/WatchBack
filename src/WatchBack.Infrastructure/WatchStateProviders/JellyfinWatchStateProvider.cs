using System.Globalization;
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

using static WatchBack.Core.Models.ExternalIdType;

namespace WatchBack.Infrastructure.WatchStateProviders;

[JsonSerializable(typeof(JellyfinSessionDto[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class JellyfinJsonContext : JsonSerializerContext;

public sealed class JellyfinWatchStateProvider(
    HttpClient httpClient,
    IOptionsSnapshot<JellyfinOptions> options,
    IMemoryCache cache,
    ILogger<JellyfinWatchStateProvider> logger)
    : IWatchStateProvider
{
    private readonly JellyfinOptions _options = options.Value;

    public DataProviderMetadata Metadata => new WatchStateDataProviderMetadata(
        "Jellyfin",
        UiStrings.JellyfinWatchStateProvider_Metadata_Jellyfin_watch_state_provider,
        BrandData: new BrandData(
            "#5580D0",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Jellyfin</title><path d=\"M12 .002C8.826.002-1.398 18.537.16 21.666c1.56 3.129 22.14 3.094 23.682 0C25.384 18.573 15.177 0 12 0zm7.76 18.949c-1.008 2.028-14.493 2.05-15.514 0C3.224 16.9 9.92 4.755 12.003 4.755c2.081 0 8.77 12.166 7.759 14.196zM12 9.198c-1.054 0-4.446 6.15-3.93 7.189.518 1.04 7.348 1.027 7.86 0 .511-1.027-2.874-7.19-3.93-7.19z\"/></svg>"
        )
    )
    { SupportedExternalIds = new HashSet<string> { Imdb, Tmdb, Tvdb } };

    public async Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default)
    {
        try
        {
            const string cacheKey = "jellyfin:session";
            if (cache.TryGetValue(cacheKey, out MediaContext? cached))
            {
                return cached;
            }

            HttpRequestMessage request = new(HttpMethod.Get, $"{_options.BaseUrl}/Sessions");
            request.Headers.Add("X-Emby-Token", _options.ApiKey);
            HttpResponseMessage response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(ct);
            JellyfinSessionDto[] sessions = JsonSerializer.Deserialize<JellyfinSessionDto[]>(
                content,
                JellyfinJsonContext.Default.JellyfinSessionDtoArray) ?? [];

            JellyfinSessionDto? activeSession = sessions.FirstOrDefault(s => s.NowPlayingItem != null);
            if (activeSession?.NowPlayingItem is null)
            {
                return null;
            }

            JellyfinItemDto? item = activeSession.NowPlayingItem;

            Dictionary<string, string> externalIds = new();
            if (item.ProviderIds?.GetValueOrDefault("Imdb") is { } imdb)
            {
                externalIds[Imdb] = imdb;
            }

            if (item.ProviderIds?.GetValueOrDefault("Tmdb") is { } tmdb)
            {
                externalIds[Tmdb] = tmdb;
            }

            if (item.ProviderIds?.GetValueOrDefault("Tvdb") is { } tvdb)
            {
                externalIds[Tvdb] = tvdb;
            }

            EpisodeContext result = new(
                item.SeriesName ?? item.Name ?? "Unknown",
                item.PremiereDate.HasValue
                    ? new DateTimeOffset(item.PremiereDate.Value, TimeSpan.Zero)
                    : null,
                item.Name ?? "Unknown",
                (short)(item.ParentIndexNumber ?? 0),
                (short)(item.IndexNumber ?? 0),
                externalIds.Count > 0 ? externalIds : null);

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
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            HttpRequestMessage request = new(HttpMethod.Get, $"{_options.BaseUrl}/Sessions");
            request.Headers.Add("X-Emby-Token", _options.ApiKey);
            HttpResponseMessage response = await httpClient.SendAsync(request, cts.Token);

            return new ServiceHealth(
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "OK" : response.StatusCode.ToString(),
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

    public string ConfigSection => "Jellyfin";

    public bool IsConfigured => !string.IsNullOrEmpty(_options.ApiKey);

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return
        [
            new ProviderConfigField("Jellyfin__BaseUrl",
                UiStrings.ConfigEndpoints_GetConfig_Server_URL,
                "text",
                "http://jellyfin:8096",
                !string.IsNullOrEmpty(_options.BaseUrl) && _options.BaseUrl != "http://jellyfin:8096",
                _options.BaseUrl,
                envVal("Jellyfin__BaseUrl"),
                isOverridden("Jellyfin", "BaseUrl")),
            new ProviderConfigField("Jellyfin__ApiKey",
                UiStrings.ConfigEndpoints_GetConfig_API_Key,
                "password",
                UiStrings.ConfigEndpoints_GetConfig_Required,
                !string.IsNullOrEmpty(_options.ApiKey),
                "",
                "",
                isOverridden("Jellyfin", "ApiKey"))
        ];
    }

    public async Task<ServiceHealth> TestConnectionAsync(
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct = default)
    {
        string baseUrl = formValues.ResolveFormValue("Jellyfin__BaseUrl", _options.BaseUrl);
        string apiKey = formValues.ResolveFormValue("Jellyfin__ApiKey", _options.ApiKey);

        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            return new ServiceHealth(false, UiStrings.ConfigEndpoints_TestJellyfin_Server_URL_and_API_Key_required,
                DateTimeOffset.UtcNow);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            return new ServiceHealth(false, UiStrings.ConfigEndpoints_TestJellyfin_, DateTimeOffset.UtcNow);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        HttpRequestMessage req = new(HttpMethod.Get, baseUrl.TrimEnd('/') + "/System/Info");
        req.Headers.Add("X-Emby-Authorization", $"MediaBrowser Token=\"{apiKey}\"");
        HttpResponseMessage res = await httpClient.SendAsync(req, cts.Token);

        if (!res.IsSuccessStatusCode)
        {
            return new ServiceHealth(false, $"HTTP {(int)res.StatusCode}", DateTimeOffset.UtcNow);
        }

        try
        {
            using JsonDocument doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cts.Token),
                cancellationToken: cts.Token);
            string? version = doc.RootElement.TryGetProperty("Version", out JsonElement v) &&
                              v.ValueKind == JsonValueKind.String
                ? v.GetString()?[..Math.Min(v.GetString()!.Length, 32)]
                : null;
            string? message = version is not null
#pragma warning disable CA1863
                ? string.Format(CultureInfo.CurrentCulture, UiStrings.ConfigEndpoints_TestJellyfin_Jellyfin_Version,
                    version)
#pragma warning restore CA1863
                : UiStrings.ConfigEndpoints_TestJellyfin_Connected;
            return new ServiceHealth(true, message, DateTimeOffset.UtcNow);
        }
        catch
        {
            return new ServiceHealth(true, UiStrings.ConfigEndpoints_TestJellyfin_Connected, DateTimeOffset.UtcNow);
        }
    }

    public string? RevealSecret(string key)
    {
        return key == "Jellyfin__ApiKey" ? _options.ApiKey : null;
    }
}

internal sealed record JellyfinSessionDto(
    [property: JsonPropertyName("Id")] string Id,
    [property: JsonPropertyName("NowPlayingItem")]
    JellyfinItemDto? NowPlayingItem);

internal sealed record JellyfinItemDto(
    [property: JsonPropertyName("Name")] string? Name,
    [property: JsonPropertyName("SeriesName")]
    string? SeriesName,
    [property: JsonPropertyName("ParentIndexNumber")]
    int? ParentIndexNumber,
    [property: JsonPropertyName("IndexNumber")]
    int? IndexNumber,
    [property: JsonPropertyName("PremiereDate")]
    DateTime? PremiereDate,
    [property: JsonPropertyName("ProviderIds")]
    Dictionary<string, string>? ProviderIds);
