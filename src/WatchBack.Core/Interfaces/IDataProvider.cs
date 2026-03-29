using WatchBack.Core.Models;

namespace WatchBack.Core.Interfaces;

/// <summary>
///     Information about a DataProvider
/// </summary>
/// <param name="Name">Used for grouping and display text in the UI if there is no DisplayName</param>
/// <param name="Description">Used for display text in the UI</param>
/// <param name="OverrideDisplayName">If populated sets DisplayName which is used for display text in the UI</param>
/// <param name="BrandData">An object with branding data used in the UI for styling</param>
public record DataProviderMetadata(
    string Name,
    string Description,
    string? OverrideDisplayName = null,
    BrandData? BrandData = null
)
{
    public string DisplayName => OverrideDisplayName ?? Name;
}

/// <summary>
///     A data provider could be any source for information. Generally other interfaces derive from this one to describe
///     implementations for specific types of DataProviders such as a WatchStateProvider
/// </summary>
public interface IDataProvider
{
    DataProviderMetadata Metadata { get; }

    /// <summary>
    ///     The appsettings configuration section this provider is bound to (e.g. "Jellyfin"),
    ///     used as the integration key in the config UI. Null means no config panel.
    /// </summary>
    string? ConfigSection => null;

    /// <summary>Whether this provider has enough configuration to operate.</summary>
    bool IsConfigured => true;

    /// <summary>
    ///     Configures HTTP request headers for outgoing requests. The default implementation
    ///     sets the WatchBack User-Agent. Override to add provider-specific headers (e.g. API keys),
    ///     calling <see cref="ApplyDefaultHeaders" /> to preserve the base behaviour.
    /// </summary>
    void ConfigureRequest(HttpRequestMessage request)
    {
        ApplyDefaultHeaders(request);
    }

    /// <summary>
    ///     Applies the default WatchBack headers (User-Agent). Call this from
    ///     <see cref="ConfigureRequest" /> overrides to replicate base-class behaviour.
    /// </summary>
    static void ApplyDefaultHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "WatchBack/1.0");
    }

    /// <summary>
    ///     Checks whether the provider's external service is reachable and
    ///     correctly configured, without fetching any real data.
    /// </summary>
    Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default);

    /// <summary>
    ///     Returns the field definitions for this provider's settings panel.
    ///     <paramref name="envVal" /> reads a flat key from environment variables only (before user-settings overlay).
    ///     <paramref name="isOverridden" /> returns true when the (section, key) pair exists in user-settings.json.
    /// </summary>
    IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return [];
    }

    /// <summary>
    ///     Tests connectivity using form-submitted values that may not yet be persisted.
    ///     Password fields that were not changed by the user arrive as "__EXISTING__";
    ///     implementations should fall back to their stored option value in that case.
    ///     Default delegates to <see cref="GetServiceHealthAsync" />.
    /// </summary>
    Task<ServiceHealth> TestConnectionAsync(
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct = default)
    {
        return GetServiceHealthAsync(ct);
    }

    /// <summary>
    ///     Returns the plaintext value of a secret config field owned by this provider
    ///     (e.g. an API key stored as a password field), or null if the key is not owned here.
    /// </summary>
    string? RevealSecret(string key)
    {
        return null;
    }
}
