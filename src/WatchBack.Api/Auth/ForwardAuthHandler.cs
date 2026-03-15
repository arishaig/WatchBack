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

        var claims = new[] { new Claim(ClaimTypes.Name, headerValue) };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
