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

    public bool OnboardingComplete { get; set; }

    /// <summary>
    ///     Set to <c>true</c> after a password reset. The next successful login
    ///     will force the user through the change-password screen (not full
    ///     onboarding) and clear this flag.
    /// </summary>
    public bool PasswordResetPending { get; set; }
}
