using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Api.Endpoints;

public record UserConfigFile(string Path);

public static class ConfigEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private static readonly HashSet<string> s_allowedConfigSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jellyfin", "Trakt", "Bluesky", "Reddit", "WatchBack"
    };

    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .WithTags("Configuration");

        group.MapGet("/config", GetConfig)
            .WithName("GetConfig")
            .WithSummary("Get configuration schema and current values")
            .WithDescription("Returns the configuration schema for all integrations (Jellyfin, Trakt, Bluesky, Reddit) with current values and branding information")
            .Produces(StatusCodes.Status200OK);

        group.MapPost("/config", SaveConfig)
            .WithName("SaveConfig")
            .WithSummary("Save configuration")
            .WithDescription("Persists configuration values to the user settings file. Rejects unknown configuration sections for security. Empty values are skipped.")
            .Accepts<Dictionary<string, string>>("application/json")
            .Produces(StatusCodes.Status200OK);

        group.MapDelete("/config", ResetConfig)
            .WithName("ResetConfig")
            .WithSummary("Reset config keys to environment/default values")
            .WithDescription("Removes the specified keys from user-settings.json so environment variables or compiled defaults take effect again.")
            .Accepts<string[]>("application/json")
            .Produces(StatusCodes.Status200OK);

        group.MapPost("/config/reveal/{key}", RevealConfigValue)
            .WithName("RevealConfigValue")
            .WithSummary("Reveal a stored secret value")
            .WithDescription("Returns the plaintext value of a password-type config field. Only whitelisted keys are accessible.")
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
            .WithDescription("Tests the connection to a specified thought provider (reddit, trakt, bluesky) and returns the result")
            .Accepts<Dictionary<string, string>>("application/json")
            .Produces(StatusCodes.Status200OK);

        group.MapGet("/themes", GetThemes)
            .WithName("GetThemes")
            .WithSummary("List available UI themes")
            .WithDescription("Returns themes discovered from wwwroot/css/themes/*.css, ordered alphabetically. Label is derived from the filename.")
            .Produces(StatusCodes.Status200OK)
            .AllowAnonymous();
    }

    private static async Task<object> GetConfig(
        IOptionsSnapshot<JellyfinOptions> jellyfin,
        IOptionsSnapshot<TraktOptions> trakt,
        IOptionsSnapshot<BlueskyOptions> bluesky,
        IOptionsSnapshot<RedditOptions> reddit,
        IOptionsSnapshot<WatchBackOptions> watchback,
        IEnumerable<IWatchStateProvider> watchStateProviders,
        IEnumerable<IThoughtProvider> thoughtProviders,
        UserConfigFile configFile,
        CancellationToken ct)
    {
        var j = jellyfin.Value;
        var t = trakt.Value;
        var b = bluesky.Value;
        var r = reddit.Value;
        var w = watchback.Value;

        // Env-only config: baseline values before user-settings.json overrides
        var envCfg = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        string EnvVal(string flatKey) => envCfg[flatKey.Replace("__", ":")] ?? "";

        // Read user-settings.json to determine which keys have been overridden via the UI
        var userSettings = await AuthEndpoints.ReadConfigFile(configFile.Path, ct);
        bool IsOverriddenInUserSettings(string section, string key) =>
            userSettings.TryGetValue(section, out var s) && s.ContainsKey(key);

        // Build brand lookup — thought providers take precedence for shared names (e.g. Trakt uses official red)
        var brandByName = new Dictionary<string, BrandData>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in watchStateProviders)
            if (p.Metadata.BrandData != null)
                brandByName[p.Metadata.Name] = p.Metadata.BrandData;
        foreach (var p in thoughtProviders)
            if (p.Metadata.BrandData != null)
                brandByName[p.Metadata.Name] = p.Metadata.BrandData;

        return new
        {
            integrations = new Dictionary<string, object>
            {
                ["jellyfin"] = new
                {
                    name = "Jellyfin",
                    logoSvg = brandByName.GetValueOrDefault("Jellyfin")?.LogoSvg ?? "",
                    brandColor = brandByName.GetValueOrDefault("Jellyfin")?.Color ?? "",
                    fields = new[]
                    {
                        new { key = "Jellyfin__BaseUrl", label = "Server URL", type = "text", placeholder = "http://jellyfin:8096", hasValue = !string.IsNullOrEmpty(j.BaseUrl) && j.BaseUrl != "http://jellyfin:8096", value = j.BaseUrl ?? "", envValue = EnvVal("Jellyfin__BaseUrl"), isOverridden = IsOverriddenInUserSettings("Jellyfin", "BaseUrl") },
                        new { key = "Jellyfin__ApiKey", label = "API Key", type = "password", placeholder = "Required", hasValue = !string.IsNullOrEmpty(j.ApiKey), value = "", envValue = "", isOverridden = IsOverriddenInUserSettings("Jellyfin", "ApiKey") }
                    },
                    configured = !string.IsNullOrEmpty(j.ApiKey)
                },
                ["trakt"] = new
                {
                    name = "Trakt.tv",
                    logoSvg = brandByName.GetValueOrDefault("Trakt")?.LogoSvg ?? "",
                    brandColor = brandByName.GetValueOrDefault("Trakt")?.Color ?? "",
                    fields = new[]
                    {
                        new { key = "Trakt__ClientId", label = "Client ID", type = "text", placeholder = "Optional (for comments)", hasValue = !string.IsNullOrEmpty(t.ClientId), value = t.ClientId ?? "", envValue = EnvVal("Trakt__ClientId"), isOverridden = IsOverriddenInUserSettings("Trakt", "ClientId") },
                        new { key = "Trakt__AccessToken", label = "Access Token (OAuth)", type = "password", placeholder = "Optional (for private profile)", hasValue = !string.IsNullOrEmpty(t.AccessToken), value = "", envValue = "", isOverridden = IsOverriddenInUserSettings("Trakt", "AccessToken") },
                        new { key = "Trakt__Username", label = "Username", type = "text", placeholder = "Optional (public profile)", hasValue = !string.IsNullOrEmpty(t.Username), value = t.Username ?? "", envValue = EnvVal("Trakt__Username"), isOverridden = IsOverriddenInUserSettings("Trakt", "Username") }
                    },
                    configured = !string.IsNullOrEmpty(t.ClientId) || !string.IsNullOrEmpty(t.Username)
                },
                ["bluesky"] = new
                {
                    name = "Bluesky",
                    logoSvg = brandByName.GetValueOrDefault("Bluesky")?.LogoSvg ?? "",
                    brandColor = brandByName.GetValueOrDefault("Bluesky")?.Color ?? "",
                    fields = new[]
                    {
                        new { key = "Bluesky__Handle", label = "Handle/Email", type = "text", placeholder = "you.bsky.social", hasValue = !string.IsNullOrEmpty(b.Handle), value = b.Handle ?? "", envValue = EnvVal("Bluesky__Handle"), isOverridden = IsOverriddenInUserSettings("Bluesky", "Handle") },
                        new { key = "Bluesky__AppPassword", label = "App Password", type = "password", placeholder = "xxxx-xxxx-xxxx-xxxx", hasValue = !string.IsNullOrEmpty(b.AppPassword), value = "", envValue = "", isOverridden = IsOverriddenInUserSettings("Bluesky", "AppPassword") }
                    },
                    configured = !string.IsNullOrEmpty(b.Handle) && !string.IsNullOrEmpty(b.AppPassword)
                },
                ["reddit"] = new
                {
                    name = "Reddit",
                    logoSvg = brandByName.GetValueOrDefault("Reddit")?.LogoSvg ?? "",
                    brandColor = brandByName.GetValueOrDefault("Reddit")?.Color ?? "",
                    fields = new[]
                    {
                        new { key = "Reddit__MaxThreads", label = "Max Threads", type = "number", placeholder = "3", hasValue = true, value = r.MaxThreads.ToString(System.Globalization.CultureInfo.InvariantCulture), envValue = EnvVal("Reddit__MaxThreads"), isOverridden = IsOverriddenInUserSettings("Reddit", "MaxThreads") },
                        new { key = "Reddit__MaxComments", label = "Max Comments", type = "number", placeholder = "250", hasValue = true, value = r.MaxComments.ToString(System.Globalization.CultureInfo.InvariantCulture), envValue = EnvVal("Reddit__MaxComments"), isOverridden = IsOverriddenInUserSettings("Reddit", "MaxComments") }
                    },
                    configured = true
                }
            },
            preferences = new
            {
                timeMachineDays = w.TimeMachineDays,
                watchProvider = w.WatchProvider,
                watchProviders = watchStateProviders
                    .Select(p => new { value = p.Metadata.Name.ToLowerInvariant(), label = p.Metadata.Name })
                    .ToArray(),
                searchEngine = w.SearchEngine,
                customSearchUrl = w.CustomSearchUrl,
                envValues = new Dictionary<string, string>
                {
                    ["WatchBack__TimeMachineDays"] = EnvVal("WatchBack__TimeMachineDays"),
                    ["WatchBack__WatchProvider"] = EnvVal("WatchBack__WatchProvider"),
                    ["WatchBack__SearchEngine"] = EnvVal("WatchBack__SearchEngine"),
                    ["WatchBack__CustomSearchUrl"] = EnvVal("WatchBack__CustomSearchUrl"),
                },
                overrides = new Dictionary<string, bool>
                {
                    ["WatchBack__TimeMachineDays"] = IsOverriddenInUserSettings("WatchBack", "TimeMachineDays"),
                    ["WatchBack__WatchProvider"] = IsOverriddenInUserSettings("WatchBack", "WatchProvider"),
                    ["WatchBack__SearchEngine"] = IsOverriddenInUserSettings("WatchBack", "SearchEngine"),
                    ["WatchBack__CustomSearchUrl"] = IsOverriddenInUserSettings("WatchBack", "CustomSearchUrl"),
                },
            }
        };
    }

    private static async Task<IResult> SaveConfig(
        HttpContext ctx,
        UserConfigFile configFile,
        CancellationToken ct)
    {
        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct);
        if (body == null)
            return Results.BadRequest("Invalid request body");

        await AuthEndpoints.ConfigFileLock.WaitAsync(ct);
        try
        {
            var existing = await AuthEndpoints.ReadConfigFile(configFile.Path, ct);

            // Apply updates (key format: "Section__Key")
            foreach (var (flatKey, value) in body)
            {
                if (string.IsNullOrEmpty(value))
                    continue; // skip empty values — preserve existing

                var sep = flatKey.IndexOf("__", StringComparison.Ordinal);
                if (sep < 0)
                    continue;

                var section = flatKey[..sep];
                var key = flatKey[(sep + 2)..];

                if (!s_allowedConfigSections.Contains(section))
                    continue; // reject unknown sections

                if (!existing.ContainsKey(section))
                    existing[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                existing[section][key] = value;
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
        UserConfigFile configFile,
        CancellationToken ct)
    {
        var keys = await ctx.Request.ReadFromJsonAsync<string[]>(ct);
        if (keys == null || keys.Length == 0)
            return Results.BadRequest("No keys specified");

        await AuthEndpoints.ConfigFileLock.WaitAsync(ct);
        try
        {
            var existing = await AuthEndpoints.ReadConfigFile(configFile.Path, ct);

            foreach (var flatKey in keys)
            {
                var sep = flatKey.IndexOf("__", StringComparison.Ordinal);
                if (sep < 0) continue;

                var section = flatKey[..sep];
                var key = flatKey[(sep + 2)..];

                if (!s_allowedConfigSections.Contains(section)) continue;

                if (existing.TryGetValue(section, out var sect))
                    sect.Remove(key);
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
        IOptionsSnapshot<JellyfinOptions> jellyfin,
        IOptionsSnapshot<TraktOptions> trakt,
        IOptionsSnapshot<BlueskyOptions> bluesky)
    {
        var value = key switch
        {
            "Jellyfin__ApiKey"      => jellyfin.Value.ApiKey,
            "Trakt__AccessToken"    => trakt.Value.AccessToken,
            "Bluesky__AppPassword"  => bluesky.Value.AppPassword,
            _                       => null
        };

        return value is not null
            ? Results.Ok(new { value })
            : Results.NotFound();
    }

    private static IResult GetThemes(IWebHostEnvironment env)
    {
        var themesPath = Path.Combine(env.WebRootPath, "css", "themes");
        if (!Directory.Exists(themesPath))
            return Results.Ok(Array.Empty<object>());

        var themes = Directory.GetFiles(themesPath, "*.css")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new
            {
                id = name,
                label = string.Join(' ', name!.Split('-')
                    .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w))
            })
            .ToArray();

        return Results.Ok(themes);
    }

    private static object GetStatus(
        IOptionsSnapshot<WatchBackOptions> watchback,
        IEnumerable<IWatchStateProvider> watchStateProviders)
    {
        var configured = watchback.Value.WatchProvider;
        var activeProvider = watchStateProviders
            .FirstOrDefault(p => p.Metadata.Name.Equals(configured, StringComparison.OrdinalIgnoreCase))
            ?? watchStateProviders.FirstOrDefault();

        return new
        {
            watchProvider = activeProvider?.Metadata.Name.ToLowerInvariant() ?? configured
        };
    }

    private static async Task<object> TestService(
        string service,
        HttpContext ctx,
        IHttpClientFactory httpClientFactory,
        IOptionsSnapshot<TraktOptions> traktOpts,
        IOptionsSnapshot<JellyfinOptions> jellyfinOpts,
        IOptionsSnapshot<BlueskyOptions> blueskyOpts,
        CancellationToken ct)
    {
        // Read credentials directly from the request body — bypasses IOptionsSnapshot timing
        // issues so we test what's in the form right now. Password fields that weren't changed
        // are sent as "__EXISTING__"; we fall back to the stored option value for those.
        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>(ct)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Resolve(string key, string? fallback)
        {
            var v = body.GetValueOrDefault(key) ?? string.Empty;
            return v == "__EXISTING__" ? (fallback ?? string.Empty) : v;
        }

        try
        {
            using var http = httpClientFactory.CreateClient();

            return service.ToLowerInvariant() switch
            {
                "trakt" => await TestTrakt(http,
                    Resolve("Trakt__ClientId", traktOpts.Value.ClientId), ct),
                "jellyfin" => await TestJellyfin(http,
                    Resolve("Jellyfin__BaseUrl", jellyfinOpts.Value.BaseUrl),
                    Resolve("Jellyfin__ApiKey", jellyfinOpts.Value.ApiKey), ct),
                "bluesky" => await TestBluesky(http,
                    Resolve("Bluesky__Handle", blueskyOpts.Value.Handle),
                    Resolve("Bluesky__AppPassword", blueskyOpts.Value.AppPassword), ct),
                "reddit" => (object)new { ok = true, message = "Connected" },
                _ => new { ok = false, message = $"Unknown service: {service}" }
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private static async Task<object> TestTrakt(HttpClient http, string clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId))
            return new { ok = false, message = "No Client ID configured" };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.trakt.tv/shows/trending?limit=1");
        req.Headers.TryAddWithoutValidation("User-Agent", "WatchBack/1.0");
        req.Headers.Add("trakt-api-version", "2");
        req.Headers.Add("trakt-api-key", clientId);
        var res = await http.SendAsync(req, cts.Token);

        if (res.StatusCode == System.Net.HttpStatusCode.OK)
            return new { ok = true, message = "Connected" };

        return new { ok = false, message = $"HTTP {(int)res.StatusCode}" };
    }

    private static async Task<object> TestJellyfin(HttpClient http, string baseUrl, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
            return new { ok = false, message = "Server URL and API Key required" };

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "http" && parsed.Scheme != "https"))
            return new { ok = false, message = "Invalid URL — must be http:// or https://" };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var url = baseUrl.TrimEnd('/') + "/System/Info";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Emby-Authorization", $"MediaBrowser Token=\"{apiKey}\"");
        var res = await http.SendAsync(req, cts.Token);

        if (!res.IsSuccessStatusCode)
            return new { ok = false, message = $"HTTP {(int)res.StatusCode}" };

        try
        {
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
            // Only extract the Version string — ignore everything else
            var version = doc.RootElement.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()?[..Math.Min(v.GetString()!.Length, 32)]
                : null;
            var message = version != null ? $"Jellyfin {version}" : "Connected";
            return new { ok = true, message };
        }
        catch
        {
            return new { ok = true, message = "Connected" };
        }
    }

    private static async Task<object> TestBluesky(HttpClient http, string handle, string appPassword, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(handle))
            return new { ok = false, message = "Handle required" };

        if (string.IsNullOrEmpty(appPassword))
            return new { ok = true, message = "Handle set (no app password to verify)" };

        var req = new HttpRequestMessage(HttpMethod.Post, "https://bsky.social/xrpc/com.atproto.server.createSession");
        req.Content = System.Net.Http.Json.JsonContent.Create(new { identifier = handle, password = appPassword });
        var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode
            ? new { ok = true, message = "Connected" }
            : new { ok = false, message = res.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "Invalid credentials" : $"HTTP {(int)res.StatusCode}" };
    }
}