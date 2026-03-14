using Microsoft.EntityFrameworkCore;
using WatchBack.Infrastructure.Persistence.Entities;

namespace WatchBack.Infrastructure.Persistence;

public class WatchBackDbContext : DbContext
{
    public WatchBackDbContext(DbContextOptions<WatchBackDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProviderConfigEntity> ProviderConfigs { get; set; } = null!;
    public DbSet<UserPreferenceEntity> UserPreferences { get; set; } = null!;
    public DbSet<SyncLogEntity> SyncLogs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ProviderConfig
        modelBuilder.Entity<ProviderConfigEntity>()
            .HasKey(e => e.Id);

        modelBuilder.Entity<ProviderConfigEntity>()
            .HasIndex(e => e.ProviderName)
            .IsUnique();

        // UserPreference
        modelBuilder.Entity<UserPreferenceEntity>()
            .HasKey(e => e.Id);

        modelBuilder.Entity<UserPreferenceEntity>()
            .HasIndex(e => e.Key)
            .IsUnique();

        // SyncLog
        modelBuilder.Entity<SyncLogEntity>()
            .HasKey(e => e.Id);

        modelBuilder.Entity<SyncLogEntity>()
            .HasIndex(e => e.Timestamp)
            .IsDescending();
    }
}
