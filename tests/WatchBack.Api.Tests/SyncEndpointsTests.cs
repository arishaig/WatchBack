using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;

using WatchBack.Api;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Api.Tests;

public class SyncEndpointsTests : IAsyncLifetime, IDisposable
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetSync_ReturnsOkStatus()
    {
        // Act
        var response = await _client.GetAsync("/api/sync");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task GetSync_ReturnsValidSyncResponse()
    {
        // Act
        var response = await _client.GetAsync("/api/sync");
        var jsonString = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonString.Should().NotBeNull();
        jsonString.Should().Contain("status");
    }

    [Fact]
    public async Task GetSync_ResponseHasRequiredFields()
    {
        // Act
        var response = await _client.GetAsync("/api/sync");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("status");
        content.Should().Contain("timeMachineDays");
    }

    [Fact]
    public async Task GetSyncStream_ReturnsEventStream()
    {
        // Act - Use HttpCompletionOption.ResponseHeadersRead to avoid waiting for full response
        var response = await _client.GetAsync("/api/sync/stream", HttpCompletionOption.ResponseHeadersRead);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
    }
}