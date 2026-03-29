using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Extensions;
using WatchBack.Resources;

// ReSharper disable once CheckNamespace
namespace WatchBack.Infrastructure.Omdb;

/// <summary>
///     OMDb provider implementing both media search and ratings lookup.
///     Shared infrastructure lives here; see the .Search and .Ratings partial files
///     for the per-interface implementations.
///     Registered as a scoped typed HTTP client via <c>AddHttpClient&lt;T&gt;</c>, which ensures
///     the <see cref="HttpClient" /> handler pipeline rotates correctly under IHttpClientFactory.
/// </summary>
public sealed partial class OmdbMediaSearchProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptionsSnapshot<OmdbOptions> options)
    : IMediaSearchProvider, IRatingsProvider
{
    private static readonly TimeSpan s_searchCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_episodeCacheDuration = TimeSpan.FromHours(24);

    // Shared brand data used by both the search and ratings provider metadata
    private static readonly BrandData s_omdbBrandData = new(
        "#626f53",
        "<svg width=\"65.343\" height=\"45.856\" viewBox=\"0 0 17.289 12.133\" xml:space=\"preserve\" xmlns=\"http://www.w3.org/2000/svg\"><path style=\"fill:#000\" d=\"M66.582 79.598c-.966-.518-1.398-.56-4.95-.495-6.922.128-9.283-1.278-9.283-5.529 0-4.033 2.469-5.667 8.544-5.654 7.143.016 10.364 3.42 7.952 8.403-.426.879-.528 1.43-.396 2.132.31 1.654-.261 2.004-1.867 1.143m1.265-1.243c-.1-.753.024-1.377.452-2.262 2.106-4.351-.44-7.299-6.48-7.5-6.28-.209-8.863 1.26-8.863 5.035 0 3.611 2.13 4.833 8.466 4.859 3.529.014 4.518.1 5.16.452 1.232.675 1.422.587 1.265-.584m-10.57-2.118c-2.384-1.083-2.34-4.461.07-5.469 3.805-1.59 8.838.08 8.838 2.933 0 2.698-5.256 4.194-8.908 2.536m7.072-.553c1.96-1.044 1.802-3.495-.278-4.327-4.379-1.752-9.797.903-7.373 3.613 1.088 1.216 5.87 1.662 7.65.714\" transform=\"translate(-52.349 -67.92)\"/><path style=\"fill:#626f53;fill-opacity:1;stroke-width:0;stroke-dashoffset:917.099\" d=\"M57.048 43.103c-.592-.158-1.114-.39-2.583-1.145-.726-.373-1.508-.746-1.738-.829-1.65-.592-3.999-.887-8.506-1.07-.742-.03-3.712-.08-6.6-.112-9.417-.105-12.56-.252-16.644-.781C7.585 37.43 2.319 32.469 2.325 21.589c.003-4.847 1.144-8.632 3.474-11.518.91-1.128 2.418-2.439 3.76-3.269 3.426-2.12 8.165-3.434 14.374-3.986 2.672-.238 3.388-.262 7.834-.262 4.32 0 5.296.03 7.575.233 7.016.624 12.627 2.302 16.644 4.978 3.343 2.227 5.513 5.075 6.486 8.512.333 1.176.468 2.146.501 3.595.033 1.443-.025 2.282-.25 3.615-.378 2.249-1.107 4.446-2.403 7.247-.99 2.14-1.388 3.233-1.658 4.562-.246 1.214-.282 2.725-.1 4.276.224 1.906.213 2.612-.05 3.155a.7.7 0 0 1-.367.371c-.297.144-.57.145-1.097.005m-22.785-9.127c5.782-.504 10.756-2.231 14.111-4.899 2.099-1.668 3.329-3.55 3.835-5.862.048-.221.074-.733.071-1.435-.004-1.202-.096-1.802-.433-2.812-1.657-4.97-7.76-8.907-15.746-10.157-1.906-.299-2.599-.345-5.138-.345-2.494 0-2.98.029-4.809.29-2.886.412-6.168 1.374-8.14 2.384-3.05 1.562-5.171 4.357-5.844 7.699-.754 3.74.371 7.598 2.98 10.224 1.45 1.46 3.068 2.384 5.844 3.337 2.606.895 5.462 1.444 8.506 1.633.97.06 3.81.026 4.763-.057\" transform=\"scale(.26458)\"/></svg>");

    public DataProviderMetadata Metadata =>
        new("OMDb", "Open Movie Database", BrandData: s_omdbBrandData);

    public string ConfigSection => "Omdb";

    bool IDataProvider.IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    IReadOnlyList<ProviderConfigField> IDataProvider.GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return
        [
            new ProviderConfigField("Omdb__ApiKey",
                UiStrings.ConfigEndpoints_GetConfig_API_Key,
                "password",
                UiStrings.ConfigEndpoints_GetConfig_Free_at_omdbapi_com__1_000_req_day_,
                !string.IsNullOrWhiteSpace(options.Value.ApiKey),
                "",
                "",
                isOverridden("Omdb", "ApiKey"))
        ];
    }

    async Task<ServiceHealth> IDataProvider.TestConnectionAsync(
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct)
    {
        string apiKey = formValues.ResolveFormValue("Omdb__ApiKey", options.Value.ApiKey);
        if (string.IsNullOrEmpty(apiKey))
        {
            return new ServiceHealth(false, UiStrings.ConfigEndpoints_TestOmdb_No_API_key_configured,
                DateTimeOffset.UtcNow);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        HttpResponseMessage res =
            await httpClient.GetAsync($"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(apiKey)}&t=test",
                cts.Token);
        if (!res.IsSuccessStatusCode)
        {
            return new ServiceHealth(false, $"HTTP {(int)res.StatusCode}", DateTimeOffset.UtcNow);
        }

        using JsonDocument doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cts.Token),
            cancellationToken: cts.Token);
        if (doc.RootElement.TryGetProperty("Error", out JsonElement err)
            && err.ValueKind == JsonValueKind.String
            && (err.GetString() ?? "").Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceHealth(false, err.GetString(), DateTimeOffset.UtcNow);
        }

        return new ServiceHealth(true, UiStrings.ConfigEndpoints_TestJellyfin_Connected, DateTimeOffset.UtcNow);
    }

    string? IDataProvider.RevealSecret(string key)
    {
        return key == "Omdb__ApiKey" ? options.Value.ApiKey : null;
    }

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        bool configured = !string.IsNullOrWhiteSpace(options.Value.ApiKey);
        return Task.FromResult(new ServiceHealth(
            configured,
            configured
                ? UiStrings.OmdbMediaSearchProvider_GetServiceHealthAsync_API_key_configured
                : UiStrings.OmdbMediaSearchProvider_GetServiceHealthAsync_No_API_key_configured,
            DateTimeOffset.UtcNow));
    }

    private async Task<T?> FetchJsonAsync<T>(string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, OmdbJsonContext.Default.Options, ct);
    }

    private static string? NullIfNa(string? value)
    {
        return string.IsNullOrEmpty(value) || value == "N/A" ? null : value;
    }

    // OMDb returns dates as "d MMM yyyy" (e.g. "20 Jan 2008") — TryParse with InvariantCulture
    // does not reliably handle this format, so we use TryParseExact as the primary path.
    private static DateTimeOffset? ParseOmdbDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "N/A")
        {
            return null;
        }

        if (DateTimeOffset.TryParseExact(raw, "d MMM yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset exact))
        {
            return exact;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                out DateTimeOffset fallback))
        {
            return fallback;
        }

        return null;
    }
}

// --- OMDb JSON context ---
// All [JsonSerializable] attributes must be on a single declaration for the source generator.
// DTOs are defined in .Search.cs and .Ratings.cs alongside the code that uses them.

[JsonSerializable(typeof(OmdbSearchResponse))]
[JsonSerializable(typeof(OmdbSeasonResponse))]
[JsonSerializable(typeof(OmdbEpisodeDetailResponse))]
[JsonSerializable(typeof(OmdbTitleDetailResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
internal sealed partial class OmdbJsonContext : JsonSerializerContext;
