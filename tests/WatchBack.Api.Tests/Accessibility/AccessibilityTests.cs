using System.Net;
using System.Net.Sockets;

using Deque.AxeCore.Playwright;

using FluentAssertions;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

using Xunit;

using static WatchBack.Api.Tests.Accessibility.PlaywrightHelpers;

namespace WatchBack.Api.Tests.Accessibility;

/// <summary>
/// Accessibility tests using axe-core via Playwright.
/// Requires Playwright browsers installed in ~/.cache/ms-playwright.
/// Every test runs once per theme defined in the UI dropdown.
/// </summary>
[Trait("Category", "Accessibility")]
public class AccessibilityTests : IAsyncLifetime, IDisposable
{
    private KestrelWebApplicationFactory _factory = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        _factory = new KestrelWebApplicationFactory();
        _baseUrl = _factory.ServerAddress;

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = FindChromiumExecutable(),
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
        _factory.Dispose();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string? FindChromiumExecutable()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "ms-playwright");

        if (!Directory.Exists(cacheDir))
            return null;

        var chromiumDirs = Directory.GetDirectories(cacheDir, "chromium-*")
            .Where(d => !d.Contains("headless_shell") && !d.Contains("tip-of-tree"))
            .OrderByDescending(d => d)
            .ToList();

        foreach (var dir in chromiumDirs)
        {
            var candidates = new[]
            {
                Path.Combine(dir, "chrome-linux", "chrome"),
                Path.Combine(dir, "chrome-linux64", "chrome"),
            };
            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null) return found;
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Dynamic theme data source
    // -----------------------------------------------------------------------

    public static IEnumerable<object[]> ThemeData =>
        GetAvailableThemes().Select(t => new object[] { t });

    // -----------------------------------------------------------------------
    // Assertion helper
    // -----------------------------------------------------------------------

    private static async Task AssertNoViolations(IPage page)
    {
        var results = await page.RunAxe();
        var violations = results.Violations;

        violations.Should().BeEmpty(
            "axe-core found {0} violation(s):\n{1}",
            violations.Length,
            string.Join("\n\n", violations.Select(v =>
                $"[{v.Id}] {v.Help}\n  Impact: {v.Impact}\n  Nodes: {v.Nodes.Length}\n  " +
                string.Join("\n  ", v.Nodes.Select(n => n.Html)))));

        // Surface computable contrast failures hidden in 'incomplete'
        var contrastFailures = new List<string>();
        foreach (var item in results.Incomplete)
        {
            if (item.Id != "color-contrast")
                continue;

            foreach (var node in item.Nodes)
            {
                foreach (var check in node.Any)
                {
                    var data = check.Data as IDictionary<string, object>;
                    if (data == null) continue;

                    var ratio = data.TryGetValue("contrastRatio", out var r)
                        ? Convert.ToDouble(r, System.Globalization.CultureInfo.InvariantCulture)
                        : 0.0;
                    if (ratio == 0) continue;

                    var expectedStr = data.TryGetValue("expectedContrastRatio", out var e)
                        ? e.ToString() ?? "4.5:1" : "4.5:1";
                    var expected = double.Parse(
                        expectedStr!.Replace(":1", ""),
                        System.Globalization.CultureInfo.InvariantCulture);

                    if (ratio < expected)
                    {
                        var fg = data.TryGetValue("fgColor", out var fgVal) ? fgVal : "?";
                        var bg = data.TryGetValue("bgColor", out var bgVal) ? bgVal : "?";
                        contrastFailures.Add(
                            $"  {node.Html}\n" +
                            $"    ratio={ratio:F2} (required {expectedStr}), fg={fg}, bg={bg}");
                    }
                }
            }
        }

        contrastFailures.Should().BeEmpty(
            "Color contrast failures (computed by axe but below WCAG AA threshold):\n{0}",
            string.Join("\n", contrastFailures));
    }

    // -----------------------------------------------------------------------
    // Tests — each runs once per theme via [MemberData]
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task IdleState(string theme)
    {
        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: SyncIdle,
                config: ConfigFilled);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task SuccessTraktOnly(string theme)
    {
        var thoughts = new[]
        {
            TraktThought(1, LoremShort, "alice"),
            TraktThought(2, LoremMedium, "bob"),
            TraktThought(3, LoremLong, "carol"),
        };

        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: MakeSyncSuccess(thoughts),
                config: ConfigFilled);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task SuccessRedditWithNesting(string theme)
    {
        var thoughts = new[]
        {
            RedditThought("r1", LoremMedium, "user1", score: 99,
                replies: [
                    RedditThought("r1_1", LoremShort, "user2",
                        replies: [RedditThought("r1_1_1", LoremShort, "user3")]),
                ]),
            RedditThought("r2", LoremLong, "user4", score: 7),
        };

        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: MakeSyncSuccess(thoughts),
                config: ConfigFilled);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task SuccessBlueskyWithImages(string theme)
    {
        var img = $"{_baseUrl}/watchback.png";
        var thoughts = new[]
        {
            BlueskyThought("b1", LoremShort, "alice.bsky.social"),
            BlueskyThought("b2", LoremMedium, "bob.bsky.social", images: [img]),
            BlueskyThought("b3", LoremLong, "carol.bsky.social", images: [img, img]),
        };

        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: MakeSyncSuccess(thoughts),
                config: ConfigFilled);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task SuccessAllSources(string theme)
    {
        var img = $"{_baseUrl}/watchback.png";
        var thoughts = new Dictionary<string, object?>[]
        {
            TraktThought(1, LoremShort, "trakt_alice"),
            RedditThought("r1", LoremMedium, "reddit_bob", score: 58,
                replies: [RedditThought("r1_1", LoremShort, "reddit_carol")]),
            BlueskyThought("b1", LoremShort, "bsky.user.social", images: [img]),
            TraktThought(2, LoremLong, "trakt_dave"),
            RedditThought("r2", LoremShort, "reddit_eve", score: 12),
            BlueskyThought("b2", LoremMedium, "another.bsky.social"),
        };

        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: MakeSyncSuccess(thoughts),
                config: ConfigFilled);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task ConfigModalEmpty(string theme)
    {
        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: SyncIdle,
                config: ConfigEmpty);
            await OpenConfigModal(page);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task ConfigModalFilled(string theme)
    {
        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: SyncIdle,
                config: ConfigFilled);
            await OpenConfigModal(page);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task DiagnosticsTabEmpty(string theme)
    {
        // No sync history, no log entries — tests the empty-state UI
        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: SyncIdle,
                config: ConfigFilled);
            await OpenConfigModal(page);
            await SwitchToDiagnosticsTab(page);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ThemeData))]
    public async Task DiagnosticsTabWithData(string theme)
    {
        // Sync history derived from the same thoughts used in the sync mock,
        // plus log entries at every severity level to exercise all badge colours.
        var thoughts = new[]
        {
            TraktThought(1, LoremShort, "alice"),
            RedditThought("r1", LoremMedium, "bob"),
            BlueskyThought("b1", LoremShort, "carol.bsky.social"),
        };
        var sync = MakeSyncSuccess(thoughts);

        var page = await _browser.NewPageAsync();
        try
        {
            await LoadPage(page, _baseUrl, theme,
                sync: sync,
                config: ConfigFilled,
                diagnosticsLogs: MakeDiagnosticsLogs(),
                diagnosticsStatus: DiagnosticsStatusFromSync(sync));
            await OpenConfigModal(page);
            await SwitchToDiagnosticsTab(page);
            await AssertNoViolations(page);
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}

/// <summary>
/// WebApplicationFactory that starts the real app on a TCP port
/// so external processes like Playwright can connect to it.
/// </summary>
internal sealed class KestrelWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly int _port;
    private TcpListener? _portHolder;
    private IHost? _host;

    public KestrelWebApplicationFactory()
    {
        // Hold the port open with a TcpListener so nothing else can steal it
        // during the slow builder.Build() call (which runs DB migrations).
        _portHolder = new TcpListener(IPAddress.Loopback, 0);
        _portHolder.Start();
        _port = ((IPEndPoint)_portHolder.LocalEndpoint).Port;
        _ = Server; // triggers CreateHost, which releases the holder then starts Kestrel
    }

    public string ServerAddress => $"http://127.0.0.1:{_port}";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Create the default test host (in-memory TestServer) — needed for the base class
        var testHost = base.CreateHost(builder);

        // Also start a real Kestrel host on a TCP port with proper content root
        builder.ConfigureWebHost(webHost =>
        {
            webHost.UseKestrel();
            webHost.UseUrls(ServerAddress);
            webHost.UseContentRoot(FindApiProjectRoot());
        });

        _host = builder.Build();

        // Release the port holder right before Kestrel binds, shrinking the TOCTOU
        // window from ~1 s (the Build() duration) down to microseconds.
        _portHolder?.Stop();
        _portHolder = null;

        _host.Start();

        return testHost;
    }

    protected override void Dispose(bool disposing)
    {
        _portHolder?.Stop();
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        base.Dispose(disposing);
    }

    private static string FindApiProjectRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "WatchBack.sln")))
                return Path.Combine(dir, "src", "WatchBack.Api");
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not find solution root");
    }
}