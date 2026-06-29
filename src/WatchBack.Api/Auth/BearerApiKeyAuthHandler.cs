using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using WatchBack.Infrastructure.Persistence;

namespace WatchBack.Api.Auth;

public class BearerApiKeyOptions : AuthenticationSchemeOptions;

public class BearerApiKeyAuthHandler(
    IOptionsMonitor<BearerApiKeyOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    WatchBackDbContext dbContext)
    : AuthenticationHandler<BearerApiKeyOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string token = header["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        string hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        bool valid = await dbContext.ApiKeys
            .AnyAsync(k => k.KeyHash == hash, Context.RequestAborted);

        if (!valid)
        {
            return AuthenticateResult.NoResult();
        }

        Claim[] claims = [new(ClaimTypes.Name, "api-key")];
        ClaimsIdentity identity = new(claims, Scheme.Name);
        AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
