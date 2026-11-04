namespace WatchBack.Core.Options;

public class AuthOptions
{
    public string Username { get; set; } = "watchback";
    public string PasswordHash { get; set; } = "";
    public string ForwardAuthHeader { get; set; } = "";

    /// <summary>
    ///     IP address or hostname of the trusted reverse proxy. Required when
    ///     <see cref="ForwardAuthHeader" /> is set. Use <c>"any"</c> or <c>"*"</c>
    ///     to explicitly trust all hosts. When blank, forward auth is disabled.
    /// </summary>
    public string ForwardAuthTrustedHost { get; set; } = "";

    /// <summary>
    ///     The header the proxy lists the user's groups in, comma-separated (Authelia sends Remote-Groups).
    ///     When a forward-auth request carries it, the groups decide who is an admin. Blank turns this off.
    /// </summary>
    public string ForwardAuthGroupsHeader { get; set; } = "Remote-Groups";

    /// <summary>The group whose members may change settings when <see cref="ForwardAuthGroupsHeader" /> is sent.</summary>
    public string ForwardAuthAdminGroup { get; set; } = "admins";

    public bool OnboardingComplete { get; set; }

    /// <summary>
    ///     Set to <c>true</c> after a password reset. The next successful login
    ///     will force the user through the change-password screen (not full
    ///     onboarding) and clear this flag.
    /// </summary>
    public bool PasswordResetPending { get; set; }
}
