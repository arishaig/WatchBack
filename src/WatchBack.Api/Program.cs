using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;
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

// Add services
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// Add database
// Use environment variable or default path
string? databasePath = Environment.GetEnvironmentVariable("WATCHBACK_DATABASE_PATH");
if (string.IsNullOrEmpty(databasePath))
{
    // Default: use /app/data for Docker, or AppData for local development
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

// Register subreddit mapping service
string mappingsDir = Path.Combine(dbDirectory ?? ".", "subreddit-mappings");
string builtInMappingsPath = Path.Combine(AppContext.BaseDirectory, "builtin-subreddit-mappings.json");
builder.Services.AddSingleton(new SubredditMappingPaths(builtInMappingsPath, mappingsDir));
builder.Services.AddSingleton<ISubredditMappingService, SubredditMappingService>();

// Load user-editable config from the same directory as the database
string userConfigPath = Path.Combine(dbDirectory ?? ".", "user-settings.json");
builder.Configuration.AddJsonFile(userConfigPath, true, true);
builder.Services.AddSingleton(new UserConfigFile(userConfigPath));

builder.Services.AddDbContext<WatchBackDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

// Configure options
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

// Add infrastructure providers
builder.Services.AddWatchBackInfrastructure();

// Add internationalization and localization
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

// Add core services
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddSingleton<ITimeMachineFilter, TimeMachineFilter>();
builder.Services.AddSingleton<IReplyTreeBuilder, ReplyTreeBuilder>();
builder.Services.AddSingleton<IPrefetchService, PrefetchService>();

// Configure JSON serialization
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
});

// Add authentication / authorization
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "WatchBackSession";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
    .AddScheme<ForwardAuthOptions, ForwardAuthHandler>("ForwardAuth", _ => { });

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, "ForwardAuth")
        .RequireAuthenticatedUser()
        .Build();
});

// Add Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

WebApplication app = builder.Build();

// Initialize database
using (IServiceScope scope = app.Services.CreateScope())
{
    WatchBackDbContext dbContext = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
    await dbContext.Database.MigrateAsync();

    // Enable WAL mode for better concurrent read/write performance (especially
    // fire-and-forget sync history writes alongside foreground queries).
    await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

// Initialize auth — generate initial password if not set
await InitializeAuthAsync(app);

// Trust X-Forwarded-* headers from reverse proxies so RemoteIpAddress,
// Scheme, and Host reflect the real client rather than the proxy itself.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

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
    await next();
});

// Enable static files (frontend)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Enable Swagger/OpenAPI — require authentication via middleware gate
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

// Map endpoints
app.MapStringsEndpoints(); // public strings endpoint
app.MapAuthEndpoints(); // public auth endpoints
RouteGroupBuilder protectedGroup = app.MapGroup("").RequireAuthorization();
protectedGroup.MapSyncEndpoints();
protectedGroup.MapConfigEndpoints();
protectedGroup.MapSystemEndpoints();
protectedGroup.MapDiagnosticsEndpoints();
protectedGroup.MapManualWatchStateEndpoints();
protectedGroup.MapSearchEndpoints();
protectedGroup.MapSubredditMappingEndpoints();

// Map fallback to index.html for SPA
app.MapFallbackToFile("index.html");

app.Run();

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

// Make Program accessible for tests
public partial class Program;
