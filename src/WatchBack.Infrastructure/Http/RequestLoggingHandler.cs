using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace WatchBack.Infrastructure.Http;

/// <summary>
///     Logs total wall time for every request sent through a WatchBack HttpClient.
///     Registered outside the resilience pipeline so each log entry represents the
///     end-to-end cost (including retries, rate-limit holds, and timeouts).
///     Runs at Debug level — set
///     <c>"Logging:LogLevel:WatchBack.Infrastructure.Http": "Debug"</c>
///     in <c>appsettings.json</c> to enable; omit or set to <c>Information</c> to disable.
/// </summary>
internal sealed class RequestLoggingHandler(ILogger<RequestLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Skip all work when the category is above Debug. Avoids paying for the
        // Stopwatch and URI redaction when logging is off.
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        string method = request.Method.Method;
        string uri = HttpLogging.RedactSensitiveParams(request.RequestUri);

        logger.LogDebug("→ {Method} {Uri}", method, uri);
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            sw.Stop();
            logger.LogDebug("← {Method} {Uri} {Status} in {Elapsed}ms",
                method, uri, (int)response.StatusCode, sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogDebug("← {Method} {Uri} FAILED {Exception} in {Elapsed}ms",
                method, uri, ex.GetType().Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}