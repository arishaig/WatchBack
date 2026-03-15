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

    public static readonly Dictionary<string, object?> ConfigEmpty = new()
    {
        ["integrations"] = new Dictionary<string, object>
        {
            ["jellyfin"] = MakeIntegration("Jellyfin", false),
            ["trakt"] = MakeIntegration("Trakt.tv", false),
            ["bluesky"] = MakeIntegration("Bluesky", false),
            ["reddit"] = MakeIntegration("Reddit", true),
        },
        ["preferences"] = new Dictionary<string, object>
        {
            ["timeMachineDays"] = 14,
            ["watchProvider"] = "jellyfin",
            ["watchProviders"] = new[]
            {
                new { value = "jellyfin", label = "Jellyfin" },
            },
        },
    };

    public static readonly Dictionary<string, object?> ConfigFilled = new()
    {
        ["integrations"] = new Dictionary<string, object>
        {
            ["jellyfin"] = MakeIntegration("Jellyfin", true,
                ("Jellyfin__BaseUrl", "Server URL", "text", "http://192.168.1.100:8096"),
                ("Jellyfin__ApiKey", "API Key", "password", "")),
            ["trakt"] = MakeIntegration("Trakt.tv", true,
                ("Trakt__ClientId", "Client ID", "text", "traktclient123"),
                ("Trakt__Username", "Username", "text", "myuser")),
            ["bluesky"] = MakeIntegration("Bluesky", false),
            ["reddit"] = MakeIntegration("Reddit", true),
        },
        ["preferences"] = new Dictionary<string, object>
        {
            ["timeMachineDays"] = 14,
            ["watchProvider"] = "jellyfin",
            ["watchProviders"] = new[]
            {
                new { value = "jellyfin", label = "Jellyfin" },
            },
        },
    };

    private static object MakeIntegration(string name, bool configured, params (string Key, string Label, string Type, string Value)[] fields)
    {
        var fieldList = fields.Length > 0
            ? fields.Select(f => new { key = f.Key, label = f.Label, type = f.Type, placeholder = "", hasValue = !string.IsNullOrEmpty(f.Value), value = f.Value }).ToArray()
            : Array.Empty<object>();

        return new { name, logoSvg = "", brandColor = "", fields = fieldList, configured };
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
        Dictionary<string, object?>? config = null)
    {
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
        Dictionary<string, object?>? config = null)
    {
        await SetupApiRoutes(page, sync: sync, config: config);
        await page.GotoAsync(url);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 });
        await page.WaitForTimeoutAsync(500); // allow Alpine init
        await ApplyTheme(page, theme);
    }

    public static async Task OpenConfigModal(IPage page)
    {
        await page.ClickAsync("button[title=\"Configuration\"]");
        await page.WaitForTimeoutAsync(300); // allow Alpine x-show transition
    }
}
