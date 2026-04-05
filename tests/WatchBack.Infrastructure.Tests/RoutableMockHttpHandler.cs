using System.Net;

namespace WatchBack.Infrastructure.Tests;

/// <summary>
///     A configurable <see cref="HttpMessageHandler" /> that dispatches responses based on URL patterns.
///     Routes are evaluated in registration order; the first matching route wins.
///     All requests are recorded in <see cref="RecordedUris" /> for assertion after the fact.
/// </summary>
internal sealed class RoutableMockHttpHandler : HttpMessageHandler
{
    private readonly List<(Func<Uri, bool> Predicate, Func<HttpRequestMessage, HttpResponseMessage> Factory)> _routes
        = [];

    private readonly List<Uri> _recordedUris = [];
    private Func<HttpRequestMessage, HttpResponseMessage>? _default;

    /// <summary>Every URI that was requested, in call order.</summary>
    public IReadOnlyList<Uri> RecordedUris => _recordedUris;

    /// <summary>
    ///     Registers a route that matches when the URL-decoded request URI string contains
    ///     <paramref name="urlSubstring" /> (case-sensitive). The URI is decoded before comparison
    ///     so callers can write readable strings (e.g. <c>"Mass Casualty"</c>) rather than
    ///     percent-encoded ones (e.g. <c>"Mass%20Casualty"</c>).
    /// </summary>
    public RoutableMockHttpHandler RespondTo(
        string urlSubstring,
        string jsonBody,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((
            uri => Uri.UnescapeDataString(uri.ToString()).Contains(urlSubstring, StringComparison.Ordinal),
            _ => Json(jsonBody, status)));
        return this;
    }

    /// <summary>
    ///     Registers a route that matches when <paramref name="predicate" /> returns <c>true</c>
    ///     for the request URI.
    /// </summary>
    public RoutableMockHttpHandler RespondTo(
        Func<Uri, bool> predicate,
        string jsonBody,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((predicate, _ => Json(jsonBody, status)));
        return this;
    }

    /// <summary>
    ///     Sets the fallback response used when no registered route matches.
    ///     Without a default, unmatched requests return HTTP 404.
    /// </summary>
    public RoutableMockHttpHandler Default(
        string jsonBody,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        _default = _ => Json(jsonBody, status);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _recordedUris.Add(request.RequestUri!);

        foreach ((Func<Uri, bool> predicate, Func<HttpRequestMessage, HttpResponseMessage> factory) in _routes)
        {
            if (predicate(request.RequestUri!))
            {
                return Task.FromResult(factory(request));
            }
        }

        return Task.FromResult(
            _default?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status) =>
        new(status) { Content = new StringContent(body) };
}
