using Microsoft.EntityFrameworkCore;

using WatchBack.Infrastructure.Persistence.Entities;

namespace WatchBack.Infrastructure.Persistence;

public class WatchBackDbContext(DbContextOptions<WatchBackDbContext> options) : DbContext(options)
{
    public DbSet<SyncLogEntity> SyncLogs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SyncLogEntity>()
            .HasKey(e => e.Id);

        modelBuilder.Entity<SyncLogEntity>()
            .HasIndex(e => e.Timestamp)
            .IsDescending();
    }
}
