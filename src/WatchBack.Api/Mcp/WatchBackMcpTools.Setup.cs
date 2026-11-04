using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

using ModelContextProtocol.Server;

using WatchBack.Api.Endpoints;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Api.Mcp;

// The tools an assistant uses to walk someone through setting WatchBack up. Secrets (password fields)
// never leave WatchBack through them: the assistant gets "has a value" and a link to the settings
// panel where the person pastes the secret themselves.
public sealed partial class WatchBackMcpTools
{
    [McpServerTool]
    [Description(
        "List everything that can be set up: the watch providers (where WatchBack sees what is playing), the " +
        "discussion and ratings sources, and the app preferences. For each integration: whether it is configured " +
        "and enabled, its settings (key, label, type, whether it is secret, whether it has a value), and a " +
        "settingsLink that opens it in WatchBack. Secret values are never returned. A value saved in WatchBack " +
        "overrides the environment variable of the same name. Start a setup walkthrough here.")]
    public async Task<string> DescribeIntegrations(CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> integrations, Dictionary<string, Dictionary<string, string>> saved) =
            await LoadIntegrationsAsync(cancellationToken);

        object response = new
        {
            watchProvider = new
            {
                current = watchback.Value.WatchProvider,
                options = watchStateProviders.Select(p => p.Metadata.Name.ToLowerInvariant()).ToArray(),
                setting = "WatchBack__WatchProvider"
            },
            preferences = IntegrationCatalog.PreferenceKeys.Select(key => new
            {
                key,
                value = PreferenceValue(key),
                savedInWatchBack = IsSaved(saved, key),
                environmentValue = EnvironmentValue(key)
            }),
            integrations = integrations.Select(i => new
            {
                section = i.Section,
                name = i.Primary.Metadata.DisplayName,
                description = i.Primary.Metadata.Description,
                roles = i.Roles.Select(RoleName).Distinct().ToArray(),
                configured = i.Configured,
                enabled = !i.Disabled,
                settingsLink = SettingsLink(i.Section),
                settings = i.Fields.Select(f =>
                {
                    bool secret = IntegrationCatalog.IsSecret(f);
                    return new
                    {
                        key = f.Key,
                        label = f.Label,
                        type = f.Type,
                        placeholder = f.Placeholder,
                        secret,
                        hasValue = f.HasValue,
                        value = secret ? null : f.Value,
                        savedInWatchBack = IsSaved(saved, f.Key),
                        setByEnvironment = !string.IsNullOrEmpty(EnvironmentValue(f.Key)),
                        environmentValue = secret ? null : NullIfEmpty(EnvironmentValue(f.Key))
                    };
                })
            })
        };

