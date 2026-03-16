using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using WatchBack.Core.Options;

namespace WatchBack.Api.Auth;

public class ForwardAuthOptions : AuthenticationSchemeOptions { }

public class ForwardAuthHandler : AuthenticationHandler<ForwardAuthOptions>
{
    private readonly IOptionsMonitor<AuthOptions> _authOptions;

    // Pin the first IP that successfully presents the forward auth header.
    // Since there's only one reverse proxy, any other source is suspicious.
    private static IPAddress? s_trustedProxyIp;
    private static readonly object s_ipLock = new();

    public ForwardAuthHandler(
        IOptionsMonitor<ForwardAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<AuthOptions> authOptions)
        : base(options, logger, encoder)
    {
        _authOptions = authOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var opts = _authOptions.CurrentValue;
        if (string.IsNullOrEmpty(opts.ForwardAuthHeader))
            return Task.FromResult(AuthenticateResult.NoResult());

        var headerValue = Request.Headers[opts.ForwardAuthHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue))
            return Task.FromResult(AuthenticateResult.NoResult());

        // IP pinning: trust only the first IP that presents the header
        var remoteIp = Context.Connection.RemoteIpAddress;
        if (remoteIp != null)
        {
            lock (s_ipLock)
            {
                if (s_trustedProxyIp == null)
                {
                    s_trustedProxyIp = remoteIp;
#pragma warning disable CA1848, CA1873
                    Logger.LogInformation("ForwardAuth: pinned trusted proxy IP to {IP}", remoteIp);
#pragma warning restore CA1848, CA1873
                }
                else if (!s_trustedProxyIp.Equals(remoteIp))
                {
#pragma warning disable CA1848
                    Logger.LogWarning(
                        "ForwardAuth: IP {IP} does not match trusted proxy {TrustedIP}, falling back to cookie auth",
                        remoteIp, s_trustedProxyIp);
#pragma warning restore CA1848
                    return Task.FromResult(AuthenticateResult.NoResult());
                }
            }
        }

        var claims = new[] { new Claim(ClaimTypes.Name, headerValue) };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Clears the pinned proxy IP (e.g., when ForwardAuth settings change).
    /// </summary>
    internal static void ResetTrustedProxy()
    {
        lock (s_ipLock) { s_trustedProxyIp = null; }
    }
}
