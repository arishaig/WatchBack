using System.Net;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using WatchBack.Core.Interfaces;
using WatchBack.Infrastructure.Extensions;
using WatchBack.Infrastructure.Omdb;
using WatchBack.Infrastructure.ThoughtProviders;
using WatchBack.Infrastructure.WatchStateProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class UserAgentTests
{
    // Regression: only Trakt set a User-Agent (a hardcoded "WatchBack/1.0"); every other client,
    // including the volunteer Reddit archives, sent none, since .NET adds no default.
    [Theory]
    [InlineData(nameof(RedditThoughtProvider))]
    [InlineData(nameof(BlueskyThoughtProvider))]
    [InlineData(nameof(LemmyThoughtProvider))]
    [InlineData(nameof(TraktThoughtProvider))]
    [InlineData(nameof(JellyfinWatchStateProvider))]
    [InlineData(nameof(TraktWatchStateProvider))]
    [InlineData(nameof(OmdbMediaSearchProvider))]
    public async Task EveryProviderClient_SendsDescriptiveUserAgent(string clientName)
    {
        List<string> sent = [];
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddWatchBackInfrastructure();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() =>
            new MockHttpMessageHandler(req =>
            {
                sent.Add(req.Headers.UserAgent.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK);
            })));
        await using ServiceProvider provider = services.BuildServiceProvider();

        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        await client.GetAsync(new Uri("https://example.test/"));

        sent.Should().ContainSingle().Which.Should()
            .Be(IDataProvider.UserAgent)
            .And.MatchRegex(@"^WatchBack/\d[^ ]* \(\+https://github\.com/arishaig/WatchBack\)$");
    }

    [Fact]
    public void ApplyDefaultHeaders_IsIdempotent()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "https://example.test/");

        IDataProvider.ApplyDefaultHeaders(request);
        IDataProvider.ApplyDefaultHeaders(request);

        request.Headers.UserAgent.ToString().Should().Be(IDataProvider.UserAgent);
    }
}
