using System.Net;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Microsoft.AspNetCore.HttpOverrides;

using WatchBack.Api;
using WatchBack.Api.Auth;
using WatchBack.Api.Endpoints;
using WatchBack.Api.Logging;
using WatchBack.Api.Mcp;
using WatchBack.Api.Serialization;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Core.Services;
using WatchBack.Infrastructure.Extensions;
using WatchBack.Infrastructure.Persistence;
using WatchBack.Infrastructure.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<InMemoryLogBuffer>();
builder.Services.AddSingleton<SyncHistoryStore>();
builder.Services.AddSingleton<SyncTrigger>();
builder.Services.AddSingleton<SyncGate>();
builder.Services.AddSingleton<SentimentScorer>();
builder.Services.AddSingleton<ILoggerProvider, InMemoryLoggerProvider>();

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

string? databasePath = Environment.GetEnvironmentVariable("WATCHBACK_DATABASE_PATH");
if (string.IsNullOrEmpty(databasePath))
{
    // /app/data for Docker, AppData for local dev.
    string basePath = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production"
        ? "/app/data"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WatchBack");
    databasePath = Path.Combine(basePath, "watchback.db");
}

string? dbDirectory = Path.GetDirectoryName(databasePath);
if (dbDirectory != null)
{
    Directory.CreateDirectory(dbDirectory);
}

// File logging — two rolling sinks in a logs/ subdirectory alongside the database:
//   watchback<date>.log   — human-readable text for tailing/grepping
//   watchback<date>.jsonl — compact JSON lines for Promtail/Loki ingestion
// Framework namespaces are suppressed to Warning to avoid EF Core / Polly noise.
string logDirectory = Path.Combine(dbDirectory ?? ".", "logs");
Directory.CreateDirectory(logDirectory);
Serilog.Core.Logger serilogFileLogger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("Polly", LogEventLevel.Warning)
    .WriteTo.File(
        Path.Combine(logDirectory, "watchback.log"),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        formatProvider: System.Globalization.CultureInfo.InvariantCulture,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        shared: true)
    .WriteTo.File(
        new CompactJsonFormatter(),
        Path.Combine(logDirectory, "watchback.jsonl"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        shared: true)
    .CreateLogger();
builder.Logging.AddSerilog(serilogFileLogger, dispose: true);
builder.Services.AddSingleton<Serilog.ILogger>(serilogFileLogger);
builder.Services.AddSingleton(new LogFileConfig(logDirectory));
builder.Logging.AddFilter("WatchBack", LogLevel.Debug);
builder.Logging.AddFilter("UI", LogLevel.Debug);
builder.Logging.AddFilter<InMemoryLoggerProvider>("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter<InMemoryLoggerProvider>("System", LogLevel.Warning);
builder.Logging.AddFilter<InMemoryLoggerProvider>("Polly", LogLevel.Warning);

string mappingsDir = Path.Combine(dbDirectory ?? ".", "subreddit-mappings");
string builtInMappingsPath = Path.Combine(AppContext.BaseDirectory, "builtin-subreddit-mappings.json");
builder.Services.AddSingleton(new SubredditMappingPaths(builtInMappingsPath, mappingsDir));
builder.Services.AddSingleton<ISubredditMappingService, SubredditMappingService>();

// User-editable config lives next to the database.
string userConfigPath = Path.Combine(dbDirectory ?? ".", "user-settings.json");
builder.Configuration.AddJsonFile(userConfigPath, true, true);
builder.Services.AddSingleton(new UserConfigFile(userConfigPath));

builder.Services.AddDbContext<WatchBackDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

builder.Services
    .AddOptions<JellyfinOptions>()
    .BindConfiguration("Jellyfin")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<TraktOptions>()
    .BindConfiguration("Trakt")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<BlueskyOptions>()
    .BindConfiguration("Bluesky")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<RedditOptions>()
    .BindConfiguration("Reddit")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<LemmyOptions>()
    .BindConfiguration("Lemmy")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<WatchBackOptions>()
    .BindConfiguration("WatchBack")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<OmdbOptions>()
    .BindConfiguration("Omdb")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<AuthOptions>()
    .BindConfiguration("Auth")
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Persist Data Protection keys so session cookies survive restarts
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dbDirectory ?? "."))
    .SetApplicationName("WatchBack");

builder.Services.AddWatchBackInfrastructure();

builder.Services.AddLocalization();
string[] supportedCultures = ["en-US", "es"];
builder.Services.Configure<RequestLocalizationOptions>(opts =>
{
    opts.SetDefaultCulture("en-US")
        .AddSupportedCultures(supportedCultures)
        .AddSupportedUICultures(supportedCultures);
    opts.FallBackToParentCultures = true;
    opts.FallBackToParentUICultures = true;
});

builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddSingleton<ITimeMachineFilter, TimeMachineFilter>();
builder.Services.AddSingleton<IReplyTreeBuilder, ReplyTreeBuilder>();
builder.Services.AddSingleton<IPrefetchService, PrefetchService>();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, WatchBackJsonContext.Default));

// Rate limiting — protect login from brute-force
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("client-log", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "WatchBackSession";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Always in Production: the app itself is only ever served over plain HTTP
        // (TLS terminates at the reverse proxy), so SameAsRequest would never mark
        // the cookie Secure unless X-Forwarded-Proto is trusted — see the
        // ForwardedHeaders configuration below.
        options.Cookie.SecurePolicy = builder.Environment.IsProduction()
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        // When the ForwardAuth header is present, delegate authentication to
        // the ForwardAuth scheme so that UseAuthentication() populates ctx.User
        // for all endpoints — including AllowAnonymous ones like GET /api/auth/me.
        options.ForwardDefaultSelector = ctx =>
        {
            AuthOptions authOpts = ctx.RequestServices
                .GetRequiredService<IOptionsMonitor<AuthOptions>>()
                .CurrentValue;
            return !string.IsNullOrEmpty(authOpts.ForwardAuthHeader)
                   && ctx.Request.Headers.ContainsKey(authOpts.ForwardAuthHeader)
                ? "ForwardAuth"
                : null;
        };
        // ForwardDefaultSelector would otherwise also apply to SignIn/SignOut,
        // routing /api/auth/login's SignInAsync call into ForwardAuthHandler
        // (which doesn't implement sign-in) whenever the header happens to be
        // present. Pin those two operations back to this scheme itself so
        // password login always works regardless of the header.
        options.ForwardSignIn = CookieAuthenticationDefaults.AuthenticationScheme;
        options.ForwardSignOut = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = 401;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    })
    .AddScheme<ForwardAuthOptions, ForwardAuthHandler>("ForwardAuth", _ => { })
    .AddScheme<BearerApiKeyOptions, BearerApiKeyAuthHandler>("ApiKey", _ => { });

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, "ForwardAuth")
        .RequireAuthenticatedUser()
        .Build();

    // The MCP endpoint also accepts bearer API keys in addition to the default auth schemes.
    // Key management endpoints (/api/mcp/keys) remain under the default policy only —
    // an API key cannot mint, list, or revoke other keys.
    options.AddPolicy("mcp", new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, "ForwardAuth", "ApiKey")
        .RequireAuthenticatedUser()
        .Build());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<WatchBackMcpTools>();

WebApplication app = builder.Build();

using (IServiceScope scope = app.Services.CreateScope())
{
    WatchBackDbContext dbContext = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
    await dbContext.Database.MigrateAsync();

    // WAL journal mode breaks on NFS-backed storage (production) because SQLite
    // WAL relies on mmap of the .shm index file which NFS cannot provide reliable
    // locking for — cross-connection WAL reads silently return stale data. DELETE
    // mode serialises writes via advisory locks that NFSv4 does support and is
    // safe for a single-instance deployment. In development and CI the database
    // lives on local disk where WAL is fine; using DELETE there causes lock
    // contention when parallel test factories all share the same SQLite file.
    if (app.Environment.IsProduction())
    {
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=DELETE;");
    }
}

await InitializeAuthAsync(app);

// X-Forwarded-Proto is trusted unconditionally, from any peer, with no KnownProxies
// restriction. Spoofing it only makes an attacker's own request look like HTTPS when
// it isn't — a browser won't store or send a Secure cookie over a connection it knows
// is plain HTTP, so this can't be used to steal another client's session. Trusting it
// is what lets CookieSecurePolicy.Always (above) work correctly behind a reverse proxy
// that terminates TLS, since the app itself only ever sees a plain HTTP connection.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto,
    KnownIPNetworks = { new System.Net.IPNetwork(IPAddress.Any, 0), new System.Net.IPNetwork(IPAddress.IPv6Any, 0) }
});

// X-Forwarded-For feeds RemoteIpAddress, which in turn feeds the login rate limiter's
// partition key and ForwardAuthHandler's trusted-host IP check — unlike the proto
// header, trusting it from an arbitrary peer would let that peer spoof its own IP for
// both of those checks. So it's only trusted from the reverse proxy configured via
// Auth:ForwardAuthTrustedHost — the same "which proxy do I trust" setting
// ForwardAuthHandler already uses. KnownProxies/KnownNetworks are read once when this
// middleware is constructed, so changing the trusted host requires an app restart;
// SaveForwardAuth triggers one automatically when this setting changes.
ForwardedHeadersOptions? xForwardedForOptions = await BuildTrustedForwardedForOptionsAsync(app);
if (xForwardedForOptions != null)
{
    app.UseForwardedHeaders(xForwardedForOptions);
}

