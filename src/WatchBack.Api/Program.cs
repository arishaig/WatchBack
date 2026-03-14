using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Options;
using WatchBack.Core.Services;
using WatchBack.Infrastructure.Extensions;
using WatchBack.Infrastructure.Persistence;
using WatchBack.Api.Endpoints;
using WatchBack.Api.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// Add database
// Use environment variable or default path
var databasePath = Environment.GetEnvironmentVariable("WATCHBACK_DATABASE_PATH");
if (string.IsNullOrEmpty(databasePath))
{
    // Default: use /app/data for Docker, or AppData for local development
    var basePath = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production"
        ? "/app/data"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WatchBack");
    databasePath = Path.Combine(basePath, "watchback.db");
}

var dbDirectory = Path.GetDirectoryName(databasePath);
if (dbDirectory != null)
    Directory.CreateDirectory(dbDirectory);

// Load user-editable config from the same directory as the database
var userConfigPath = Path.Combine(dbDirectory ?? ".", "user-settings.json");
builder.Configuration.AddJsonFile(userConfigPath, optional: true, reloadOnChange: true);
builder.Services.AddSingleton(new WatchBack.Api.Endpoints.UserConfigFile(userConfigPath));

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
    .BindConfiguration("Reddit");

builder.Services
    .AddOptions<WatchBackOptions>()
    .BindConfiguration("WatchBack");

// Add infrastructure providers
builder.Services.AddWatchBackInfrastructure();

// Add core services
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddSingleton<ITimeMachineFilter, TimeMachineFilter>();
builder.Services.AddSingleton<IReplyTreeBuilder, ReplyTreeBuilder>();

// Configure JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, WatchBackJsonContext.Default));

// Add Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "WatchBack API",
        Version = "v1",
        Description = "Watch state detection and thought aggregation API",
        Contact = new OpenApiContact
        {
            Name = "WatchBack",
            Url = new Uri("https://github.com/watchback/watchback-net")
        }
    });
});

var app = builder.Build();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Enable Swagger/OpenAPI
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "WatchBack API v1");
    options.RoutePrefix = "swagger"; // Available at /swagger
});

// Enable static files (frontend)
app.UseDefaultFiles();
app.UseStaticFiles();

// Map endpoints
app.MapSyncEndpoints();
app.MapConfigEndpoints();

// Map fallback to index.html for SPA
app.MapFallbackToFile("index.html");

app.Run();

// Make Program accessible for tests
public partial class Program { }
