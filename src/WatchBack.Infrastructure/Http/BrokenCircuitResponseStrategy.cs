using System.Net;

using Polly;
using Polly.CircuitBreaker;

namespace WatchBack.Infrastructure.Http;

/// <summary>
///     Turns an open circuit into a synthetic 503 response instead of a thrown
///     <see cref="BrokenCircuitException" />, so providers see an ordinary failed request
///     (like the synthetic 429 from <see cref="RateLimitSuppressionStrategy" />) rather than
///     logging an exception for every call skipped while the host is down.
/// </summary>
internal sealed class BrokenCircuitResponseStrategy : ResilienceStrategy<HttpResponseMessage>
{
    protected override async ValueTask<Outcome<HttpResponseMessage>> ExecuteCore<TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<HttpResponseMessage>>> callback,
        ResilienceContext context,
        TState state)
    {
        Outcome<HttpResponseMessage> outcome = await callback(context, state);
        return outcome.Exception is BrokenCircuitException
            ? Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = context.GetRequestMessage()
            })
            : outcome;
    }
}
