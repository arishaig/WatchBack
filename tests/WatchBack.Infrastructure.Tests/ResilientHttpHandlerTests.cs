using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using WatchBack.Infrastructure.Http;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class ResilientHttpHandlerTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private HttpClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        ResilientHttpHandler handler = new(_cache, NullLogger<ResilientHttpHandler>.Instance)
        {
            InnerHandler = new DelegatingHandlerStub(factory)
        };
        return new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
    }

    // ---- Happy path ----

    [Fact]
    public async Task SendAsync_SuccessfulGet_PassesThroughWithoutRetry()
    {
        // Arrange
        int callCount = 0;
        HttpClient client = BuildClient(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/data");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        callCount.Should().Be(1);
    }

    // ---- Retry on transient 5xx (GET) ----

    [Fact]
    public async Task SendAsync_GetWith503_RetriesUpToThreeTimesAndReturnsLastResponse()
    {
        // Arrange
        int callCount = 0;
        HttpClient client = BuildClient(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/data");

        // Assert — 1 original + 3 retries = 4 total calls
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        callCount.Should().Be(4);
    }

    // ---- No retry for non-idempotent POST ----

    [Fact]
    public async Task SendAsync_PostWith503_DoesNotRetry()
    {
        // Arrange
        int callCount = 0;
        HttpClient client = BuildClient(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });

        // Act
        HttpResponseMessage response = await client.PostAsync("/api/data", new StringContent("{}"));

        // Assert — POST is not idempotent; must not retry
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        callCount.Should().Be(1);
    }

    // ---- 429 rate-limit short-circuiting ----

    [Fact]
    public async Task SendAsync_After429WithRetryAfter_SubsequentRequestShortCircuitsWithoutNetworkCall()
    {
        // Arrange — first call returns 429 with Retry-After: 30
        int callCount = 0;
        HttpClient client = BuildClient(_ =>
        {
            callCount++;
            HttpResponseMessage msg = new(HttpStatusCode.TooManyRequests);
            msg.Headers.Add("Retry-After", "30");
            return msg;
        });

        // Act — first call triggers the rate-limit window
        HttpResponseMessage first = await client.GetAsync("/api/data");
        // Second call should be short-circuited by the cache — no network call made
        HttpResponseMessage second = await client.GetAsync("/api/data");

        // Assert
        first.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        callCount.Should().Be(1, "only the first request should reach the network");
    }

    // ---- CancellationToken propagation ----

    [Fact]
    public async Task SendAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        using CancellationTokenSource cts = new();
        HttpClient client = BuildClient(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("Cancelled", null, cts.Token);
        });

        // Act
        Func<Task> act = async () => await client.GetAsync("/api/data", cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public sealed class RedactSensitiveParamsTests
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
        string result = ResilientHttpHandler.RedactSensitiveParams(new Uri(input));

        result.Should().Be(expected);
    }

    [Fact]
    public void RedactSensitiveParams_ReturnsPlaceholderForNullUri()
    {
        string result = ResilientHttpHandler.RedactSensitiveParams(null);

        result.Should().Be("(no uri)");
    }

    [Fact]
    public void RedactSensitiveParams_IsCaseInsensitiveForParamNames()
    {
        string result = ResilientHttpHandler.RedactSensitiveParams(new Uri("https://api.example.com/?APIKEY=val"));

        result.Should().Be("https://api.example.com/?APIKEY=***");
    }
}

/// <summary>Minimal stub that wraps a factory into a non-delegating HttpMessageHandler.</summary>
internal sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> factory)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(factory(request));
    }
}
