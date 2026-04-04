using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Resources;

namespace WatchBack.Api.Endpoints;

public record UserConfigFile(string Path);

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api")
            .WithTags("Configuration");

        group.MapGet("/config", GetConfig)
            .WithName("GetConfig")
            .WithSummary("Get configuration schema and current values")
            .WithDescription(
                "Returns the configuration schema for all registered integrations with current values and branding information")
            .Produces(StatusCodes.Status200OK);

        group.MapPost("/config", SaveConfig)
            .WithName("SaveConfig")
            .WithSummary("Save configuration")
            .WithDescription(
                "Persists configuration values to the user settings file. Rejects unknown configuration sections for security. Empty values are skipped.")
            .Accepts<Dictionary<string, string>>("application/json")
            .Produces(StatusCodes.Status200OK);

        group.MapDelete("/config", ResetConfig)
            .WithName("ResetConfig")
            .WithSummary("Reset config keys to environment/default values")
            .WithDescription(
                "Removes the specified keys from user-settings.json so environment variables or compiled defaults take effect again.")
            .Accepts<string[]>("application/json")
            .Produces(StatusCodes.Status200OK);

        group.MapPost("/config/reveal/{key}", RevealConfigValue)
            .WithName("RevealConfigValue")
            .WithSummary("Reveal a stored secret value")
            .WithDescription(
                "Returns the plaintext value of a password-type config field owned by a registered provider.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/status", GetStatus)
            .WithName("GetStatus")
            .WithSummary("Get health status of providers")
            .WithDescription("Returns the health status of the configured watch state provider and display name")
            .Produces(StatusCodes.Status200OK);

        group.MapPost("/test/{service}", TestService)
            .WithName("TestService")
            .WithSummary("Test service connection")
            .WithDescription(
                "Tests the connection for a registered integration using the submitted form values and returns the result")
            .Accepts<Dictionary<string, string>>("application/json")
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/themes", GetThemes)
            .WithName("GetThemes")
            .WithSummary("List available UI themes")
            .WithDescription(
                "Returns themes discovered from wwwroot/css/themes/*.css, ordered alphabetically. Label is derived from the filename.")
            .Produces(StatusCodes.Status200OK)
            .AllowAnonymous();
    }

    private static async Task<object> GetConfig(
        [FromServices] IEnumerable<IWatchStateProvider> watchStateProviders,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        [FromServices] IEnumerable<IMediaSearchProvider> mediaSearchProviders,
        [FromServices] IEnumerable<IRatingsProvider> ratingsProviders,
        IOptionsSnapshot<WatchBackOptions> watchback,
        IConfiguration configuration,
        UserConfigFile configFile,
        CancellationToken ct)
    {
        WatchBackOptions w = watchback.Value;

        // Materialize all IEnumerable provider collections upfront to avoid multiple enumeration
        List<IWatchStateProvider> watchStateProviderList = watchStateProviders.ToList();
        List<IThoughtProvider> thoughtProviderList = thoughtProviders.ToList();
        List<IRatingsProvider> ratingsProviderList = ratingsProviders.ToList();
        List<IMediaSearchProvider> mediaSearchProviderList = mediaSearchProviders.ToList();

        // Resolve env values from the live IConfiguration root (includes env vars, appsettings, etc.)
        string EnvVal(string flatKey)
        {
            return configuration[flatKey.Replace("__", ":")] ?? "";
        }

        // Read user-settings.json to determine which keys have been overridden via the UI
        Dictionary<string, Dictionary<string, string>> userSettings =
            await AuthEndpoints.ReadConfigFile(configFile.Path, ct);

        bool IsOverridden(string section, string key)
        {
            return userSettings.TryGetValue(section, out Dictionary<string, string>? s) && s.ContainsKey(key);
        }

        // Tag every provider with its interface role, then group by config section.
        // Providers sharing a section (e.g. Trakt as both watch-state and thought) merge into one card.
        IEnumerable<(IDataProvider, string)> tagged = watchStateProviderList
            .Select(p => ((IDataProvider)p, "watchState"))
            .Concat(thoughtProviderList.Select(p => ((IDataProvider)p, "thought")))
            .Concat(ratingsProviderList.Select(p => ((IDataProvider)p, "ratings")))
            .Concat(mediaSearchProviderList.Select(p => ((IDataProvider)p, "search")));

        HashSet<string> disabledProviders = w.DisabledProviders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, object> integrations = tagged
            .Where(t => t.Item1.ConfigSection is not null)
            .GroupBy(t => t.Item1.ConfigSection!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key.ToLowerInvariant(),
                g =>
                {
                    List<IDataProvider> providers = g.Select(t => t.Item1).ToList();
                    IDataProvider primary = providers[0];
                    string[] providerTypes = g.Select(t => t.Item2).Distinct().ToArray();
                    // Use fields from the first provider that declares any
                    IReadOnlyList<ProviderConfigField> fields = providers
                        .Select(p => p.GetConfigSchema(EnvVal, IsOverridden))
                        .FirstOrDefault(f => f is { Count: > 0 }) ?? (IReadOnlyList<ProviderConfigField>)[];
                    return (object)new
                    {
                        name = primary.Metadata.DisplayName,
                        logoSvg = primary.Metadata.BrandData?.LogoSvg ?? "",
                        brandColor = primary.Metadata.BrandData?.Color ?? "",
                        fields,
                        configured = providers.Any(p => p.IsConfigured),
                        providerTypes,
                        disabled = disabledProviders.Contains(g.Key)
                    };
                });

        return new
        {
            integrations,
            preferences = new
            {
                timeMachineDays = w.TimeMachineDays,
                watchProvider = w.WatchProvider,
                watchProviders = watchStateProviderList
                    .Select(p => new
                    {
                        value = p.Metadata.Name.ToLowerInvariant(),
                        label = p.Metadata.Name,
                        requiresManualInput = (p.Metadata as WatchStateDataProviderMetadata)?.RequiresManualInput ??
                                              false
                    })
                    .ToArray(),
                searchConfigured = ratingsProviderList.Cast<IDataProvider>().Any(p => p.IsConfigured),
                searchEngine = w.SearchEngine,
                customSearchUrl = w.CustomSearchUrl,
                segmentedProgressBar = w.SegmentedProgressBar,
                envValues = new Dictionary<string, string>
                {
                    ["WatchBack__TimeMachineDays"] = EnvVal("WatchBack__TimeMachineDays"),
                    ["WatchBack__WatchProvider"] = EnvVal("WatchBack__WatchProvider"),
                    ["WatchBack__SearchEngine"] = EnvVal("WatchBack__SearchEngine"),
                    ["WatchBack__CustomSearchUrl"] = EnvVal("WatchBack__CustomSearchUrl"),
                    ["WatchBack__DisabledProviders"] = EnvVal("WatchBack__DisabledProviders")
                },
                overrides = new Dictionary<string, bool>
                {
                    ["WatchBack__TimeMachineDays"] = IsOverridden("WatchBack", "TimeMachineDays"),
                    ["WatchBack__WatchProvider"] = IsOverridden("WatchBack", "WatchProvider"),
                    ["WatchBack__SearchEngine"] = IsOverridden("WatchBack", "SearchEngine"),
                    ["WatchBack__CustomSearchUrl"] = IsOverridden("WatchBack", "CustomSearchUrl"),
                    ["WatchBack__DisabledProviders"] = IsOverridden("WatchBack", "DisabledProviders")
                }
            }
        };
    }

    /// <summary>Flattens all provider collections into a single IDataProvider sequence.</summary>
    private static IEnumerable<IDataProvider> GetAllDataProviders(
        IEnumerable<IWatchStateProvider> watchStateProviders,
        IEnumerable<IThoughtProvider> thoughtProviders,
        IEnumerable<IRatingsProvider> ratingsProviders,
        IEnumerable<IMediaSearchProvider> mediaSearchProviders)
    {
        return watchStateProviders
            .Concat(thoughtProviders.Cast<IDataProvider>())
            .Concat(ratingsProviders)
            .Concat(mediaSearchProviders);
    }

    /// <summary>Derives the set of allowable config sections from registered providers plus WatchBack itself.</summary>
    private static HashSet<string> GetAllowedSections(IEnumerable<IDataProvider> providers)
    {
        HashSet<string> sections = providers
            .Select(p => p.ConfigSection)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        sections.Add("WatchBack");
        return sections;
    }

    private static async Task<IResult> SaveConfig(
        HttpContext ctx,
        [FromServices] IEnumerable<IWatchStateProvider> watchStateProviders,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        [FromServices] IEnumerable<IRatingsProvider> ratingsProviders,
        [FromServices] IEnumerable<IMediaSearchProvider> mediaSearchProviders,
        UserConfigFile configFile,
        CancellationToken ct)
    {
        Dictionary<string, string>? body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct);
        if (body is null)
        {
            return Results.BadRequest(UiStrings.ConfigEndpoints_SaveConfig_Invalid_request_body);
        }

        HashSet<string> allowedSections = GetAllowedSections(GetAllDataProviders(watchStateProviders, thoughtProviders,
            ratingsProviders, mediaSearchProviders));

        await AuthEndpoints.ConfigFileLock.WaitAsync(ct);
        try
        {
            Dictionary<string, Dictionary<string, string>> existing =
                await AuthEndpoints.ReadConfigFile(configFile.Path, ct);

            // Apply updates (key format: "Section__Key")
            foreach ((string flatKey, string value) in body)
            {
                if (string.IsNullOrEmpty(value))
                {
                    continue; // skip empty values — preserve existing
                }

                int sep = flatKey.IndexOf("__", StringComparison.Ordinal);
                if (sep < 0)
                {
                    continue;
                }

                string section = flatKey[..sep];
                string key = flatKey[(sep + 2)..];

                if (!allowedSections.Contains(section))
                {
                    continue; // reject unknown sections
                }

                if (!existing.TryGetValue(section, out Dictionary<string, string>? sectionDict))
                {
                    sectionDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    existing[section] = sectionDict;
                }

                sectionDict[key] = value;
            }

            await AuthEndpoints.WriteConfigFile(configFile.Path, existing, ct);
        }
        finally
        {
            AuthEndpoints.ConfigFileLock.Release();
        }

        return Results.Ok();
    }

    private static async Task<IResult> ResetConfig(
        HttpContext ctx,
        [FromServices] IEnumerable<IWatchStateProvider> watchStateProviders,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        [FromServices] IEnumerable<IRatingsProvider> ratingsProviders,
        [FromServices] IEnumerable<IMediaSearchProvider> mediaSearchProviders,
        UserConfigFile configFile,
        CancellationToken ct)
    {
        string[]? keys = await ctx.Request.ReadFromJsonAsync<string[]>(ct);
        if (keys == null || keys.Length == 0)
        {
            return Results.BadRequest(UiStrings.ConfigEndpoints_ResetConfig_No_keys_specified);
        }

        HashSet<string> allowedSections = GetAllowedSections(GetAllDataProviders(watchStateProviders, thoughtProviders,
            ratingsProviders, mediaSearchProviders));

        await AuthEndpoints.ConfigFileLock.WaitAsync(ct);
        try
        {
            Dictionary<string, Dictionary<string, string>> existing =
                await AuthEndpoints.ReadConfigFile(configFile.Path, ct);

            foreach (string flatKey in keys)
            {
                int sep = flatKey.IndexOf("__", StringComparison.Ordinal);
                if (sep < 0)
                {
                    continue;
                }

                string section = flatKey[..sep];
                string key = flatKey[(sep + 2)..];

                if (!allowedSections.Contains(section))
                {
                    continue;
                }

                if (existing.TryGetValue(section, out Dictionary<string, string>? sect))
                {
                    sect.Remove(key);
                }
            }

            await AuthEndpoints.WriteConfigFile(configFile.Path, existing, ct);
        }
        finally
        {
            AuthEndpoints.ConfigFileLock.Release();
        }

        return Results.Ok();
    }

    private static IResult RevealConfigValue(
        string key,
        [FromServices] IEnumerable<IWatchStateProvider> watchStateProviders,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        [FromServices] IEnumerable<IRatingsProvider> ratingsProviders,
        [FromServices] IEnumerable<IMediaSearchProvider> mediaSearchProviders)
    {
        string? value =
            GetAllDataProviders(watchStateProviders, thoughtProviders, ratingsProviders, mediaSearchProviders)
                .Select(p => p.RevealSecret(key))
                .FirstOrDefault(v => v is not null);

        return value is not null
            ? Results.Ok(new { value })
            : Results.NotFound();
    }

    private static IResult GetThemes(IWebHostEnvironment env)
    {
        string themesPath = Path.Combine(env.WebRootPath, "css", "themes");
        if (!Directory.Exists(themesPath))
        {
            return Results.Ok(Array.Empty<object>());
        }

        var themes = Directory.GetFiles(themesPath, "*.css")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new
            {
                id = name,
                label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name!.Replace('-', ' '))
            })
            .ToArray();

        return Results.Ok(themes);
    }

    private static object GetStatus(
        IOptionsSnapshot<WatchBackOptions> watchback,
        IEnumerable<IWatchStateProvider> watchStateProviders)
    {
        string configured = watchback.Value.WatchProvider;
        List<IWatchStateProvider> providers = watchStateProviders.ToList();
        IWatchStateProvider? activeProvider = providers
                                                  .FirstOrDefault(p =>
                                                      p.Metadata.Name.Equals(configured,
                                                          StringComparison.OrdinalIgnoreCase))
                                              ?? providers.FirstOrDefault();

        return new { watchProvider = activeProvider?.Metadata.Name.ToLowerInvariant() ?? configured };
    }

    private static async Task<object> TestService(
        string service,
        HttpContext ctx,
        [FromServices] IEnumerable<IWatchStateProvider> watchStateProviders,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        [FromServices] IEnumerable<IRatingsProvider> ratingsProviders,
        [FromServices] IEnumerable<IMediaSearchProvider> mediaSearchProviders,
        CancellationToken ct)
    {
        Dictionary<string, string> formValues = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct)
                                                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        IDataProvider? provider =
            GetAllDataProviders(watchStateProviders, thoughtProviders, ratingsProviders, mediaSearchProviders)
                .FirstOrDefault(p => string.Equals(p.ConfigSection, service, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            return new
            {
                ok = false,
                message =
#pragma warning disable CA1863
                    string.Format(
                        CultureInfo.InvariantCulture,
                        UiStrings.ConfigEndpoints_TestService_Unknown_service___0_,
                        service)
#pragma warning restore CA1863
            };
        }

        try
        {
            ServiceHealth health = await provider.TestConnectionAsync(formValues, ct);
            return new { ok = health.IsHealthy, message = health.Message };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }
}
