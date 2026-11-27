using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Caching;
using WatchBack.Infrastructure.Extensions;
using WatchBack.Resources;

namespace WatchBack.Infrastructure.Translators;

/// <summary>
///     Translates text with a LibreTranslate server (<c>POST /translate</c>, source language auto-detected).
///     Confirmed answers are remembered durably; failures are never remembered.
/// </summary>
public sealed class LibreTranslateTranslator(
    HttpClient http,
    IOptionsMonitor<LibreTranslateOptions> options,
    IDurableLookupCache cache,
    ILogger<LibreTranslateTranslator> logger) : ITranslator
{
    private const string DefaultBaseUrl = "http://libretranslate:5000";

    public DataProviderMetadata Metadata { get; } = new(
        "LibreTranslate",
        UiStrings.Translators_Description_LibreTranslate,
        BrandData: new BrandData("#2563EB",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>LibreTranslate</title><path d=\"M12 2a10 10 0 100 20 10 10 0 000-20zm0 2c1.4 1.6 2.3 3.7 2.5 6h-5c.2-2.3 1.1-4.4 2.5-6zm-2.5 8h5c-.2 2.3-1.1 4.4-2.5 6-1.4-1.6-2.3-3.7-2.5-6z\"/></svg>"));

    public string? ConfigSection => "LibreTranslate";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.BaseUrl);

    public async Task<TranslationResult> TranslateAsync(string text, string targetLanguage,
        CancellationToken ct = default)
    {
        string target = targetLanguage.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text) || target.Length == 0 || text.Length > TranslationCache.MaxTextLength)
        {
            return new TranslationResult(text, null, false);
        }

        LibreTranslateOptions opts = options.CurrentValue;
        string key = TranslationCache.Key("libretranslate", target, "", text);

        TranslationResult? remembered = await TranslationCache.GetAsync(cache, key, text, ct);
        if (remembered is not null)
        {
            return remembered;
        }

        try
        {
            TranslationResult result = await RequestAsync(text, target, opts, ct);
            await TranslationCache.SetAsync(cache, key, result, ct);
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
                                   ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException)
        {
            logger.LogWarning(ex, "LibreTranslate translation to {Language} failed", target);
            return new TranslationResult(text, null, false, true);
        }
    }

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        LibreTranslateOptions o = options.CurrentValue;
        return
        [
            new ProviderConfigField("LibreTranslate__BaseUrl", UiStrings.ConfigEndpoints_GetConfig_Server_URL, "text",
                DefaultBaseUrl, !string.IsNullOrEmpty(o.BaseUrl), o.BaseUrl,
                envVal("LibreTranslate__BaseUrl"), isOverridden("LibreTranslate", "BaseUrl")),
            new ProviderConfigField("LibreTranslate__ApiKey", UiStrings.ConfigEndpoints_GetConfig_API_Key, "password",
                UiStrings.Translators_Optional, !string.IsNullOrEmpty(o.ApiKey), "", "",
                isOverridden("LibreTranslate", "ApiKey"))
        ];
    }

    public Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default) =>
        CheckAsync(options.CurrentValue.BaseUrl, ct);

    public Task<ServiceHealth> TestConnectionAsync(IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct = default) =>
        CheckAsync(formValues.ResolveFormValue("LibreTranslate__BaseUrl", options.CurrentValue.BaseUrl), ct);

    public string? RevealSecret(string key) =>
        key == "LibreTranslate__ApiKey" ? options.CurrentValue.ApiKey : null;

    // The languages list is public and needs no key, so it checks the server is reachable.
    private async Task<ServiceHealth> CheckAsync(string baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ServiceHealth(false, UiStrings.Translators_NeedsUrl, DateTimeOffset.UtcNow);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            return new ServiceHealth(false, UiStrings.ConfigEndpoints_TestJellyfin_, DateTimeOffset.UtcNow);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using HttpResponseMessage response = await http.GetAsync(baseUrl.TrimEnd('/') + "/languages", cts.Token);
            return response.IsSuccessStatusCode
                ? new ServiceHealth(true, UiStrings.ConfigEndpoints_TestJellyfin_Connected, DateTimeOffset.UtcNow)
                : new ServiceHealth(false, $"HTTP {(int)response.StatusCode}", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
                                   ex is HttpRequestException or TaskCanceledException)
        {
            return new ServiceHealth(false, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    private async Task<TranslationResult> RequestAsync(string text, string target, LibreTranslateOptions opts,
        CancellationToken ct)
    {
        JsonObject body = new() { ["q"] = text, ["source"] = "auto", ["target"] = target, ["format"] = "text" };
        if (!string.IsNullOrEmpty(opts.ApiKey))
        {
            body["api_key"] = opts.ApiKey;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, $"{opts.BaseUrl.TrimEnd('/')}/translate")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        JsonNode? answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        string? translation = answer?["translatedText"]?.GetValue<string>();
        string? detected = answer?["detectedLanguage"]?["language"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(translation))
        {
            throw new InvalidOperationException("LibreTranslate returned no translation.");
        }

        bool translated = detected is not "" && detected != target &&
                          !string.Equals(translation.Trim(), text.Trim(), StringComparison.Ordinal);
        return translated
            ? new TranslationResult(translation, detected, true)
            : new TranslationResult(text, detected, false);
    }
}
