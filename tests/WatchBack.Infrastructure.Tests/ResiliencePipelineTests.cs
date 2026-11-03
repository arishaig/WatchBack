using System.Diagnostics;
using System.Net;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Polly;

using WatchBack.Infrastructure.Extensions;
using WatchBack.Infrastructure.Http;
using WatchBack.Infrastructure.ThoughtProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class ResiliencePipelineTests
{
    // Records when each request reached the network and how many were in flight per host.
    private sealed class ConcurrencyProbeHandler(TimeSpan latency) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _inFlight = [];
        private readonly Lock _lock = new();
        public Dictionary<string, int> MaxInFlight { get; } = [];
        public List<(string Host, TimeSpan At)> Starts { get; } = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            lock (_lock)
            {
                Starts.Add((host, _clock.Elapsed));
                int now = _inFlight[host] = _inFlight.GetValueOrDefault(host) + 1;
                MaxInFlight[host] = Math.Max(MaxInFlight.GetValueOrDefault(host), now);
            }

            await Task.Delay(latency, cancellationToken);

            lock (_lock)
            {
                _inFlight[host]--;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static ServiceProvider BuildServices(HttpMessageHandler primary)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddWatchBackInfrastructure();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => primary));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task VolunteerClient_SendsOneRequestAtATimePerHost_AboutOnePerSecond()
    {
        ConcurrencyProbeHandler handler = new(TimeSpan.FromMilliseconds(50));
        await using ServiceProvider provider = BuildServices(handler);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(RedditThoughtProvider));

        await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(i => client.GetAsync(new Uri($"https://api.pullpush.io/search?{i}"))));

        handler.MaxInFlight["api.pullpush.io"].Should().Be(1);
        TimeSpan[] starts = handler.Starts.Select(s => s.At).Order().ToArray();
        (starts[2] - starts[0]).Should().BeGreaterThan(TimeSpan.FromSeconds(1.8),
            "three requests through a 1/s token bucket span at least two refill periods");
    }

    // Regression (seen live): the throttle sat inside suppression and the breaker, so requests already
    // queued had passed those checks and went out after the host said to back off.
    [Fact]
    public async Task VolunteerClient_QueuedRequestsHonourA429SeenWhileTheyWaited()
    {
        SlowRateLimitedHandler handler = new();
        await using ServiceProvider provider = BuildServices(handler);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(RedditThoughtProvider));

        // All four are queued before the first (slow) answer arrives.
        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(i => client.GetAsync(new Uri($"https://api.pullpush.io/search?{i}"))));

        handler.Calls.Should().Be(1, "the three queued requests must see the 429's backoff window when their turn comes");
    }

    private sealed class SlowRateLimitedHandler : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(200, cancellationToken);
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "600");
            return response;
        }
    }

    [Fact]
    public async Task VolunteerClient_ThrottlesEachHostSeparately()
    {
        ConcurrencyProbeHandler handler = new(TimeSpan.FromMilliseconds(300));
        await using ServiceProvider provider = BuildServices(handler);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(RedditThoughtProvider));

        await Task.WhenAll(
            client.GetAsync(new Uri("https://api.pullpush.io/a")),
            client.GetAsync(new Uri("https://arctic-shift.photon-reddit.com/b")));

        TimeSpan[] starts = handler.Starts.Select(s => s.At).Order().ToArray();
        (starts[1] - starts[0]).Should().BeLessThan(TimeSpan.FromMilliseconds(250),
            "a request to Arctic Shift must not queue behind one to PullPush");
    }

    [Fact]
    public async Task NonVolunteerClient_IsNotThrottled()
    {
        ConcurrencyProbeHandler handler = new(TimeSpan.FromMilliseconds(200));
        await using ServiceProvider provider = BuildServices(handler);
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BlueskyThoughtProvider));

        await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(i => client.GetAsync(new Uri($"https://bsky.example/search?{i}"))));

        handler.MaxInFlight["bsky.example"].Should().Be(3);
    }

    // Regression: retry handled 503 before suppression saw it, so a 503 + Retry-After was retried
    // (after waiting out the Retry-After inside the request) instead of suppressing the host.
    [Fact]
    public async Task ServiceUnavailableWithRetryAfter_IsNotRetried_AndSuppressesTheHost()
    {
        int calls = 0;
        await using ServiceProvider provider = BuildServices(new MockHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            return response;
        }));
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(BlueskyThoughtProvider));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await client.GetAsync(new Uri("https://bsky.example/a"), timeout.Token);
        await client.GetAsync(new Uri("https://bsky.example/b"), timeout.Token);

        calls.Should().Be(1, "no retry, and the second request is suppressed for the 120 s the server asked");
    }

    // The breaker is exercised on a bare pipeline (no retry) so the test doesn't sit through backoff delays.
    [Fact]
    public async Task CircuitBreaker_AfterRepeatedFailures_ShortCircuitsAsServiceUnavailable()
    {
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddStrategy(_ => new BrokenCircuitResponseStrategy())
            .AddCircuitBreaker(ServiceCollectionExtensions.BuildCircuitBreakerOptions(NullLogger.Instance))
            .Build();
        int networkCalls = 0;

        List<HttpStatusCode> statuses = [];
        for (int i = 0; i < 6; i++)
        {
            HttpResponseMessage response = await pipeline.ExecuteAsync(_ =>
            {
                networkCalls++;
                return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            });
            statuses.Add(response.StatusCode);
        }

        networkCalls.Should().Be(4, "the circuit opens once the minimum throughput of failures is reached");
        statuses.Skip(4).Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.ServiceUnavailable));
    }

    // Regression (seen live): PullPush answered 522 after ~20 s per request. 522 wasn't counted, and
    // slow failures never reached 4 inside a one-minute window, so all 8 searches ran to the end.
    [Fact]
    public async Task CircuitBreaker_OpensOnSlowCloudflareOriginErrors()
    {
        FakeTimeProvider time = new();
        ResiliencePipelineBuilder<HttpResponseMessage> builder = new() { TimeProvider = time };
        ResiliencePipeline<HttpResponseMessage> pipeline = builder
            .AddStrategy(_ => new BrokenCircuitResponseStrategy())
            .AddCircuitBreaker(ServiceCollectionExtensions.BuildCircuitBreakerOptions(NullLogger.Instance))
            .Build();
        int networkCalls = 0;

        for (int i = 0; i < 8; i++)
        {
            await pipeline.ExecuteAsync(_ =>
            {
                networkCalls++;
                time.Advance(TimeSpan.FromSeconds(20));
                return ValueTask.FromResult(new HttpResponseMessage((HttpStatusCode)522));
            });
        }

        networkCalls.Should().Be(4);
    }

    [Fact]
    public async Task CircuitBreaker_IgnoresRateLimitResponses()
    {
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddStrategy(_ => new BrokenCircuitResponseStrategy())
            .AddCircuitBreaker(ServiceCollectionExtensions.BuildCircuitBreakerOptions(NullLogger.Instance))
            .Build();
        int networkCalls = 0;

        for (int i = 0; i < 6; i++)
        {
            await pipeline.ExecuteAsync(_ =>
            {
                networkCalls++;
                return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            });
        }

        networkCalls.Should().Be(6, "429s are handled by rate-limit suppression, not the breaker");
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    [InlineData(3, 240)]
    [InlineData(20, 1800)]
    public void BreakDuration_GrowsWithFailedProbes_UpToHalfAnHour(int halfOpenAttempts, int expectedSeconds)
    {
        ServiceCollectionExtensions.BreakDuration(halfOpenAttempts).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }
}
