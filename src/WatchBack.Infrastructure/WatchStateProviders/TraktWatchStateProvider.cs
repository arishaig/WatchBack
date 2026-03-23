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

using static WatchBack.Core.Models.ExternalIdType;

namespace WatchBack.Infrastructure.WatchStateProviders;

[JsonSerializable(typeof(TraktWatchingDto))]
[JsonSerializable(typeof(TraktIdsDto))]
internal sealed partial class TraktJsonContext : JsonSerializerContext { }

public class TraktWatchStateProvider(
    HttpClient httpClient,
    IOptionsSnapshot<TraktOptions> options,
    IMemoryCache cache,
    ILogger<TraktWatchStateProvider> logger)
    : IWatchStateProvider
{
    private readonly TraktOptions _options = options.Value;

    public DataProviderMetadata Metadata => new WatchStateDataProviderMetadata(
        Name: "Trakt",
        Description: UiStrings.TraktWatchStateProvider_Metadata_Trakt_watch_state_provider,
        OverrideDisplayName: "Trakt.tv",
        BrandData: new BrandData(
            Color: "#9F42C6",
            LogoSvg:
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Trakt</title><path d=\"m15.082 15.107-.73-.73 9.578-9.583a4.499 4.499 0 0 0-.115-.575L13.662 14.382l1.08 1.08-.73.73-1.81-1.81L23.422 3.144c-.075-.15-.155-.3-.25-.44L11.508 14.377l2.154 2.155-.73.73-7.193-7.199.73-.73 4.309 4.31L22.546 1.86A5.618 5.618 0 0 0 18.362 0H5.635A5.637 5.637 0 0 0 0 5.634V18.37A5.632 5.632 0 0 0 5.635 24h12.732C21.477 24 24 21.48 24 18.37V6.19l-8.913 8.918zm-4.314-2.155L6.814 8.988l.73-.73 3.954 3.96zm1.075-1.084-3.954-3.96.73-.73 3.959 3.96zm9.853 5.688a4.141 4.141 0 0 1-4.14 4.14H6.438a4.144 4.144 0 0 1-4.139-4.14V6.438A4.141 4.141 0 0 1 6.44 2.3h10.387v1.04H6.438c-1.71 0-3.099 1.39-3.099 3.1V17.55c0 1.71 1.39 3.105 3.1 3.105h11.117c1.71 0 3.1-1.395 3.1-3.105v-1.754h1.04v1.754z\"/></svg>"
        )
    ) { SupportedExternalIds = new HashSet<string> { Imdb, Tmdb, Tvdb } };

    public void ConfigureRequest(HttpRequestMessage request)
    {
        IDataProvider.ApplyDefaultHeaders(request);
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", _options.ClientId);
        if (!string.IsNullOrEmpty(_options.AccessToken))
            request.Headers.Add("Authorization", $"Bearer {_options.AccessToken}");
    }

    public async Task<MediaContext?> GetCurrentMediaContextAsync(CancellationToken ct = default)
    {
        try
        {
            const string cacheKey = "trakt:watching";
            if (cache.TryGetValue(cacheKey, out MediaContext? cached))
            {
                return cached;
            }

            var username = !string.IsNullOrEmpty(_options.Username) ? _options.Username : "me";
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/users/{Uri.EscapeDataString(username)}/watching");
            ConfigureRequest(request);

            var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var watching = JsonSerializer.Deserialize<TraktWatchingDto>(
                content,
                TraktJsonContext.Default.TraktWatchingDto);

            if (watching?.Show == null || watching.Episode == null)
            {
                return null;
            }

            var externalIds = new Dictionary<string, string>();
            if (watching.Show.Ids?.Imdb is { } imdb) externalIds[Imdb] = imdb;
            if (watching.Show.Ids?.Tmdb is { } tmdb) externalIds[Tmdb] = tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (watching.Show.Ids?.Tvdb is { } tvdb) externalIds[Tvdb] = tvdb.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var result = new EpisodeContext(
                Title: watching.Show.Title ?? "Unknown",
                ReleaseDate: watching.Episode.FirstAired.HasValue
                    ? new DateTimeOffset(watching.Episode.FirstAired.Value, TimeSpan.Zero)
                    : null,
                EpisodeTitle: watching.Episode.Title ?? "Unknown",
                SeasonNumber: (short)(watching.Episode.Season ?? 0),
                EpisodeNumber: (short)(watching.Episode.Number ?? 0),
                ExternalIds: externalIds.Count > 0 ? externalIds : null);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Trakt watch state fetch failed");
            return null;
        }
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_options.Username) && string.IsNullOrEmpty(_options.AccessToken))
            {
                return new ServiceHealth(
                    IsHealthy: false,
                    Message: UiStrings.TraktWatchStateProvider_GetServiceHealthAsync_No_username_or_access_token_configured,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var username = !string.IsNullOrEmpty(_options.Username) ? _options.Username : "me";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.trakt.tv/users/{Uri.EscapeDataString(username)}/watching");
            ConfigureRequest(request);

            var response = await httpClient.SendAsync(request, cts.Token);

            switch (response.StatusCode)
            {
                case System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.NoContent:
                    {
                        var watching = response.StatusCode == System.Net.HttpStatusCode.OK ? UiStrings.TraktWatchStateProvider_GetServiceHealthAsync_currently_watching : UiStrings.TraktWatchStateProvider_GetServiceHealthAsync_idle;
                        return new ServiceHealth(
                            IsHealthy: true,
                            Message:
#pragma warning disable CA1863
                                string.Format(
                                    CultureInfo.CurrentCulture,
                                    UiStrings.TraktWatchStateProvider_GetServiceHealthAsync_Profile_reachable___0__, watching),
#pragma warning restore CA1863
                                        CheckedAt: DateTimeOffset.UtcNow);
                    }
                case System.Net.HttpStatusCode.Unauthorized:
                    return new ServiceHealth(
                        IsHealthy: false,
                        Message: UiStrings.TraktWatchStateProvider_GetServiceHealthAsync_checkaccesstoken,
                        CheckedAt: DateTimeOffset.UtcNow);
                default:
                    return new ServiceHealth(
                        IsHealthy: false,
                        Message: $"HTTP {(int)response.StatusCode}",
                        CheckedAt: DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            return new ServiceHealth(
                IsHealthy: false,
                Message: ex.Message,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    public string? ConfigSection => "Trakt";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_options.ClientId) || !string.IsNullOrEmpty(_options.Username);

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden) =>
    [
        new(Key: "Trakt__ClientId",
            Label: UiStrings.ConfigEndpoints_GetConfig_Client_ID,
            Type: "text",
            Placeholder: UiStrings.ConfigEndpoints_GetConfig_Optional__for_comments_,
            HasValue: !string.IsNullOrEmpty(_options.ClientId),
            Value: _options.ClientId ?? "",
            EnvValue: envVal("Trakt__ClientId"),
            IsOverridden: isOverridden("Trakt", "ClientId")),
        new(Key: "Trakt__AccessToken",
            Label: UiStrings.ConfigEndpoints_GetConfig_Access_Token__OAuth_,
            Type: "password",
            Placeholder: UiStrings.ConfigEndpoints_GetConfig_Optional__for_private_profile_,
            HasValue: !string.IsNullOrEmpty(_options.AccessToken),
            Value: "",
            EnvValue: "",
            IsOverridden: isOverridden("Trakt", "AccessToken")),
        new(Key: "Trakt__Username",
            Label: UiStrings.ConfigEndpoints_GetConfig_Username,
            Type: "text",
            Placeholder: UiStrings.ConfigEndpoints_GetConfig_Optional__public_profile_,
            HasValue: !string.IsNullOrEmpty(_options.Username),
            Value: _options.Username ?? "",
            EnvValue: envVal("Trakt__Username"),
            IsOverridden: isOverridden("Trakt", "Username")),
    ];

    public async Task<ServiceHealth> TestConnectionAsync(
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct = default)
    {
        static string Resolve(IReadOnlyDictionary<string, string> form, string key, string? fallback)
        {
            var v = form.GetValueOrDefault(key) ?? string.Empty;
            return v == "__EXISTING__" ? (fallback ?? string.Empty) : v;
        }

        var clientId = Resolve(formValues, "Trakt__ClientId", _options.ClientId);
        if (string.IsNullOrEmpty(clientId))
            return new ServiceHealth(false, UiStrings.TraktThoughtProvider_GetServiceHealthAsync_No_Client_ID_configured, DateTimeOffset.UtcNow);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.trakt.tv/shows/trending?limit=1");
        IDataProvider.ApplyDefaultHeaders(req);
        req.Headers.Add("trakt-api-version", "2");
        req.Headers.Add("trakt-api-key", clientId);
        var res = await httpClient.SendAsync(req, cts.Token);

        return res.StatusCode == System.Net.HttpStatusCode.OK
            ? new ServiceHealth(true, UiStrings.ConfigEndpoints_TestJellyfin_Connected, DateTimeOffset.UtcNow)
            : new ServiceHealth(false, $"HTTP {(int)res.StatusCode}", DateTimeOffset.UtcNow);
    }

    public string? RevealSecret(string key) =>
        key == "Trakt__AccessToken" ? _options.AccessToken : null;
}

internal sealed record TraktIdsDto(
    [property: JsonPropertyName("imdb")] string? Imdb,
    [property: JsonPropertyName("tmdb")] int? Tmdb,
    [property: JsonPropertyName("tvdb")] int? Tvdb);

internal sealed record TraktShowDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("ids")] TraktIdsDto? Ids);

internal sealed record TraktEpisodeDto(
    [property: JsonPropertyName("season")] int? Season,
    [property: JsonPropertyName("number")] int? Number,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("first_aired")] DateTime? FirstAired);

internal sealed record TraktWatchingDto(
    [property: JsonPropertyName("show")] TraktShowDto? Show,
    [property: JsonPropertyName("episode")] TraktEpisodeDto? Episode);