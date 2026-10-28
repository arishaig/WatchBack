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

public sealed class RedditArcticShiftTests : IDisposable
{
    private const string ArcticShiftHost = "arctic-shift.photon-reddit.com";

    // Trimmed from a real /api/posts/search response (r/LoveIsBlindNetflix, October 2026).
    private const string WindowJson = """
                                      {"data":[
                                        {"id":"1x1lhgt","title":"Yasemin......","subreddit":"LoveIsBlindNetflix","created_utc":1791500000,"score":287,"num_comments":265,"is_self":true,"selftext":"","stickied":false,"permalink":"/r/LoveIsBlindNetflix/comments/1x1lhgt/yasemin/"},
                                        {"id":"1x5mt1l","title":"Season 11 Episode 1 Discussion","subreddit":"LoveIsBlindNetflix","created_utc":1791971000,"score":14,"num_comments":327,"is_self":true,"selftext":"Talk about it here","stickied":true,"permalink":"/r/LoveIsBlindNetflix/comments/1x5mt1l/season_11_episode_1_discussion/"}
                                      ]}
                                      """;

    // Shape of /api/comments/tree: nested Reddit listings, "more" stubs, and "" for no replies.
    private const string TreeJson = """
                                    {"data":[
                                      {"kind":"t1","data":{"id":"c1","parent_id":"t3_1x5mt1l","link_id":"t3_1x5mt1l","author":"a","score":9,"created_utc":1791971281,"body":"top one",
                                        "replies":{"kind":"Listing","data":{"children":[
                                          {"kind":"t1","data":{"id":"c2","parent_id":"t1_c1","link_id":"t3_1x5mt1l","author":"b","score":3,"created_utc":1791971400,"body":"a reply","replies":""}},
                                          {"kind":"more","data":{"count":4,"children":["x","y"],"parent_id":"t1_c1"}}
                                        ]}}}},
                                      {"kind":"t1","data":{"id":"c3","parent_id":"t3_1x5mt1l","link_id":"t3_1x5mt1l","author":"c","score":5,"created_utc":1791972002,"body":"top two","replies":""}},
                                      {"kind":"more","data":{"count":10,"children":["z"],"parent_id":"t3_1x5mt1l"}}
                                    ]}
                                    """;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private static readonly EpisodeContext s_episode = new(
        "Love Is Blind", new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero), "Episode 1", 11, 1);

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ISubredditMappingService Mapped(string subreddit)
    {
        ISubredditMappingService svc = Substitute.For<ISubredditMappingService>();
        svc.GetSubreddits(Arg.Any<MediaContext>()).Returns([subreddit]);
        return svc;
    }

    private static IReplyTreeBuilder PassThroughTree()
    {
        IReplyTreeBuilder tree = Substitute.For<IReplyTreeBuilder>();
        tree.BuildTree(Arg.Any<IReadOnlyList<Thought>>()).Returns(call => call.Arg<IReadOnlyList<Thought>>());
        return tree;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private RedditThoughtProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> arcticShift,
        Func<HttpRequestMessage, HttpResponseMessage> pullPush,
        RedditOptions? options = null)
    {
        MockHttpMessageHandler handler = new(req =>
            req.RequestUri!.Host == ArcticShiftHost ? arcticShift(req) : pullPush(req));
        return new RedditThoughtProvider(
            new HttpClient(handler),
            new OptionsSnapshotStub<RedditOptions>(options ?? new RedditOptions()),
            _cache, PassThroughTree(), Mapped("LoveIsBlindNetflix"),
            NullLogger<RedditThoughtProvider>.Instance);
    }

    private static HttpResponseMessage TooManyRequests(HttpRequestMessage _) => new(HttpStatusCode.TooManyRequests);

    private static HttpResponseMessage ArcticShiftOk(HttpRequestMessage req) =>
        Json(req.RequestUri!.AbsolutePath.Contains("comments") ? TreeJson : WindowJson);

    [Fact]
    public async Task GetThoughtsAsync_PullPushDown_FindsEpisodeThreadViaArcticShift()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftOk, TooManyRequests);

        ThoughtResult? result = await provider.GetThoughtsAsync(s_episode);

        result!.PostTitle.Should().Be("Season 11 Episode 1 Discussion");
        result.Thoughts!.Select(t => t.Id).Should().BeEquivalentTo("reddit:c1", "reddit:c2", "reddit:c3");
        result.Thoughts!.Single(t => t.Id == "reddit:c2").ParentId.Should().Be("reddit:c1");
        result.Thoughts!.Single(t => t.Id == "reddit:c1").ParentId.Should().BeNull("t3_ parents are top-level");
    }

    [Fact]
    public async Task GetThoughtsAsync_ArcticShiftSearch_ListsSubredditWindowWithoutTitleKeyword()
    {
        List<Uri> arcticShiftUris = [];
        RedditThoughtProvider provider = CreateProvider(req =>
        {
            arcticShiftUris.Add(req.RequestUri!);
            return ArcticShiftOk(req);
        }, TooManyRequests);

        await provider.GetThoughtsAsync(s_episode);

        Uri search = arcticShiftUris.First(u => u.AbsolutePath.EndsWith("/posts/search", StringComparison.Ordinal));
        search.Query.Should().Contain("subreddit=LoveIsBlindNetflix")
            .And.Contain($"after={s_episode.ReleaseDate!.Value.AddDays(-2).ToUnixTimeSeconds()}")
            .And.Contain($"before={s_episode.ReleaseDate!.Value.AddDays(10).ToUnixTimeSeconds()}")
            .And.NotContain("title=", "keyword search times out on large subreddits");
    }

    [Fact]
    public async Task GetThoughtsAsync_BothArchives_MergesThreadsAndDedupesById()
    {
        const string pullPushSubs = """
                                    {"data":[
                                      {"id":"1x5mt1l","title":"Season 11 Episode 1 Discussion","subreddit":"LoveIsBlindNetflix","score":14,"is_self":false},
                                      {"id":"pp2","title":"Love Is Blind S11E01 thoughts","subreddit":"LoveIsBlindNetflix","score":50,"is_self":false}
                                    ]}
                                    """;
        const string pullPushComments = """
                                        {"data":[{"id":"pp-c1","body":"from pullpush","author":"d","score":1,"created_utc":1791971500,"parent_id":"t3_pp2"}]}
                                        """;
        List<string> pullPushThreadsAsked = [];
        RedditThoughtProvider provider = CreateProvider(ArcticShiftOk, req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("comment"))
            {
                pullPushThreadsAsked.Add(req.RequestUri.Query);
                return Json(pullPushComments);
            }

            return Json(pullPushSubs);
        });

        ThoughtResult? result = await provider.GetThoughtsAsync(s_episode);

        result!.Thoughts!.Select(t => t.Id).Should().Contain(["reddit:c1", "reddit:pp-c1"]);
        result.Thoughts!.Count(t => t.Id == "reddit:c1").Should().Be(1, "a thread found by both is fetched once");
        pullPushThreadsAsked.Should().ContainSingle("only the PullPush-only thread asks PullPush for comments")
            .Which.Should().Contain("link_id=pp2");
    }

    [Fact]
    public async Task GetThoughtsAsync_ArcticShiftTreeFails_FallsBackToPullPushComments()
    {
        const string pullPushComments = """
                                        {"data":[{"id":"fallback","body":"rescued","author":"e","score":2,"created_utc":1791971500,"parent_id":"t3_1x5mt1l"}]}
                                        """;
        RedditThoughtProvider provider = CreateProvider(
            req => req.RequestUri!.AbsolutePath.Contains("comments")
                ? Json("""{"data":null,"error":"Timeout. Maybe slow down a bit"}""")
                : Json(WindowJson),
            req => req.RequestUri!.AbsolutePath.Contains("comment")
                ? Json(pullPushComments)
                : TooManyRequests(req));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_episode);

        result!.Thoughts!.Should().ContainSingle().Which.Id.Should().Be("reddit:fallback");
    }

    // Regression: Arctic Shift reports timeouts as HTTP 200 with {"data":null,"error":...}. Counting that
    // as a successful (empty) search cached "no threads" for the full TTL instead of retrying soon.
    [Fact]
    public async Task GetThoughtsAsync_ArcticShiftErrorBody_IsCachedAsTransientFailure()
    {
        int arcticShiftRequests = 0;
        RedditOptions options = new() { CacheTtlSeconds = 1 }; // confirmed misses expire after 1s; failures after 60s
        RedditThoughtProvider provider = CreateProvider(_ =>
        {
            Interlocked.Increment(ref arcticShiftRequests);
            return Json("""{"data":null,"error":"Timeout. Maybe slow down a bit"}""");
        }, TooManyRequests, options);

        ThoughtResult? first = await provider.GetThoughtsAsync(s_episode);
        int requestsAfterFirst = arcticShiftRequests;
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        await provider.GetThoughtsAsync(s_episode);

        first!.Thoughts!.Should().BeEmpty();
        arcticShiftRequests.Should().Be(requestsAfterFirst, "a timeout is cached with the short failure TTL, not the miss TTL");
        // The long-TTL entry would have expired by now and triggered a refetch.
    }

    [Fact]
    public async Task GetThoughtsAsync_ArcticShiftDisabled_NeverCallsArcticShift()
    {
        int arcticShiftRequests = 0;
        RedditThoughtProvider provider = CreateProvider(req =>
        {
            Interlocked.Increment(ref arcticShiftRequests);
            return ArcticShiftOk(req);
        }, TooManyRequests, new RedditOptions { UseArcticShift = false });

        await provider.GetThoughtsAsync(s_episode);

        arcticShiftRequests.Should().Be(0);
    }

    [Fact]
    public async Task GetThoughtsAsync_PullPushDisabled_NeverCallsPullPush()
    {
        int pullPushRequests = 0;
        RedditThoughtProvider provider = CreateProvider(ArcticShiftOk, req =>
        {
            Interlocked.Increment(ref pullPushRequests);
            return TooManyRequests(req);
        }, new RedditOptions { UsePullPush = false });

        ThoughtResult? result = await provider.GetThoughtsAsync(s_episode);

        pullPushRequests.Should().Be(0);
        result!.Thoughts!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetServiceHealthAsync_PullPushDownButArcticShiftUp_IsHealthyAndNamesTheOutage()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json("""{"data":[]}"""), TooManyRequests);

        ServiceHealth health = await provider.GetServiceHealthAsync();

        health.IsHealthy.Should().BeTrue("one working archive is enough");
        health.Message.Should().Contain("PullPush").And.Contain("TooManyRequests");
    }

    [Fact]
    public async Task GetServiceHealthAsync_BothArchivesDown_IsUnhealthy()
    {
        RedditThoughtProvider provider = CreateProvider(TooManyRequests, TooManyRequests);

        ServiceHealth health = await provider.GetServiceHealthAsync();

        health.IsHealthy.Should().BeFalse();
    }
}