// Security headers — applied to every response before the rest of the pipeline.
//
// CSP notes:
//   script-src: 'unsafe-inline' and 'unsafe-eval' are required by Alpine.js v3, which
//   evaluates x-on/x-bind expressions via new Function(). The risk is mitigated by the
//   app never rendering user-controlled content into directive expressions.
//
//   style-src: 'unsafe-inline' is required for Alpine.js :style bindings that emit
//   inline style="" attributes at runtime.
//
//   img-src https: is required because provider APIs (OMDb, Reddit) return external
//   poster/post image URLs from CDNs (e.g. m.media-amazon.com, i.redd.it).
app.Use(async (ctx, next) =>
{
    IHeaderDictionary headers = ctx.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'none'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "connect-src 'self'; " +
        "font-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none';";
    headers["X-Frame-Options"] = "DENY";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // Disable browser APIs the app never uses so a future XSS can't abuse them
    headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), " +
                                     "magnetometer=(), microphone=(), payment=(), usb=()";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Swagger requires authentication — gated via middleware.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/swagger"),
    branch => branch.Use(async (ctx, next) =>
    {
        AuthenticateResult authResult = await ctx.AuthenticateAsync();
        if (!authResult.Succeeded)
        {
            ctx.Response.Redirect("/");
            return;
        }

        await next();
    }));
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "WatchBack API v1");
    options.RoutePrefix = "swagger";
});

app.MapStringsEndpoints();
app.MapAuthEndpoints();
RouteGroupBuilder protectedGroup = app.MapGroup("").RequireAuthorization();
protectedGroup.MapSyncEndpoints();
protectedGroup.MapConfigEndpoints();
protectedGroup.MapSystemEndpoints();
protectedGroup.MapDiagnosticsEndpoints();
protectedGroup.MapManualWatchStateEndpoints();
protectedGroup.MapSearchEndpoints();
protectedGroup.MapSubredditMappingEndpoints();
protectedGroup.MapApiKeyEndpoints();

app.MapMcp("/mcp").RequireAuthorization("mcp");

app.MapFallbackToFile("index.html");

app.Run();

static async Task<ForwardedHeadersOptions?> BuildTrustedForwardedForOptionsAsync(WebApplication webApp)
{
    string trustedHost = webApp.Services.GetRequiredService<IOptionsMonitor<AuthOptions>>()
        .CurrentValue.ForwardAuthTrustedHost;
    if (string.IsNullOrEmpty(trustedHost))
    {
        // No trusted proxy configured — leave X-Forwarded-For untrusted (default
        // loopback-only KnownNetworks), matching the previous safe-by-default behavior.
        return null;
    }

    ForwardedHeadersOptions options = new() { ForwardedHeaders = ForwardedHeaders.XForwardedFor };

    if (trustedHost.Equals("any", StringComparison.OrdinalIgnoreCase) || trustedHost == "*")
    {
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Any, 0));
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Any, 0));
        return options;
    }

    if (IPAddress.TryParse(trustedHost, out IPAddress? trustedIp))
    {
        options.KnownProxies.Add(trustedIp);
        return options;
    }

    try
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(trustedHost);
        foreach (IPAddress address in addresses)
        {
            options.KnownProxies.Add(address);
        }
    }
    catch (Exception ex)
    {
        LogTrustedHostResolutionFailed(webApp.Logger, ex, trustedHost);
        return null;
    }

    return options.KnownProxies.Count > 0 ? options : null;
}

static async Task InitializeAuthAsync(WebApplication webApp)
{
    string passwordHash = webApp.Configuration["Auth:PasswordHash"] ?? "";
    if (!string.IsNullOrEmpty(passwordHash))
    {
        return;
    }

    string password = AuthEndpoints.GeneratePassword();
    PasswordHasher<string> hasher = new();
    string hash = hasher.HashPassword("", password);

    UserConfigFile configFile = webApp.Services.GetRequiredService<UserConfigFile>();
    await AuthEndpoints.WriteAuthConfig(configFile, "watchback", hash, false, CancellationToken.None);

    // Force reload so IOptionsSnapshot picks up new values on first request
    if (webApp.Configuration is IConfigurationRoot configRoot)
    {
        configRoot.Reload();
    }

    // Write directly to stdout — avoids logger pipeline so credentials
    // don't end up in structured log sinks (Seq, ELK, etc.)
    Console.WriteLine("╔══════════════════════════════════════════════╗");
    Console.WriteLine("║     WatchBack — Initial Credentials          ║");
    Console.WriteLine("║  Username : watchback                        ║");
    Console.WriteLine($"║  Password : {password,-32}║");
    Console.WriteLine("║  Log in and complete account setup.          ║");
    Console.WriteLine("╚══════════════════════════════════════════════╝");
}

public partial class Program
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Forward-auth trusted host '{TrustedHost}' could not be resolved at startup — " +
                  "X-Forwarded-For will not be trusted until this is fixed and the app restarts")]
    private static partial void LogTrustedHostResolutionFailed(
        Microsoft.Extensions.Logging.ILogger logger, Exception ex, string trustedHost);
}
