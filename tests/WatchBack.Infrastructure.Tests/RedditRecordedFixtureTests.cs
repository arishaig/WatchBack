using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.ThoughtProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

// Real Arctic Shift responses, recorded once (November 2026) with the exact fields= the code sends.
// No live calls: these pin down matching against what the archive actually returns.
public sealed class RedditRecordedFixtureTests : IDisposable
{
    private static readonly DateTimeOffset s_severancePremiere = new(2022, 2, 18, 0, 0, 0, TimeSpan.Zero);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly List<Uri> _requests = [];

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Reddit", name));

    private RedditThoughtProvider SeveranceProvider()
    {
        ISubredditMappingService mapping = Substitute.For<ISubredditMappingService>();
        mapping.GetSubreddits(Arg.Any<MediaContext>()).Returns(["SeveranceAppleTVPlus"]);
        IReplyTreeBuilder tree = Substitute.For<IReplyTreeBuilder>();
        tree.BuildTree(Arg.Any<IReadOnlyList<Thought>>()).Returns(call => call.Arg<IReadOnlyList<Thought>>());

        MockHttpMessageHandler handler = new(req =>
        {
            Uri uri = req.RequestUri!;
            lock (_requests)
            {
                _requests.Add(uri);
            }

            string body = uri.AbsolutePath switch
            {
                "/api/posts/search" => Fixture("severance-window-page1.json"),
                "/api/comments/tree" => Fixture("severance-s01e01-tree.json"),
                _ => """{"data":[]}"""
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });

        return new RedditThoughtProvider(new HttpClient(handler),
            new OptionsSnapshotStub<RedditOptions>(new RedditOptions()), _cache, tree, mapping,
            Substitute.For<ISubredditResolver>(), NullLogger<RedditThoughtProvider>.Instance);
    }

    // The premiere's thread is "Severance - Series Premeire Discussion Thread" (sic), flaired
    // "Series Premiere": no episode code and no episode title, so only the premiere rule finds it.
    [Fact]
    public async Task SeverancePilot_FindsTheSeriesPremiereThread_InOneSearch()
    {
        RedditThoughtProvider provider = SeveranceProvider();

        ThoughtResult? result = await provider.GetThoughtsAsync(
            new EpisodeContext("Severance", s_severancePremiere, "Good News About Hell", 1, 1));

        result!.PostTitle.Should().Be("Severance - Series Premeire Discussion Thread");
        result.PostUrl.Should().Be("https://reddit.com/r/SeveranceAppleTVPlus/comments/svtz8k/");
        result.Thoughts.Should().NotBeEmpty();
        _requests.Count(u => u.AbsolutePath == "/api/posts/search").Should().Be(1);
        _requests.Should().NotContain(u => u.Host == "api.pullpush.io");
    }

    [Fact]
    public async Task SeveranceEpisodeTwo_FindsItsDiscussion_AndNotThePremiere()
    {
        RedditThoughtProvider provider = SeveranceProvider();

        ThoughtResult? result = await provider.GetThoughtsAsync(
            new EpisodeContext("Severance", s_severancePremiere, "Half Loop", 1, 2));

        result!.PostTitle.Should().Be("Severance - 1x02 \"Half Loop\" - Episode Discussion");
        _requests.Where(u => u.AbsolutePath == "/api/comments/tree")
            .Should().NotContain(u => u.Query.Contains("svtz8k"), "the premiere thread is episode 1's");
    }

    [Fact]
    public void Resolver_PicksTheShowsCommunity_NotTheExactNameOrTheBiggest()
    {
        ArcticShiftSubredditDto[] severance = ParseSubreddits("subreddits-severance.json");
        ArcticShiftSubredditDto[] lost = ParseSubreddits("subreddits-lost.json");

        // r/Severance (exact name) is a video game's; r/SeveranceAppleTVPlus's description never says "Severance".
        ArcticShiftSubredditResolver.Pick(severance, "Severance").Should().Be("SeveranceAppleTVPlus");
        // r/lostredditors, r/lostgeneration and r/lostmedia are all bigger than r/lost.
        ArcticShiftSubredditResolver.Pick(lost, "Lost").Should().Be("lost");
    }

    private static ArcticShiftSubredditDto[] ParseSubreddits(string name) =>
        System.Text.Json.JsonSerializer.Deserialize(Fixture(name),
            RedditJsonContext.Default.ArcticShiftSubredditsResponseDto)!.Data!;
}
