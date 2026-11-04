using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Api.Mcp;

/// <summary>One configurable integration: the providers sharing a config section (Trakt is two).</summary>
/// <param name="Section">The config section, e.g. "Bluesky"; settings are "Section__Key".</param>
/// <param name="Providers">The providers in the section; the first is the primary.</param>
/// <param name="Roles">What the section provides: watchState, thought, ratings, search.</param>
/// <param name="Fields">The settings the UI shows for it, with their current values.</param>
public sealed record Integration(
    string Section,
    IReadOnlyList<IDataProvider> Providers,
    IReadOnlyList<string> Roles,
    IReadOnlyList<ProviderConfigField> Fields,
    bool Configured,
    bool Disabled)
{
    public IDataProvider Primary => Providers[0];
}

/// <summary>What can be configured, and how, for the settings UI and the MCP setup tools alike.</summary>
public static class IntegrationCatalog
{
    /// <summary>
    ///     The app preferences that may be changed through the config API and MCP. An explicit list, not
    ///     every property of WatchBackOptions: a new option is only writable once it is added here.
    /// </summary>
    public static readonly IReadOnlyList<string> PreferenceKeys =
    [
        "WatchBack__WatchProvider",
        "WatchBack__TimeMachineDays",
        "WatchBack__SearchEngine",
        "WatchBack__CustomSearchUrl",
        "WatchBack__SegmentedProgressBar",
        "WatchBack__EnableSentimentAnalysis",
        "WatchBack__DisabledProviders"
    ];

    // Credentials whose settings field is plain text (Trakt shows its client ID, which it receives as the
    // trakt-api-key header). Treated as secret everywhere an assistant could read or be handed them.
    private static readonly HashSet<string> s_secretTextKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Trakt__ClientId"
    };

    /// <summary>
    ///     The value the settings UI sends for a password it didn't change, meaning "use the stored one"
    ///     (see FormValueExtensions.ResolveFormValue).
    /// </summary>
    public const string StoredSecretSentinel = "__EXISTING__";

    /// <summary>Password fields and known credentials hold secrets: their values never go to an assistant.</summary>
    public static bool IsSecret(ProviderConfigField field) =>
        string.Equals(field.Type, "password", StringComparison.OrdinalIgnoreCase) || s_secretTextKeys.Contains(field.Key);

    /// <summary>
    ///     Values an assistant asked to test, safe to pass to a provider's connection test: the "use the stored
    ///     secret" sentinel is blanked, so a test against a URL the assistant chose can never carry the stored
    ///     key to it. A test of new values only uses what the caller supplied.
    /// </summary>
    public static Dictionary<string, string> TestValuesWithoutStoredSecrets(IReadOnlyDictionary<string, string> values) =>
        values.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Trim() == StoredSecretSentinel ? "" : kv.Value,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The integrations, grouped by config section, in registration order.</summary>
    public static IReadOnlyList<Integration> Build(
        IEnumerable<IWatchStateProvider> watchStateProviders,
        IEnumerable<IThoughtProvider> thoughtProviders,
        IEnumerable<IRatingsProvider> ratingsProviders,
        IEnumerable<IMediaSearchProvider> mediaSearchProviders,
        Func<string, string> envValue,
        Func<string, string, bool> isSaved,
        string disabledProviders)
    {
        HashSet<string> disabled = disabledProviders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<(IDataProvider Provider, string Role)> tagged = watchStateProviders
            .Select(p => ((IDataProvider)p, "watchState"))
            .Concat(thoughtProviders.Select(p => ((IDataProvider)p, "thought")))
            .Concat(ratingsProviders.Select(p => ((IDataProvider)p, "ratings")))
            .Concat(mediaSearchProviders.Select(p => ((IDataProvider)p, "search")));

        return tagged
            .Where(t => t.Provider.ConfigSection is not null)
            .GroupBy(t => t.Provider.ConfigSection!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                List<IDataProvider> providers = g.Select(t => t.Provider).Distinct().ToList();
                // Fields from the first provider that declares any (Trakt's are on its watch-state provider).
                IReadOnlyList<ProviderConfigField> fields = providers
                    .Select(p => p.GetConfigSchema(envValue, isSaved))
                    .FirstOrDefault(f => f is { Count: > 0 }) ?? [];
                return new Integration(g.Key, providers, g.Select(t => t.Role).Distinct().ToList(), fields,
                    providers.Any(p => p.IsConfigured), disabled.Contains(g.Key));
            })
            .ToList();
    }

    /// <summary>Every setting that may be written: the preferences and each integration's fields.</summary>
    public static HashSet<string> WritableKeys(IEnumerable<Integration> integrations) =>
        integrations.SelectMany(i => i.Fields.Select(f => f.Key))
            .Concat(PreferenceKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
