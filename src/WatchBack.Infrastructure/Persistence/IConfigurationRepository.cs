using WatchBack.Infrastructure.Persistence.Entities;

namespace WatchBack.Infrastructure.Persistence;

/// <summary>
/// Repository for managing provider configuration persistence.
/// </summary>
public interface IConfigurationRepository
{
    /// <summary>
    /// Get all provider configurations.
    /// </summary>
    Task<IReadOnlyList<ProviderConfigEntity>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Get configuration for a specific provider.
    /// </summary>
    Task<IReadOnlyList<ProviderConfigEntity>> GetByProviderAsync(string providerName, CancellationToken ct = default);

    /// <summary>
    /// Get a specific configuration value.
    /// </summary>
    Task<ProviderConfigEntity?> GetAsync(string providerName, string configKey, CancellationToken ct = default);

    /// <summary>
    /// Save or update a configuration value.
    /// </summary>
    Task SaveAsync(ProviderConfigEntity config, CancellationToken ct = default);

    /// <summary>
    /// Delete a configuration value.
    /// </summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Delete all configurations for a provider.
    /// </summary>
    Task DeleteByProviderAsync(string providerName, CancellationToken ct = default);
}