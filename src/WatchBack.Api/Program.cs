using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
builder.Services.AddWatchBackInfrastructure(builder.Configuration);

// Add core services
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddSingleton<ITimeMachineFilter, TimeMachineFilter>();
builder.Services.AddSingleton<IReplyTreeBuilder, ReplyTreeBuilder>();

// Configure JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, WatchBackJsonContext.Default));

var app = builder.Build();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<WatchBackDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseHttpsRedirection();

// Map endpoints
app.MapSyncEndpoints();

app.Run();

// Make Program accessible for tests
public partial class Program { }
