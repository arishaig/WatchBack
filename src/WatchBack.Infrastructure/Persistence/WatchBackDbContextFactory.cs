using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WatchBack.Infrastructure.Persistence;

/// <summary>
///     Design-time factory for EF Core migrations.
///     Used by dotnet-ef tools to create DbContext instances without a full DI container.
/// </summary>
public class WatchBackDbContextFactory : IDesignTimeDbContextFactory<WatchBackDbContext>
{
    public WatchBackDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<WatchBackDbContext> optionsBuilder = new();

        // Use SQLite with a default database file for migrations
        // In production, this is overridden by actual configuration in Program.cs
        optionsBuilder.UseSqlite("Data Source=watchback.db");

        return new WatchBackDbContext(optionsBuilder.Options);
    }
}
