using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Polly;

using WatchBack.Infrastructure.Http;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class RateLimitStrategyTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly HostBackoffRegistry _registry;

    public RateLimitStrategyTests()
    {
        _registry = new HostBackoffRegistry(_time);
    }

    private ResiliencePipeline<HttpResponseMessage> BuildPipeline() =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitSuppression(_registry, NullLogger<RateLimitSuppressionStrategy>.Instance)
            .Build();

    private static Func<HttpResponseMessage> Respond(HttpStatusCode status, string? header = null, string? value = null)
    {
        return () =>
        {
            HttpResponseMessage msg = new(status);
            if (header is not null)
            {
                msg.Headers.TryAddWithoutValidation(header, value);
            }

            return msg;
        };
    }

    // Returns how many of the probes made at each offset (seconds after the 429) reached the network.
    private async Task<List<int>> ProbeAsync(Func<HttpResponseMessage> first, params int[] offsets)
    {
        ResiliencePipeline<HttpResponseMessage> pipeline = BuildPipeline();
        HttpRequestMessage request = new(HttpMethod.Get, "https://example.com/api/data");
        await ExecuteAsync(pipeline, request, first);

        DateTimeOffset start = _time.GetUtcNow();
        List<int> reached = [];
        foreach (int offset in offsets)
        {
            _time.SetUtcNow(start.AddSeconds(offset));
            int calls = 0;
            await ExecuteAsync(pipeline, request, () =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            reached.Add(calls);
        }

        return reached;
    }

    private static async Task<HttpResponseMessage> ExecuteAsync(
        ResiliencePipeline<HttpResponseMessage> pipeline,
        HttpRequestMessage request,
        Func<HttpResponseMessage> networkStub)
    {
        ResilienceContext context = ResilienceContextPool.Shared.Get();
        context.SetRequestMessage(request);
        try
        {
            return await pipeline.ExecuteAsync(_ => ValueTask.FromResult(networkStub()), context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    // ---- Happy path ----

    [Fact]
    public async Task ExecuteAsync_SuccessfulGet_PassesThroughWithoutInterference()
    {
        // Arrange
        int callCount = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = BuildPipeline();
        HttpRequestMessage request = new(HttpMethod.Get, "https://example.com/api/data");

        // Act
        HttpResponseMessage response = await ExecuteAsync(pipeline, request, () =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1);
    }

    // ---- 429 rate-limit short-circuiting ----

    [Fact]
    public async Task ExecuteAsync_After429WithRetryAfter_SubsequentRequestShortCircuitsWithoutNetworkCall()
    {
        // Arrange — first call returns 429 with Retry-After: 30
        int callCount = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = BuildPipeline();
        HttpRequestMessage request = new(HttpMethod.Get, "https://example.com/api/data");

        // Act — first call triggers the rate-limit window
        HttpResponseMessage first = await ExecuteAsync(pipeline, request, () =>
        {
            callCount++;
            HttpResponseMessage msg = new(HttpStatusCode.TooManyRequests);
            msg.Headers.Add("Retry-After", "30");
            return msg;
        });

        // Second call should be short-circuited by the cache — no network call made
        HttpResponseMessage second = await ExecuteAsync(pipeline, request, () =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Assert
        first.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        callCount.Should().Be(1, "only the first request should reach the network");
    }

    [Fact]
    public async Task ExecuteAsync_After429WithRateLimitResetSeconds_HonorsResetInsteadOfDefaultWindow()
    {
        // Arctic Shift sends X-RateLimit-Reset (seconds) rather than Retry-After
        List<int> reached = await ProbeAsync(Respond(HttpStatusCode.TooManyRequests, "X-RateLimit-Reset", "5"), 4, 6);

        reached.Should().Equal(0, 1);
    }

    // Regression: windows were capped at 5 minutes, so a longer Retry-After was retried early.
    [Fact]
    public async Task ExecuteAsync_After429WithLongRetryAfter_NeverCallsBeforeTheServerAsked()
    {
        List<int> reached = await ProbeAsync(Respond(HttpStatusCode.TooManyRequests, "Retry-After", "600"), 299, 301, 599, 601);

        reached.Should().Equal(0, 0, 0, 1);
    }

    // Regression: an epoch X-RateLimit-Reset was read as ~56 years of seconds, then clamped to 5 minutes.
    [Fact]
    public async Task ExecuteAsync_After429WithEpochRateLimitReset_WaitsUntilThatInstant()
    {
        long resetAt = _time.GetUtcNow().AddMinutes(20).ToUnixTimeSeconds();

        List<int> reached = await ProbeAsync(
            Respond(HttpStatusCode.TooManyRequests, "X-RateLimit-Reset",
                resetAt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            1190, 1210);

        reached.Should().Equal(0, 1);
    }

    private string TraktLimit(int remaining, int untilSeconds) =>
        $$"""{"name":"UNAUTHED_API_GET_LIMIT","period":300,"limit":1000,"remaining":{{remaining}},"until":"{{_time.GetUtcNow().AddSeconds(untilSeconds):yyyy-MM-ddTHH:mm:ssZ}}"}""";

    [Fact]
    public async Task ExecuteAsync_SuccessUsingTheLastTraktCall_PausesUntilTheQuotaResets()
    {
        List<int> reached = await ProbeAsync(Respond(HttpStatusCode.OK, "X-Ratelimit", TraktLimit(0, 120)), 119, 121);

        reached.Should().Equal(0, 1);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessWithTraktCallsLeft_DoesNotPause()
    {
        List<int> reached = await ProbeAsync(Respond(HttpStatusCode.OK, "X-Ratelimit", TraktLimit(999, 120)), 1);

        reached.Should().Equal(1);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessWithNoRemainingAndResetSeconds_PausesForTheReset()
    {
        ResiliencePipeline<HttpResponseMessage> pipeline = BuildPipeline();
        HttpRequestMessage request = new(HttpMethod.Get, "https://example.com/api/data");
        await ExecuteAsync(pipeline, request, () =>
        {
            HttpResponseMessage msg = new(HttpStatusCode.OK);
            msg.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            msg.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "30");
            return msg;
        });

        _registry.IsSuppressed("example.com", out DateTimeOffset until).Should().BeTrue();
        until.Should().Be(_time.GetUtcNow().AddSeconds(30));
    }

    [Fact]
    public async Task ExecuteAsync_429WithOnlyTraktHeader_WaitsUntilItsResetTime()
    {
        List<int> reached = await ProbeAsync(
            Respond(HttpStatusCode.TooManyRequests, "X-Ratelimit", TraktLimit(0, 200)), 199, 201);

        reached.Should().Equal(0, 1);
    }

    // Regression: Bluesky sends ratelimit-reset (an epoch, no "X-" prefix) with its 429s. It was not read,
    // so a sign-in limited for a day was retried every minute.
    [Fact]
    public async Task ExecuteAsync_After429WithUnprefixedEpochRateLimitReset_WaitsUntilThatInstant()
    {
        long resetAt = _time.GetUtcNow().AddHours(18).ToUnixTimeSeconds();

        List<int> reached = await ProbeAsync(
            Respond(HttpStatusCode.TooManyRequests, "ratelimit-reset",
                resetAt.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            60, 18 * 3600 - 10, 18 * 3600 + 10);

        reached.Should().Equal(0, 0, 1);
    }

    [Fact]
    public async Task ExecuteAsync_After503WithRetryAfter_HonorsIt()
    {
        List<int> reached = await ProbeAsync(Respond(HttpStatusCode.ServiceUnavailable, "Retry-After", "120"), 119, 121);

        reached.Should().Equal(0, 1);
    }

    [Fact]
    public async Task ExecuteAsync_After429WithoutHeaders_UsesDefaultWindow()
    {
        List<int> reached = await ProbeAsync(Respond(HttpStatusCode.TooManyRequests), 59, 61);

        reached.Should().Equal(0, 1);
    }

    [Fact]
    public void Suppress_NeverShortensAnExistingWindow()
    {
        DateTimeOffset now = _time.GetUtcNow();
        _registry.Suppress("example.com", now.AddMinutes(10));
        _registry.Suppress("example.com", now.AddMinutes(1));

        _time.Advance(TimeSpan.FromMinutes(5));

        _registry.IsSuppressed("example.com", out DateTimeOffset until).Should().BeTrue();
        until.Should().Be(now.AddMinutes(10));
    }
}

public sealed class HttpLoggingTests
{
    [Theory]
    [InlineData("https://api.example.com/search?q=test&apikey=secret123", "https://api.example.com/search?q=test&apikey=***")]
    [InlineData("https://api.example.com/v1?api_key=abc&type=movie", "https://api.example.com/v1?api_key=***&type=movie")]
    [InlineData("https://api.example.com/data?token=xyz&page=1", "https://api.example.com/data?token=***&page=1")]
    [InlineData("https://api.example.com/data?secret=pass", "https://api.example.com/data?secret=***")]
    [InlineData("https://api.example.com/data?key=abc", "https://api.example.com/data?key=***")]
    [InlineData("https://api.example.com/data", "https://api.example.com/data")]
    [InlineData("https://api.example.com/data?q=watchback", "https://api.example.com/data?q=watchback")]
    public void RedactSensitiveParams_RedactsKnownSensitiveQueryParams(string input, string expected)
    {
        string result = HttpLogging.RedactSensitiveParams(new Uri(input));

        result.Should().Be(expected);
    }

    [Fact]
    public void RedactSensitiveParams_ReturnsPlaceholderForNullUri()
    {
        string result = HttpLogging.RedactSensitiveParams(null);

        result.Should().Be("(no uri)");
    }

    [Fact]
    public void RedactSensitiveParams_IsCaseInsensitiveForParamNames()
    {
        string result = HttpLogging.RedactSensitiveParams(new Uri("https://api.example.com/?APIKEY=val"));

        result.Should().Be("https://api.example.com/?APIKEY=***");
    }
}
