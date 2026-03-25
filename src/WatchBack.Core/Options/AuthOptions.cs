namespace WatchBack.Core.Options;

public class AuthOptions
{
    public string Username { get; set; } = "watchback";
    public string PasswordHash { get; set; } = "";
    public string ForwardAuthHeader { get; set; } = "";

    /// <summary>
    /// Optional IP address or hostname of the trusted reverse proxy.
    /// When set, only requests from this host are accepted for forward auth.
    /// When blank, any host presenting the forward auth header is trusted.
    /// </summary>
    public string ForwardAuthTrustedHost { get; set; } = "";

    public bool OnboardingComplete { get; set; }
}
