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
    private static readonly TimeSpan s_dnsNegativeCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_dnsTimeout = TimeSpan.FromSeconds(5);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        AuthOptions opts = authOptions.CurrentValue;
        if (string.IsNullOrEmpty(opts.ForwardAuthHeader))
        {
            return AuthenticateResult.NoResult();
        }

        // Trusted host is required when forward auth is enabled.
        // Without it, any network peer could spoof the header.
        if (string.IsNullOrEmpty(opts.ForwardAuthTrustedHost))
        {
            LogMissingTrustedHost(Logger);
            return AuthenticateResult.NoResult();
        }

        string? headerValue = Request.Headers[opts.ForwardAuthHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue))
        {
            return AuthenticateResult.NoResult();
        }

        // "any" or "*" means the user explicitly trusts all hosts.
        bool trustAll = opts.ForwardAuthTrustedHost.Equals("any", StringComparison.OrdinalIgnoreCase)
                        || opts.ForwardAuthTrustedHost == "*";

        if (!trustAll)
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

        // Resolve hostname to IP addresses, caching to avoid DNS per-request.
        // Negative results are cached briefly to prevent repeated timeout-inducing
        // lookups when the trusted host is misconfigured.
        string cacheKey = $"forwardauth:dns:{trustedHost}";
        string negativeCacheKey = $"forwardauth:dns:neg:{trustedHost}";
        if (!cache.TryGetValue(cacheKey, out IPAddress[]? addresses))
        {
            if (cache.TryGetValue(negativeCacheKey, out _))
            {
                return false;
            }

            try
            {
                // Use a dedicated timeout CTS so a slow or unreachable DNS server
                // does not block the auth pipeline indefinitely.
                using CancellationTokenSource dnsCts = new(s_dnsTimeout);
                addresses = await Dns.GetHostAddressesAsync(trustedHost, dnsCts.Token);
                cache.Set(cacheKey, addresses, s_dnsCacheDuration);
            }
            catch (OperationCanceledException)
            {
                LogDnsTimeout(Logger, trustedHost);
                cache.Set(negativeCacheKey, true, s_dnsNegativeCacheDuration);
                return false;
            }
            catch (Exception)
            {
                cache.Set(negativeCacheKey, true, s_dnsNegativeCacheDuration);
                return false;
            }
        }

        return addresses is not null && addresses.Any(a => a.Equals(remoteIp));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "ForwardAuth: remote IP {IP} does not match trusted host '{TrustedHost}', falling back to cookie auth")]
    private static partial void LogTrustedHostMismatch(ILogger logger, IPAddress? ip, string trustedHost);

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "ForwardAuth: DNS lookup for trusted host '{TrustedHost}' timed out after 5 s, treating as untrusted")]
    private static partial void LogDnsTimeout(ILogger logger, string trustedHost);

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "ForwardAuth: header is configured but ForwardAuthTrustedHost is empty — forward auth is disabled until a trusted host is set (use 'any' or '*' to trust all hosts)")]
    private static partial void LogMissingTrustedHost(ILogger logger);
}
