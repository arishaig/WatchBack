using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Services;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class SummaryEndpointsTests
{
    private const string Username = "admin";
    private const string Password = "test-password-123";

    private static readonly Thought s_all = new("1", null, null, "All", null, [], "a", 1, DateTimeOffset.UtcNow, "S", []);
    private static readonly Thought s_window = new("2", null, null, "Window", null, [], "a", 1, DateTimeOffset.UtcNow, "S", []);

    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient Client, IThoughtSummaryService Summaries)>
        StartAsync(Dictionary<string, string?> settings, bool modelConfigured, SyncStatus status = SyncStatus.Watching)
    {
        IThoughtSummaryService summaries = Substitute.For<IThoughtSummaryService>();
        summaries.SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<Thought>>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThoughtSummary(SummaryStatus.Ready, "Loved it.", 5));
        ISummarizer summarizer = Substitute.For<ISummarizer>();
        summarizer.IsConfigured.Returns(modelConfigured);
        ISyncService sync = Substitute.For<ISyncService>();
        sync.SyncAsync(Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new SyncResult(status, "Breaking Bad", null, [s_all], [s_window], 14, []));

        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            string hash = new PasswordHasher<string>().HashPassword("", Password);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Auth:Username"] = Username, ["Auth:PasswordHash"] = hash, ["Auth:OnboardingComplete"] = "true"
                }.Concat(settings).ToDictionary()));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISummarizer>();
                services.RemoveAll<IThoughtSummaryService>();
                services.RemoveAll<ISyncService>();
                services.AddScoped(_ => summarizer);
                services.AddSingleton(summaries);
                services.AddScoped(_ => sync);
            });
        });
        HttpClient client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/login", new { username = Username, password = Password });
        return (factory, client, summaries);
    }

    [Fact]
    public async Task GetSummary_ModelSetUpAndNoSavedMode_WritesTheSummary()
    {
        (WebApplicationFactory<Program> factory, HttpClient client, IThoughtSummaryService summaries) =
            await StartAsync([], true);
        using (factory)
        using (client)
        {
            JsonElement json = await client.GetFromJsonAsync<JsonElement>("/api/summary");

            json.GetProperty("status").GetString().Should().Be("ready");
            json.GetProperty("text").GetString().Should().Be("Loved it.");
            json.GetProperty("commentCount").GetInt32().Should().Be(5);
            await summaries.Received(1).SummarizeAsync("Breaking Bad",
                Arg.Is<IReadOnlyList<Thought>>(t => t.Single().Content == "All"), "en", Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task GetSummary_TimeMachineView_SummarizesOnlyTheTimeMachineThoughts()
    {
        (WebApplicationFactory<Program> factory, HttpClient client, IThoughtSummaryService summaries) =
            await StartAsync(new() { ["WatchBack:TranslateTargetLanguage"] = "es" }, true);
        using (factory)
        using (client)
        {
            await client.GetAsync("/api/summary?timeMachine=true");

            await summaries.Received(1).SummarizeAsync(Arg.Any<string>(),
                Arg.Is<IReadOnlyList<Thought>>(t => t.Single().Content == "Window"), "es", Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "hidden")]
    [InlineData(false, "hidden")]
    public async Task GetSummary_Hidden_ReturnsHiddenWithoutSyncingOrAskingTheModel(bool configured, string saved)
    {
        (WebApplicationFactory<Program> factory, HttpClient client, IThoughtSummaryService summaries) =
            await StartAsync(new() { ["WatchBack:SummaryMode"] = saved }, configured);
        using (factory)
        using (client)
        {
            JsonElement json = await client.GetFromJsonAsync<JsonElement>("/api/summary");

            json.GetProperty("status").GetString().Should().Be("hidden");
            await summaries.DidNotReceiveWithAnyArgs().SummarizeAsync(default!, default!, default!);
        }
    }

    [Fact]
    public async Task GetSummary_OpenModeWithoutAModel_StillAsksTheServiceSoTheViewerSeesWhySetUpIsNeeded()
    {
        (WebApplicationFactory<Program> factory, HttpClient client, IThoughtSummaryService summaries) =
            await StartAsync(new() { ["WatchBack:SummaryMode"] = "open" }, false);
        using (factory)
        using (client)
        {
            await client.GetAsync("/api/summary");

            await summaries.Received(1).SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<Thought>>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task GetSummary_NothingPlaying_IsEmpty()
    {
        (WebApplicationFactory<Program> factory, HttpClient client, IThoughtSummaryService summaries) =
            await StartAsync([], true, SyncStatus.Idle);
        using (factory)
        using (client)
        {
            JsonElement json = await client.GetFromJsonAsync<JsonElement>("/api/summary");

            json.GetProperty("status").GetString().Should().Be("empty");
            await summaries.DidNotReceiveWithAnyArgs().SummarizeAsync(default!, default!, default!);
        }
    }

    [Fact]
    public async Task GetSummary_Unauthenticated_IsRejected()
    {
        (WebApplicationFactory<Program> factory, HttpClient client, _) = await StartAsync([], true);
        using (factory)
        using (client)
        {
            using HttpClient anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            HttpResponseMessage response = await anonymous.GetAsync("/api/summary");

            response.StatusCode.Should().BeOneOf(System.Net.HttpStatusCode.Unauthorized,
                System.Net.HttpStatusCode.Redirect, System.Net.HttpStatusCode.Forbidden);
        }
    }
}
