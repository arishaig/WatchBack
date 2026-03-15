namespace WatchBack.Core.Options;

public class AuthOptions
{
    public string Username { get; set; } = "watchback";
    public string PasswordHash { get; set; } = "";
    public string ForwardAuthHeader { get; set; } = "";
    public bool OnboardingComplete { get; set; }
}
