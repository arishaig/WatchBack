using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using WatchBack.Api.Endpoints;

namespace WatchBack.Api.Auth;

/// <summary>
///     Who may change WatchBack's settings. Keyed on the watch user picked in the browser (a Jellyfin account),
///     which the frontend mirrors into the <see cref="CookieName" /> cookie. This guards against mistakes, not
///     attackers: anyone signed in can pick any user. With no admins stored, everyone is an admin, so
///     single-user setups and providers that can't tell users apart are never restricted.
/// </summary>
public sealed class AdminStore(UserConfigFile configFile)
{
    /// <summary>The cookie holding the watch user ID the browser picked.</summary>
    public const string CookieName = "wb_watchUser";

    /// <summary>Where the admin list lives in user-settings.json: Access:Admins, comma-separated IDs.</summary>
    public const string Section = "Access";

    public const string Key = "Admins";

    /// <summary>The picked watch user ID, or null when the browser hasn't picked one.</summary>
    public static string? CallerId(HttpContext ctx)
    {
        string? id = ctx.Request.Cookies[CookieName];
        return string.IsNullOrWhiteSpace(id) ? null : Uri.UnescapeDataString(id).Trim();
    }

    /// <summary>The stored admin IDs. Read from the file each time, so a change applies to the next request.</summary>
    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken ct)
    {
        Dictionary<string, Dictionary<string, string>> settings = await AuthEndpoints.ReadConfigFile(configFile.Path, ct);
        return Parse(settings);
    }

    /// <summary>
    ///     Makes <paramref name="id" /> the first admin if there are none yet. Returns the admins after the call.
    ///     Re-read under the config lock, so two browsers racing on first load can't both win.
    /// </summary>
    public async Task<IReadOnlyList<string>> ClaimIfNoneAsync(string id, CancellationToken ct)
    {
        await AuthEndpoints.ConfigFileLock.WaitAsync(ct);
        try
        {
            Dictionary<string, Dictionary<string, string>> settings =
                await AuthEndpoints.ReadConfigFile(configFile.Path, ct);
            List<string> admins = Parse(settings);
            if (admins.Count > 0)
            {
                return admins;
            }

            await WriteAsync(settings, [id], ct);
            return [id];
        }
        finally
        {
            AuthEndpoints.ConfigFileLock.Release();
        }
    }

    /// <summary>Replaces the admin list.</summary>
    public async Task SetAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await AuthEndpoints.ConfigFileLock.WaitAsync(ct);
        try
        {
            Dictionary<string, Dictionary<string, string>> settings =
                await AuthEndpoints.ReadConfigFile(configFile.Path, ct);
            await WriteAsync(settings, ids, ct);
        }
        finally
        {
            AuthEndpoints.ConfigFileLock.Release();
        }
    }

    /// <summary>Whether the caller may change settings.</summary>
    public async Task<bool> IsAdminAsync(ClaimsPrincipal user, HttpContext ctx, CancellationToken ct)
    {
        // API keys are only minted by admins (key management is admin-only), so a key acts as one.
        if (user.Identity?.AuthenticationType == "ApiKey")
        {
            return true;
        }

        IReadOnlyList<string> admins = await GetAsync(ct);
        if (admins.Count == 0)
        {
            return true;
        }

        string? caller = CallerId(ctx);
        return caller is not null && admins.Contains(caller, StringComparer.Ordinal);
    }

    private static List<string> Parse(Dictionary<string, Dictionary<string, string>> settings) =>
        settings.TryGetValue(Section, out Dictionary<string, string>? section)
        && section.TryGetValue(Key, out string? value)
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToList()
            : [];

    private Task WriteAsync(Dictionary<string, Dictionary<string, string>> settings, IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        if (!settings.TryGetValue(Section, out Dictionary<string, string>? section))
        {
            section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings[Section] = section;
        }

        section[Key] = string.Join(',', ids.Distinct(StringComparer.Ordinal));
        return AuthEndpoints.WriteConfigFile(configFile.Path, settings, ct);
    }
}

/// <summary>Requires the caller to be an admin (see <see cref="AdminStore" />).</summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

public sealed class AdminAuthorizationHandler(AdminStore store, IHttpContextAccessor accessor)
    : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        AdminRequirement requirement)
    {
        HttpContext? ctx = accessor.HttpContext;
        if (ctx is not null && context.User.Identity?.IsAuthenticated == true
                            && await store.IsAdminAsync(context.User, ctx, ctx.RequestAborted))
        {
            context.Succeed(requirement);
        }
    }
}
