using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using Polly;

using WatchBack.Infrastructure.Http;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class RateLimitStrategyTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private ResiliencePipeline<HttpResponseMessage> BuildPipeline() =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRateLimitSuppression(_cache, NullLogger<RateLimitSuppressionStrategy>.Instance)
            .Build();

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