        return JsonSerializer.Serialize(response, s_jsonOptions);
    }

    [McpServerTool]
    [Description(
        "Test an integration's connection. With no values, tests the saved settings: call it once after the person " +
        "saves, rather than repeatedly (some services, like Bluesky, allow only a few sign-ins a day). With values " +
        "(Section__Key to value), tests only those, never combined with stored secrets; never put a secret in values " +
        "unless the person pasted it into this chat themselves.")]
    public async Task<string> TestIntegration(
        [Description("The integration's section, e.g. \"Jellyfin\" (from describe_integrations).")] string section,
        [Description("Optional unsaved values to test, e.g. {\"Jellyfin__BaseUrl\": \"http://jellyfin:8096\"}.")]
        Dictionary<string, string>? values,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> integrations, _) = await LoadIntegrationsAsync(cancellationToken);
        Integration? integration = Find(integrations, section);
        if (integration is null)
        {
            return Error($"Unknown integration '{section}'. Known: {string.Join(", ", integrations.Select(i => i.Section))}.");
        }

        try
        {
            // The saved settings are tested the way a sync uses them (Bluesky reuses its session); only new
            // values need a fresh sign-in.
            ServiceHealth health = values is { Count: > 0 }
                ? await integration.Primary.TestConnectionAsync(
                    IntegrationCatalog.TestValuesWithoutStoredSecrets(values), cancellationToken)
                : await integration.Primary.GetServiceHealthAsync(cancellationToken);
            return JsonSerializer.Serialize(new
            {
                ok = health.IsHealthy,
                message = health.Message,
                tested = values is { Count: > 0 } ? "the given values" : "the saved settings"
            }, s_jsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return JsonSerializer.Serialize(new { ok = false, message = ex.Message }, s_jsonOptions);
        }
    }

    [McpServerTool]
    [Description(
        "Turn an integration on or off. Off means WatchBack skips it during sync; its settings are kept.")]
    public async Task<string> SetProviderEnabled(
        [Description("The integration's section, e.g. \"Lemmy\".")] string section,
        [Description("True to turn it on, false to turn it off.")] bool enabled,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> integrations, _) = await LoadIntegrationsAsync(cancellationToken);
        Integration? integration = Find(integrations, section);
        if (integration is null)
        {
            return Error($"Unknown integration '{section}'. Known: {string.Join(", ", integrations.Select(i => i.Section))}.");
        }

        List<string> disabled = watchback.Value.DisabledProviders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !s.Equals(integration.Section, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!enabled)
        {
            disabled.Add(integration.Section);
        }

        await WriteSettingsAsync(new Dictionary<string, string?> { ["WatchBack__DisabledProviders"] = string.Join(",", disabled) },
            cancellationToken);
        return JsonSerializer.Serialize(new { ok = true, section = integration.Section, enabled }, s_jsonOptions);
    }

    [McpServerTool]
    [Description(
        "Get a link that opens an integration's settings in WatchBack, with its first empty secret field focused. " +
        "Give this to the person for anything secret (API keys, passwords, tokens) so they paste it into WatchBack " +
        "directly instead of into the chat; then call test_integration once they say it's saved.")]
    public async Task<string> GetSettingsLink(
        [Description("The integration's section, e.g. \"Bluesky\".")] string section,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> integrations, _) = await LoadIntegrationsAsync(cancellationToken);
        Integration? integration = Find(integrations, section);
        return integration is null
            ? Error($"Unknown integration '{section}'. Known: {string.Join(", ", integrations.Select(i => i.Section))}.")
            : JsonSerializer.Serialize(new { section = integration.Section, link = SettingsLink(integration.Section) }, s_jsonOptions);
    }

    [McpServerTool]
    [Description(
        "Save settings. Keys use Section__Key format (e.g. 'WatchBack__WatchProvider', 'Jellyfin__BaseUrl'); only the " +
        "keys describe_integrations lists are accepted, and each rejected key comes back with the reason. Values " +
        "saved here override environment variables. For secrets, prefer get_settings_link so the person pastes " +
        "them into WatchBack; save a secret here only if they chose to paste it into this chat.")]
    public async Task<string> UpdateConfig(
        [Description("Dictionary mapping Section__Key to new value. Example: {\"WatchBack__WatchProvider\": \"jellyfin\"}")]
        Dictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> integrations, _) = await LoadIntegrationsAsync(cancellationToken);
        HashSet<string> writable = IntegrationCatalog.WritableKeys(integrations);

        Dictionary<string, string?> accepted = new(StringComparer.OrdinalIgnoreCase);
        List<object> rejected = [];
        foreach ((string key, string value) in values)
        {
            string? reason = RejectionReason(key, value, writable, integrations);
            if (reason is null)
            {
                accepted[key] = value;
            }
            else
            {
                rejected.Add(new { key, reason });
            }
        }

        if (accepted.Count > 0)
        {
            await WriteSettingsAsync(accepted, cancellationToken);
        }

        return JsonSerializer.Serialize(new { applied = accepted.Keys, rejected }, s_jsonOptions);
    }

    [McpServerTool]
    [Description(
        "Reset settings to their environment variable or built-in defaults by removing the values saved in WatchBack. " +
        "Keys use Section__Key format (e.g. 'WatchBack__TimeMachineDays').")]
    public async Task<string> ResetConfig(
        [Description("Array of Section__Key keys to reset. Example: [\"WatchBack__TimeMachineDays\", \"Jellyfin__BaseUrl\"]")]
        string[] keys,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<Integration> integrations, _) = await LoadIntegrationsAsync(cancellationToken);
        HashSet<string> writable = IntegrationCatalog.WritableKeys(integrations);

        List<string> reset = keys.Where(writable.Contains).ToList();
        List<object> rejected = keys.Where(k => !writable.Contains(k))
            .Select(k => (object)new { key = k, reason = UnknownKeyReason(k, integrations) })
            .ToList();

        if (reset.Count > 0)
        {
            await WriteSettingsAsync(reset.ToDictionary(k => k, _ => (string?)null), cancellationToken);
        }

        return JsonSerializer.Serialize(new { reset, rejected }, s_jsonOptions);
    }

    private async Task<(IReadOnlyList<Integration>, Dictionary<string, Dictionary<string, string>>)>
        LoadIntegrationsAsync(CancellationToken ct)
    {
        Dictionary<string, Dictionary<string, string>> saved =
            await AuthEndpoints.ReadConfigFile(configFile.Path, ct);
        IReadOnlyList<Integration> integrations = IntegrationCatalog.Build(watchStateProviders, thoughtProviders,
            ratingsProviders, mediaSearchProviders, EnvironmentValue,
            (section, key) => saved.TryGetValue(section, out Dictionary<string, string>? s) && s.ContainsKey(key),
            watchback.Value.DisabledProviders);
        return (integrations, saved);
    }

    // Writes (or, for a null value, removes) settings in user-settings.json, under the shared file lock.
    private async Task WriteSettingsAsync(IReadOnlyDictionary<string, string?> settings, CancellationToken ct)
    {
        await AuthEndpoints.ConfigFileLock.WaitAsync(ct);
        try
        {
            Dictionary<string, Dictionary<string, string>> existing =
                await AuthEndpoints.ReadConfigFile(configFile.Path, ct);
            foreach ((string flatKey, string? value) in settings)
            {
                (string section, string key) = Split(flatKey);
                if (!existing.TryGetValue(section, out Dictionary<string, string>? sectionDict))
                {
                    sectionDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    existing[section] = sectionDict;
                }

                if (value is null)
                {
                    sectionDict.Remove(key);
                }
                else
                {
                    sectionDict[key] = value;
                }
            }

            await AuthEndpoints.WriteConfigFile(configFile.Path, existing, ct);
        }
        finally
        {
            AuthEndpoints.ConfigFileLock.Release();
        }
    }

    private string? RejectionReason(string key, string value, HashSet<string> writable,
        IReadOnlyList<Integration> integrations)
    {
        if (!writable.Contains(key))
        {
            return UnknownKeyReason(key, integrations);
        }

        return key.ToUpperInvariant() switch
        {
            "WATCHBACK__WATCHPROVIDER" when !watchStateProviders.Any(p =>
                    p.Metadata.Name.Equals(value, StringComparison.OrdinalIgnoreCase)) =>
                $"Not a watch provider. Options: {string.Join(", ", watchStateProviders.Select(p => p.Metadata.Name.ToLowerInvariant()))}.",
            "WATCHBACK__TIMEMACHINEDAYS" when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int days) || days is < 0 or > 365 => "Must be a whole number of days from 0 to 365.",
            "WATCHBACK__SEGMENTEDPROGRESSBAR" or "WATCHBACK__ENABLESENTIMENTANALYSIS" when !bool.TryParse(value, out _) =>
                "Must be true or false.",
            _ => null
        };
    }

    private static string UnknownKeyReason(string key, IReadOnlyList<Integration> integrations)
    {
        if (!key.Contains("__", StringComparison.Ordinal))
        {
            return "Keys use Section__Key format, e.g. Jellyfin__BaseUrl.";
        }

        (string section, _) = Split(key);
        if (section.Equals("WatchBack", StringComparison.OrdinalIgnoreCase))
        {
            return $"Not a WatchBack preference. Preferences: {string.Join(", ", IntegrationCatalog.PreferenceKeys)}.";
        }

        Integration? integration = Find(integrations, section);
        return integration is null
            ? $"Unknown section '{section}'. Integrations: {string.Join(", ", integrations.Select(i => i.Section))}."
            : $"Not a {integration.Section} setting. Its settings: {string.Join(", ", integration.Fields.Select(f => f.Key))}.";
    }

    private string SettingsLink(string section)
    {
        string fragment = $"#settings/{section.ToLowerInvariant()}";
        HttpRequest? request = httpContextAccessor.HttpContext?.Request;
        return request is null
            ? "/" + fragment
            : $"{request.Scheme}://{request.Host}{request.PathBase}/{fragment}";
    }

    private object? PreferenceValue(string key)
    {
        Core.Options.WatchBackOptions w = watchback.Value;
        return Split(key).Key switch
        {
            "WatchProvider" => w.WatchProvider,
            "TimeMachineDays" => w.TimeMachineDays,
            "SearchEngine" => w.SearchEngine,
            "CustomSearchUrl" => w.CustomSearchUrl,
            "SegmentedProgressBar" => w.SegmentedProgressBar,
            "EnableSentimentAnalysis" => w.EnableSentimentAnalysis,
            "DisabledProviders" => w.DisabledProviders,
            _ => null
        };
    }

    private static string RoleName(string role) => role switch
    {
        "watchState" => "watching",
        "thought" => "discussion",
        "ratings" => "ratings",
        "search" => "search",
        _ => role
    };

    // Environment variables use the same Section__Key names as the settings.
    private static string EnvironmentValue(string flatKey) => Environment.GetEnvironmentVariable(flatKey) ?? "";

    private static bool IsSaved(Dictionary<string, Dictionary<string, string>> saved, string flatKey)
    {
        (string section, string key) = Split(flatKey);
        return saved.TryGetValue(section, out Dictionary<string, string>? s) && s.ContainsKey(key);
    }

    private static Integration? Find(IReadOnlyList<Integration> integrations, string section) =>
        integrations.FirstOrDefault(i => i.Section.Equals(section.Trim(), StringComparison.OrdinalIgnoreCase));

    private static (string Section, string Key) Split(string flatKey)
    {
        int sep = flatKey.IndexOf("__", StringComparison.Ordinal);
        return sep < 0 ? (flatKey, "") : (flatKey[..sep], flatKey[(sep + 2)..]);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message }, s_jsonOptions);
}
