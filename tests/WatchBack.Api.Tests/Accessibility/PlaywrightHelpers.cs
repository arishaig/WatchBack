using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

namespace WatchBack.Api.Tests.Accessibility;

/// <summary>
/// Shared Playwright test helpers: mock data factories, theme discovery,
/// and route-injection utilities.
/// </summary>
internal static class PlaywrightHelpers
{
    // -----------------------------------------------------------------------
    // Theme discovery
    // -----------------------------------------------------------------------

    /// <summary>
    /// Return the list of theme values defined in the UI theme dropdown.
    /// Parses the theme select in index.html so that adding a new theme to
    /// the dropdown automatically includes it in tests.
    /// </summary>
    public static IReadOnlyList<string> GetAvailableThemes()
    {
        var htmlPath = Path.Combine(FindProjectRoot(), "src", "WatchBack.Api", "wwwroot", "index.html");
        var content = File.ReadAllText(htmlPath);

        var blockMatch = Regex.Match(content, @"aria-label=""Theme"".*?</select>", RegexOptions.Singleline);
        if (blockMatch.Success)
        {
            var themes = Regex.Matches(blockMatch.Value, @"<option\s+value=""([^""]+)""")
                .Select(m => m.Groups[1].Value)
                .ToList();
            if (themes.Count > 0)
                return themes;
        }

        return ["dark", "light"]; // fallback
    }

