using WatchBack.Core.Interfaces;

namespace WatchBack.Infrastructure.Http;

/// <summary>
///     Gives every request from a WatchBack HTTP client the descriptive User-Agent
///     (<see cref="IDataProvider.UserAgent" />), so no provider can forget it. .NET sends none by default.
/// </summary>
internal sealed class UserAgentHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        IDataProvider.ApplyDefaultHeaders(request);
        return base.SendAsync(request, cancellationToken);
    }
}
