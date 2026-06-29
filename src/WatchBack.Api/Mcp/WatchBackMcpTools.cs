using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using WatchBack.Api.Endpoints;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Api.Mcp;

[McpServerToolType]
public sealed class WatchBackMcpTools(
    ISyncService syncService,
    SyncTrigger syncTrigger,
    IManualWatchStateProvider manualWatchStateProvider,
    IEnumerable<IWatchStateProvider> watchStateProviders,
    IEnumerable<IThoughtProvider> thoughtProviders,
    IEnumerable<IRatingsProvider> ratingsProviders,
    IEnumerable<IMediaSearchProvider> mediaSearchProviders,
    IOptionsSnapshot<WatchBackOptions> watchback,
    UserConfigFile configFile)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [McpServerTool]
    [Description(
        "Get the current watch state: what is currently being watched, community thought counts by source, and ratings. " +
        "Returns status ('Idle', 'Watching', or 'Error'), title, episode info if applicable, thought counts, and ratings.")]
    public async Task<string> GetWatchState(CancellationToken cancellationToken)
    {
        SyncResult result = await syncService.SyncAsync(null, cancellationToken);

        Dictionary<string, int> thoughtsBySource = result.SourceResults
            .Where(r => r.Thoughts is { Count: > 0 })
            .ToDictionary(r => r.Source, r => r.Thoughts!.Count);

        object? episode = null;
        if (result.Metadata is EpisodeContext ep)
        {
            episode = new
            {
                episodeTitle = ep.EpisodeTitle,
                seasonNumber = ep.SeasonNumber,
                episodeNumber = ep.EpisodeNumber
            };
        }

        object response = new
        {
            status = result.Status.ToString(),
            title = result.Title,
            episode,
            releaseDate = result.Metadata?.ReleaseDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            thoughtCount = result.AllThoughts.Count,
            thoughtsBySource,
            ratings = result.Ratings?.Select(r => new { source = r.Source, value = r.Value }).ToList(),
            ratingsProvider = result.RatingsProvider,
            watchProvider = result.WatchProvider,
            suppressedProvider = result.SuppressedProvider,
            suppressedTitle = result.SuppressedTitle,
            timeMachineDays = result.TimeMachineDays
        };

        return JsonSerializer.Serialize(response, s_jsonOptions);
    }

    [McpServerTool]
    [Description("Trigger an immediate sync so the watch state is refreshed right away instead of waiting for the next automatic poll.")]
    public void TriggerSync()
    {
        syncTrigger.Signal();
    }

    [McpServerTool]
    [Description(
        "Get configuration: current preferences (watch provider, time machine days, etc.) and which integrations are configured. " +
        "Also returns the user-overridden settings from user-settings.json.")]
    public async Task<string> GetConfig(CancellationToken cancellationToken)
    {
        WatchBackOptions w = watchback.Value;

        // Read only to discover which keys are overridden — never return values, as
        // user-settings.json holds plaintext API keys and the Auth password hash.
        Dictionary<string, Dictionary<string, string>> userSettings =
            await AuthEndpoints.ReadConfigFile(configFile.Path, cancellationToken);

        List<IWatchStateProvider> watchStateProviderList = watchStateProviders.ToList();

        HashSet<string> disabledProviders = w.DisabledProviders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<IDataProvider> allProviders = ConfigEndpoints.GetAllDataProviders(
            watchStateProviders, thoughtProviders, ratingsProviders, mediaSearchProviders);

        Dictionary<string, object> integrations = allProviders
            .Where(p => p.ConfigSection is not null)
            .GroupBy(p => p.ConfigSection!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key.ToLowerInvariant(),
                g => (object)new
                {
                    displayName = g.First().Metadata.DisplayName,
                    configured = g.Any(p => p.IsConfigured),
                    disabled = disabledProviders.Contains(g.Key)
                });

        // Expose only the key names that have been overridden in user-settings.json,
        // never their values. Values may contain API keys and other secrets.
        Dictionary<string, string[]> overriddenKeys = userSettings
            .Where(s => !s.Key.Equals("Auth", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                s => s.Key,
                s => s.Value.Keys.ToArray());

        object response = new
        {
            preferences = new
            {
                watchProvider = w.WatchProvider,
                availableWatchProviders = watchStateProviderList.Select(p => p.Metadata.Name.ToLowerInvariant()).ToArray(),
                timeMachineDays = w.TimeMachineDays,
                searchEngine = w.SearchEngine,
                customSearchUrl = w.CustomSearchUrl,
                segmentedProgressBar = w.SegmentedProgressBar,
                enableSentimentAnalysis = w.EnableSentimentAnalysis,
                disabledProviders = w.DisabledProviders
            },
            integrations,
            overriddenKeys
        };

        return JsonSerializer.Serialize(response, s_jsonOptions);
    }

    [McpServerTool]
    [Description(
        "Update configuration values. Keys must use Section__Key format (e.g. 'WatchBack__WatchProvider', 'Jellyfin__Url'). " +
        "Only known sections are accepted; unknown sections are silently ignored for security. " +
        "Changes are persisted to user-settings.json and take effect on the next request.")]
    public async Task<string> UpdateConfig(
        [Description("Dictionary mapping Section__Key to new value. Example: {\"WatchBack__WatchProvider\": \"jellyfin\"}")] Dictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        HashSet<string> allowedSections = ConfigEndpoints.GetAllowedSections(
            ConfigEndpoints.GetAllDataProviders(watchStateProviders, thoughtProviders, ratingsProviders, mediaSearchProviders));

        List<string> applied = [];
        List<string> rejected = [];

        await AuthEndpoints.ConfigFileLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, Dictionary<string, string>> existing =
                await AuthEndpoints.ReadConfigFile(configFile.Path, cancellationToken);

            foreach ((string flatKey, string value) in values)
            {
                int sep = flatKey.IndexOf("__", StringComparison.Ordinal);
                if (sep < 0)
                {
                    rejected.Add(flatKey);
                    continue;
                }

                string section = flatKey[..sep];
                string key = flatKey[(sep + 2)..];

                if (!allowedSections.Contains(section))
                {
                    rejected.Add(flatKey);
                    continue;
                }

                if (!existing.TryGetValue(section, out Dictionary<string, string>? sectionDict))
                {
                    sectionDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    existing[section] = sectionDict;
                }

                sectionDict[key] = value;
                applied.Add(flatKey);
            }

            await AuthEndpoints.WriteConfigFile(configFile.Path, existing, cancellationToken);
        }
        finally
        {
            AuthEndpoints.ConfigFileLock.Release();
        }

        return JsonSerializer.Serialize(new { applied, rejected }, s_jsonOptions);
    }

    [McpServerTool]
    [Description(
        "Reset configuration keys to their environment variable or compiled defaults by removing them from user-settings.json. " +
        "Keys must use Section__Key format (e.g. 'WatchBack__TimeMachineDays').")]
    public async Task<string> ResetConfig(
        [Description("Array of Section__Key keys to reset. Example: [\"WatchBack__TimeMachineDays\", \"Jellyfin__Url\"]")] string[] keys,
        CancellationToken cancellationToken)
    {
        HashSet<string> allowedSections = ConfigEndpoints.GetAllowedSections(
            ConfigEndpoints.GetAllDataProviders(watchStateProviders, thoughtProviders, ratingsProviders, mediaSearchProviders));

        List<string> reset = [];
        List<string> rejected = [];

        await AuthEndpoints.ConfigFileLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, Dictionary<string, string>> existing =
                await AuthEndpoints.ReadConfigFile(configFile.Path, cancellationToken);

            foreach (string flatKey in keys)
            {
                int sep = flatKey.IndexOf("__", StringComparison.Ordinal);
                if (sep < 0)
                {
                    rejected.Add(flatKey);
                    continue;
                }

                string section = flatKey[..sep];
                string key = flatKey[(sep + 2)..];

                if (!allowedSections.Contains(section))
                {
                    rejected.Add(flatKey);
                    continue;
                }

                if (existing.TryGetValue(section, out Dictionary<string, string>? sect))
                {
                    sect.Remove(key);
                }

                reset.Add(flatKey);
            }

            await AuthEndpoints.WriteConfigFile(configFile.Path, existing, cancellationToken);
        }
        finally
        {
            AuthEndpoints.ConfigFileLock.Release();
        }

        return JsonSerializer.Serialize(new { reset, rejected }, s_jsonOptions);
    }

    [McpServerTool]
    [Description(
        "Set the manual watch state to a specific movie or TV episode. Use this when the automatic watch state provider is not available " +
        "or you want to override what is currently being watched. Pass external IDs (e.g. imdbId, tmdbId) when available for better search accuracy.")]
    public string SetManualWatchState(
        [Description("Title of the movie or TV show")] string title,
        [Description("Release date in ISO 8601 format (e.g. '2013-09-22'). Optional.")] DateTimeOffset? releaseDate,
        [Description("Episode title. Provide this (along with seasonNumber and episodeNumber) to set a TV episode.")] string? episodeTitle,
        [Description("Season number (required when setting a TV episode)")] short? seasonNumber,
        [Description("Episode number (required when setting a TV episode)")] short? episodeNumber,
        [Description("External IDs for the media, e.g. {\"imdbId\": \"tt0903747\", \"tmdbId\": \"1396\"}. Optional.")] Dictionary<string, string>? externalIds)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return JsonSerializer.Serialize(new { ok = false, error = "Title is required." }, s_jsonOptions);
        }

        MediaContext context;
        if (episodeTitle is not null && seasonNumber.HasValue && episodeNumber.HasValue)
        {
            context = new EpisodeContext(title.Trim(), releaseDate, episodeTitle, seasonNumber.Value, episodeNumber.Value, externalIds);
        }
        else
        {
            context = new MediaContext(title.Trim(), releaseDate, externalIds);
        }

        manualWatchStateProvider.SetCurrentContext(context);
        return JsonSerializer.Serialize(new { ok = true, title = context.Title }, s_jsonOptions);
    }

    [McpServerTool]
    [Description("Clear the manual watch state, returning the watch provider to its automatic state (idle or whatever the configured provider reports).")]
    public void ClearManualWatchState()
    {
        manualWatchStateProvider.SetCurrentContext(null);
    }
}