    private static string FindProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "WatchBack.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not find solution root");
    }

    // -----------------------------------------------------------------------
    // Lorem ipsum at three lengths
    // -----------------------------------------------------------------------

    public const string LoremShort = "Lorem ipsum dolor sit amet, consectetur adipiscing elit.";

    public const string LoremMedium =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
        "incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud " +
        "exercitation ullamco laboris.";

    public const string LoremLong =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
        "incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud " +
        "exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute " +
        "irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla " +
        "pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia " +
        "deserunt mollit anim id est laborum.";

    // -----------------------------------------------------------------------
    // Thought factories (produce the camelCase JSON shape the frontend expects)
    // -----------------------------------------------------------------------

    public static Dictionary<string, object?> TraktThought(
        int id,
        string text = LoremShort,
        string author = "alice",
        string createdAt = "2023-08-03T00:00:00")
    {
        return new Dictionary<string, object?>
        {
            ["id"] = $"trakt:{id}",
            ["parentId"] = null,
            ["title"] = null,
            ["content"] = text,
            ["url"] = null,
            ["images"] = Array.Empty<object>(),
            ["author"] = author,
            ["score"] = null,
            ["createdAt"] = createdAt,
            ["source"] = "Trakt",
            ["replies"] = Array.Empty<object>(),
            ["postTitle"] = null,
            ["postUrl"] = null,
            ["postBody"] = null,
        };
    }

    public static Dictionary<string, object?> RedditThought(
        string id,
        string text = LoremMedium,
        string author = "redditor",
        string createdAt = "2023-08-03T00:00:00",
        int score = 42,
        IReadOnlyList<Dictionary<string, object?>>? replies = null,
        string postTitle = "S01E01 Discussion Thread",
        string postUrl = "https://reddit.com/r/testshow/comments/abc/s01e01/",
        string? postBody = null)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = $"reddit:{id}",
            ["parentId"] = null,
            ["title"] = null,
            ["content"] = text,
            ["url"] = $"{postUrl.TrimEnd('/')}/{id}/",
            ["images"] = Array.Empty<object>(),
            ["author"] = author,
            ["score"] = score,
            ["createdAt"] = createdAt,
            ["source"] = "Reddit",
            ["replies"] = replies?.ToArray() ?? Array.Empty<object>(),
            ["postTitle"] = postTitle,
            ["postUrl"] = postUrl,
            ["postBody"] = postBody,
        };
    }

    public static Dictionary<string, object?> BlueskyThought(
        string id,
        string text = LoremShort,
        string author = "user.bsky.social",
        string createdAt = "2023-08-03T00:00:00",
        IReadOnlyList<string>? images = null)
    {
        var imageList = (images ?? [])
            .Select(url => new Dictionary<string, object?> { ["url"] = url, ["alt"] = "" })
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["id"] = $"bsky:{id}",
            ["parentId"] = null,
            ["title"] = null,
            ["content"] = text,
            ["url"] = $"https://bsky.app/profile/{author}/post/{id}",
            ["images"] = imageList,
            ["author"] = author,
            ["score"] = null,
            ["createdAt"] = createdAt,
            ["source"] = "Bluesky",
            ["replies"] = Array.Empty<object>(),
            ["postTitle"] = null,
            ["postUrl"] = null,
            ["postBody"] = null,
        };
    }

    // -----------------------------------------------------------------------
    // Canned API responses
    // -----------------------------------------------------------------------

    public static readonly Dictionary<string, object?> SyncIdle = new()
    {
        ["status"] = "Idle",
        ["title"] = null,
        ["metadata"] = null,
        ["allThoughts"] = Array.Empty<object>(),
        ["timeMachineThoughts"] = Array.Empty<object>(),
        ["timeMachineDays"] = 14,
        ["sourceResults"] = Array.Empty<object>(),
    };

    public static Dictionary<string, object?> MakeSyncSuccess(
        IReadOnlyList<Dictionary<string, object?>> thoughts,
        IReadOnlyList<Dictionary<string, object?>>? timeMachine = null)
    {
        var sourceGroups = thoughts
            .GroupBy(t => (string?)t["source"] ?? "Unknown")
            .Select(g => new Dictionary<string, object?>
            {
                ["source"] = g.Key,
                ["postTitle"] = g.First()["postTitle"],
                ["postUrl"] = g.First()["postUrl"],
                ["imageUrl"] = null,
                ["thoughts"] = g.ToArray(),
                ["nextPageToken"] = null,
            })
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["status"] = "Watching",
            ["title"] = "Test Show",
            ["metadata"] = new Dictionary<string, object?>
            {
                ["title"] = "Test Show",
                ["releaseDate"] = "2023-08-01T00:00:00",
                ["episodeTitle"] = "Pilot",
                ["seasonNumber"] = 1,
                ["episodeNumber"] = 1,
            },
            ["allThoughts"] = thoughts.ToArray(),
            ["timeMachineThoughts"] = timeMachine?.ToArray() ?? thoughts.Take(2).ToArray(),
            ["timeMachineDays"] = 14,
            ["sourceResults"] = sourceGroups,
        };
    }

    // -----------------------------------------------------------------------
    // Diagnostics mock data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Derives a diagnostics status snapshot from a canned sync response,
    /// so the source list always matches whatever providers are in the sync mock.
    /// </summary>
    public static object DiagnosticsStatusFromSync(Dictionary<string, object?> syncResponse)
    {
        var sourceResults = (syncResponse["sourceResults"] as IEnumerable<object>) ?? [];
        var sources = sourceResults
            .OfType<Dictionary<string, object?>>()
            .Select(sr => new
            {
                source = (string?)sr["source"] ?? "",
                thoughtCount = (sr["thoughts"] as IEnumerable<object>)?.Count() ?? 0,
            })
            .ToArray();

        var lastSync = new
        {
            timestamp = "2026-03-15T10:20:00Z",
            status = (string?)syncResponse["status"] ?? "Idle",
            title = (string?)syncResponse["title"],
            sources,
        };

        return new { version = "2026.03.15", lastSync };
    }

    /// <summary>
    /// Returns log entries covering every severity level so the log viewer
    /// renders all badge colours. Categories are generic — not tied to any
    /// specific provider implementation.
    /// </summary>
    public static object[] MakeDiagnosticsLogs() =>
    [
        MakeLogEntry("Debug",       "BackgroundService", "Sync cycle starting"),
        MakeLogEntry("Information", "SyncService",       "Sync completed successfully"),
        MakeLogEntry("Warning",     "HttpHandler",       "Transient error, retrying in 1000 ms."),
        MakeLogEntry("Error",       "ThoughtProvider",   "Provider fetch failed",
            "HttpRequestException: Connection refused (127.0.0.1:80)"),
        MakeLogEntry("Critical",    "SyncService",       "Unhandled exception in sync loop",
            "InvalidOperationException: Sequence contains no elements"),
    ];

    private static Dictionary<string, object?> MakeLogEntry(
        string level, string category, string message, string? exceptionText = null) => new()
        {
            ["timestamp"] = "2026-03-15T10:23:45Z",
            ["level"] = level,
            ["category"] = category,
            ["message"] = message,
            ["exceptionText"] = exceptionText,
        };

    public static readonly Dictionary<string, object?> ConfigEmpty = new()
    {
        ["integrations"] = new Dictionary<string, object>
        {
            ["jellyfin"] = MakeIntegration("Jellyfin", false, ["watchState"]),
            ["trakt"] = MakeIntegration("Trakt.tv", false, ["watchState", "thought"]),
            ["bluesky"] = MakeIntegration("Bluesky", false, ["thought"]),
            ["reddit"] = MakeIntegration("Reddit", true, ["thought"]),
        },
        ["preferences"] = new Dictionary<string, object>
        {
            ["timeMachineDays"] = 14,
            ["watchProvider"] = "jellyfin",
            ["watchProviders"] = new[]
            {
                new { value = "jellyfin", label = "Jellyfin", requiresManualInput = false },
            },
            ["searchConfigured"] = false,
        },
    };

    public static readonly Dictionary<string, object?> ConfigFilled = new()
    {
        ["integrations"] = new Dictionary<string, object>
        {
            ["jellyfin"] = MakeIntegration("Jellyfin", true, ["watchState"],
                ("Jellyfin__BaseUrl", "Server URL", "text", "http://192.168.1.100:8096"),
                ("Jellyfin__ApiKey", "API Key", "password", "")),
            ["trakt"] = MakeIntegration("Trakt.tv", true, ["watchState", "thought"],
                ("Trakt__ClientId", "Client ID", "text", "traktclient123"),
                ("Trakt__Username", "Username", "text", "myuser")),
            ["bluesky"] = MakeIntegration("Bluesky", false, ["thought"]),
            ["reddit"] = MakeIntegration("Reddit", true, ["thought"]),
        },
        ["preferences"] = new Dictionary<string, object>
        {
            ["timeMachineDays"] = 14,
            ["watchProvider"] = "jellyfin",
            ["watchProviders"] = new[]
            {
                new { value = "jellyfin", label = "Jellyfin", requiresManualInput = false },
            },
            ["searchConfigured"] = true,
        },
    };

    private static object MakeIntegration(
        string name,
        bool configured,
        string[]? providerTypes = null,
        params (string Key, string Label, string Type, string Value)[] fields)
    {
        var fieldList = fields.Length > 0
            ? fields.Select(f => new { key = f.Key, label = f.Label, type = f.Type, placeholder = "", hasValue = !string.IsNullOrEmpty(f.Value), value = f.Value }).ToArray()
            : Array.Empty<object>();

        return new
        {
            name,
            logoSvg = "",
            brandColor = "",
            fields = fieldList,
            configured,
            providerTypes = providerTypes ?? Array.Empty<string>(),
        };
    }

    // -----------------------------------------------------------------------
    // Route injection
    // -----------------------------------------------------------------------

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = null, // keys are already camelCase in our dicts
    };

    public static async Task SetupApiRoutes(
        IPage page,
        Dictionary<string, object?>? sync = null,
        Dictionary<string, object?>? config = null,
        object[]? diagnosticsLogs = null,
        object? diagnosticsStatus = null,
        bool needsOnboarding = false)
    {
        var authBody = needsOnboarding
            ? """{"authenticated":true,"username":"test","needsOnboarding":true,"authMethod":"cookie","forwardAuthHeader":""}"""
            : """{"authenticated":true,"username":"test","needsOnboarding":false,"authMethod":"cookie","forwardAuthHeader":""}""";

        // Intercept auth check so the app skips the login form and renders content
        await page.RouteAsync("**/api/auth/me", route =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = authBody,
            }));

        // Block SSE so networkidle can settle
        await page.RouteAsync("**/api/sync/stream", route => route.AbortAsync());

        if (sync != null)
        {
            var syncJson = JsonSerializer.Serialize(sync, s_jsonOptions);
            await page.RouteAsync("**/api/sync*", route =>
                route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = syncJson,
                }));
        }

        if (config != null)
        {
            var configJson = JsonSerializer.Serialize(config, s_jsonOptions);
            await page.RouteAsync("**/api/config", route =>
                route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "application/json",
                    Body = configJson,
                }));
        }

        await page.RouteAsync("**/api/status", route =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = """{"watchProvider":"jellyfin"}""",
            }));

        // Diagnostics: log history endpoint
        var logsJson = JsonSerializer.Serialize(diagnosticsLogs ?? [], s_jsonOptions);
        await page.RouteAsync("**/api/diagnostics/logs", route =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = logsJson,
            }));

        // Diagnostics: status endpoint
        var statusJson = diagnosticsStatus != null
            ? JsonSerializer.Serialize(diagnosticsStatus, s_jsonOptions)
            : "null";
        await page.RouteAsync("**/api/diagnostics/status", route =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = statusJson,
            }));

        // Block diagnostics log SSE (same reason as sync SSE)
        await page.RouteAsync("**/api/diagnostics/logs/stream", route => route.AbortAsync());
    }

    // -----------------------------------------------------------------------
    // Theme helpers
    // -----------------------------------------------------------------------

    public static async Task ApplyTheme(IPage page, string theme)
    {
        await page.EvaluateAsync($"document.documentElement.setAttribute('data-theme', '{theme}')");
    }

    // -----------------------------------------------------------------------
    // Page loading
    // -----------------------------------------------------------------------

    public static async Task LoadPage(
        IPage page,
        string url,
        string theme,
        Dictionary<string, object?>? sync = null,
        Dictionary<string, object?>? config = null,
        object[]? diagnosticsLogs = null,
        object? diagnosticsStatus = null,
        bool needsOnboarding = false)
    {
        await SetupApiRoutes(page, sync: sync, config: config,
            diagnosticsLogs: diagnosticsLogs, diagnosticsStatus: diagnosticsStatus,
            needsOnboarding: needsOnboarding);
        await page.GotoAsync(url);

        // Mark wizard/checklist as completed so the overlay doesn't block non-wizard tests
        await page.EvaluateAsync("localStorage.setItem('wb_wizardCompleted', 'true')");
        await page.EvaluateAsync("localStorage.setItem('wb_checklistCompleted', 'true')");
        await page.ReloadAsync();

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 });
        await page.WaitForTimeoutAsync(500); // allow Alpine init
        await ApplyTheme(page, theme);
    }

    /// <summary>
    /// Load a page with the wizard active (simulates first-time user after account setup).
    /// Clears wizard-related localStorage flags so the wizard auto-launches.
    /// </summary>
    public static async Task LoadPageWithWizard(
        IPage page,
        string url,
        string theme,
        Dictionary<string, object?>? config = null)
    {
        await SetupApiRoutes(page, sync: SyncIdle, config: config ?? ConfigEmpty);

        // Clear wizard flags before navigation so the wizard auto-launches
        await page.GotoAsync(url);
        await page.EvaluateAsync("localStorage.removeItem('wb_wizardCompleted')");
        await page.EvaluateAsync("localStorage.removeItem('wb_checklistCompleted')");
        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 });
        await page.WaitForTimeoutAsync(500); // allow Alpine init
        await ApplyTheme(page, theme);
    }

    public static async Task AdvanceWizardToStep(IPage page, int targetStep)
    {
        for (var i = 0; i < targetStep; i++)
        {
            // Each step has a different primary button — find the visible one
            if (i == 0)
            {
                // Step 0: "Get Started" button
                var btn = page.Locator("button:visible:has-text('Get Started'), button:visible:has-text('Comenzar')");
                await btn.ClickAsync();
            }
            else
            {
                // Steps 1+: "Skip this step" link (fastest path to advance)
                var skip = page.Locator("button:visible:has-text('Skip this step'), button:visible:has-text('Omitir este paso')");
                await skip.ClickAsync();
            }
            await page.WaitForTimeoutAsync(300); // allow transition
        }
    }

    public static async Task OpenConfigModal(IPage page)
    {
        await page.ClickAsync("button[title=\"Configuration\"]");
        await page.WaitForTimeoutAsync(300); // allow Alpine x-show transition
    }

    public static async Task SwitchToDiagnosticsTab(IPage page)
    {
        await page.ClickAsync("button:has-text('Diagnostics')");
        // Wait for the log container to become visible (Alpine lifts display:none on the panel)
        await page.WaitForSelectorAsync("#log-container",
            new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        await page.WaitForTimeoutAsync(200); // allow Alpine to render entries
    }
}
