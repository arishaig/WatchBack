using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using WatchBack.Core.Options;

namespace WatchBack.Api.Auth;

public class ForwardAuthOptions : AuthenticationSchemeOptions;

public partial class ForwardAuthHandler(
    IOptionsMonitor<ForwardAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<AuthOptions> authOptions,
    IMemoryCache cache)
    : AuthenticationHandler<ForwardAuthOptions>(options, logger, encoder)
{
    private static readonly TimeSpan s_dnsCacheDuration = TimeSpan.FromSeconds(60);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        AuthOptions opts = authOptions.CurrentValue;
        if (string.IsNullOrEmpty(opts.ForwardAuthHeader))
        {
            return AuthenticateResult.NoResult();
        }

        string? headerValue = Request.Headers[opts.ForwardAuthHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue))
        {
            return AuthenticateResult.NoResult();
        }

        // If a trusted host is configured, validate the remote IP against it.
        // When blank, any host presenting the header is trusted.
        if (!string.IsNullOrEmpty(opts.ForwardAuthTrustedHost))
        {
            IPAddress? remoteIp = Context.Connection.RemoteIpAddress;
            if (remoteIp == null || !await IsFromTrustedHostAsync(remoteIp, opts.ForwardAuthTrustedHost))
            {
                LogTrustedHostMismatch(Logger, remoteIp, opts.ForwardAuthTrustedHost);
                return AuthenticateResult.NoResult();
            }
        }

        Claim[] claims = [new(ClaimTypes.Name, headerValue)];
        ClaimsIdentity identity = new(claims, Scheme.Name);
        AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    ///     Checks whether the remote IP matches the configured trusted host.
    ///     Accepts an IP address directly or resolves a hostname via DNS (cached for 60 s).
    /// </summary>
    private async Task<bool> IsFromTrustedHostAsync(IPAddress remoteIp, string trustedHost)
    {
        // Try parsing as an IP address first — no DNS needed
        if (IPAddress.TryParse(trustedHost, out IPAddress? trustedIp))
        {
            return remoteIp.Equals(trustedIp);
        }

        // Resolve hostname to IP addresses, caching to avoid DNS per-request
        string cacheKey = $"forwardauth:dns:{trustedHost}";
        if (!cache.TryGetValue(cacheKey, out IPAddress[]? addresses))
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(trustedHost);
                cache.Set(cacheKey, addresses, s_dnsCacheDuration);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }
        }

        return addresses is not null && addresses.Any(a => a.Equals(remoteIp));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "ForwardAuth: remote IP {IP} does not match trusted host '{TrustedHost}', falling back to cookie auth")]
    private static partial void LogTrustedHostMismatch(ILogger logger, IPAddress? ip, string trustedHost);
}
