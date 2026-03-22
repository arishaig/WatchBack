using Microsoft.EntityFrameworkCore;

using WatchBack.Infrastructure.Persistence.Entities;

namespace WatchBack.Infrastructure.Persistence;

/// <summary>
/// Implementation of configuration repository using EF Core.
/// </summary>
public class ConfigurationRepository(WatchBackDbContext dbContext) : IConfigurationRepository
{
    public async Task<IReadOnlyList<ProviderConfigEntity>> GetAllAsync(CancellationToken ct = default)
    {
        return await dbContext.ProviderConfigs.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderConfigEntity>> GetByProviderAsync(string providerName, CancellationToken ct = default)
    {
        return await dbContext.ProviderConfigs
            .Where(p => p.ProviderName == providerName)
            .ToListAsync(ct);
    }

    public async Task<ProviderConfigEntity?> GetAsync(string providerName, string configKey, CancellationToken ct = default)
    {
        return await dbContext.ProviderConfigs
            .FirstOrDefaultAsync(p => p.ProviderName == providerName && p.ConfigKey == configKey, ct);
    }

    public async Task SaveAsync(ProviderConfigEntity config, CancellationToken ct = default)
    {
        if (config.Id == 0)
        {
            dbContext.ProviderConfigs.Add(config);
        }
        else
        {
            dbContext.ProviderConfigs.Update(config);
        }

        await dbContext.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var config = await dbContext.ProviderConfigs.FindAsync([id], ct);
        if (config is not null)
        {
            dbContext.ProviderConfigs.Remove(config);
            await dbContext.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteByProviderAsync(string providerName, CancellationToken ct = default)
    {
        await dbContext.ProviderConfigs
            .Where(p => p.ProviderName == providerName)
            .ExecuteDeleteAsync(ct);
    }
}