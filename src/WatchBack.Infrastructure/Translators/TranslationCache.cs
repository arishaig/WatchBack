using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using WatchBack.Core.Interfaces;
using WatchBack.Infrastructure.Caching;

namespace WatchBack.Infrastructure.Translators;

/// <summary>
///     Durable memory of confirmed translations, shared by every translator: a translated text, or "already in
///     the target language". A failure is never remembered.
/// </summary>
internal static class TranslationCache
{
    internal const string Kind = "translation";

    /// <summary>Longer texts are left alone: a small model loses the thread, and the request would be slow.</summary>
    internal const int MaxTextLength = 4000;

    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);

    /// <param name="provider">The translator's name: a different engine gives a different translation.</param>
    /// <param name="variant">Anything else that changes the output, such as an Ollama model.</param>
    internal static string Key(string provider, string target, string variant, string text) =>
        $"translate:{provider}:{target}:{variant}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";

    internal static async Task<TranslationResult?> GetAsync(IDurableLookupCache cache, string key, string original,
        CancellationToken ct)
    {
        LookupAnswer? remembered = await cache.GetAsync(key, ct);
        return remembered is { Value: { } stored } && TryRead(stored, original, out TranslationResult? hit) ? hit : null;
    }

    internal static Task SetAsync(IDurableLookupCache cache, string key, TranslationResult result,
        CancellationToken ct) =>
        cache.SetAsync(key, Kind,
            new JsonObject { ["d"] = result.DetectedLanguage, ["t"] = result.WasTranslated ? result.Text : null }
                .ToJsonString(), Lifetime, ct);

    private static bool TryRead(string stored, string original, out TranslationResult result)
    {
        result = null!;
        try
        {
            JsonNode? node = JsonNode.Parse(stored);
            string? detected = node?["d"]?.GetValue<string>();
            string? translation = node?["t"]?.GetValue<string>();
            result = translation is null
                ? new TranslationResult(original, detected, false)
                : new TranslationResult(translation, detected, true);
            return node is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
