using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

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

// The Reddit provider against a fake Arctic Shift / PullPush. Requests are recorded so each test can
// count searches per host and endpoint: the lookup budget is part of the contract.
public sealed class RedditThoughtProviderTests : IDisposable
{
    private const string ArcticShiftHost = "arctic-shift.photon-reddit.com";
    private const string PullPushHost = "api.pullpush.io";

    // Aired before PullPush's cutoff (May 2025), and after it.
    private static readonly DateTimeOffset s_oldAirDate = new(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_newAirDate = new(2026, 1, 9, 0, 0, 0, TimeSpan.Zero);

    private static readonly EpisodeContext s_oldEpisode = new("The Show", s_oldAirDate, "The Thing", 1, 5);
    private static readonly EpisodeContext s_newEpisode = new("The Show", s_newAirDate, "The Thing", 1, 5);

    private static readonly long s_oldAired = s_oldAirDate.ToUnixTimeSeconds() + 3600;
    private static readonly long s_newAired = s_newAirDate.ToUnixTimeSeconds() + 3600;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ConcurrentQueue<Uri> _requests = new();
    private readonly ISubredditResolver _resolver = Substitute.For<ISubredditResolver>();

    public RedditThoughtProviderTests()
    {
        _resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new SubredditResolution(null));
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- Fake API ----

    private static string Post(string id, string title, long created, int comments = 10, int score = 5,
        string? flair = null, string selftext = "", string subreddit = "TheShowTV") =>
        $$"""{"id":"{{id}}","title":{{JsonSerializer.Serialize(title)}},"subreddit":"{{subreddit}}","created_utc":{{created}},"score":{{score}},"num_comments":{{comments}},"selftext":{{JsonSerializer.Serialize(selftext)}},"link_flair_text":{{(flair is null ? "null" : JsonSerializer.Serialize(flair))}},"author":"op"}""";

    private static string Listing(params string[] posts) => $$"""{"data":[{{string.Join(",", posts)}}]}""";

    private static string Tree(string threadId, params (string Id, string? Parent, string Body)[] comments)
    {
        IEnumerable<string> nodes = comments.Select(c =>
        {
            string parent = c.Parent ?? $"t3_{threadId}";
            string body = JsonSerializer.Serialize(c.Body);
            return "{\"kind\":\"t1\",\"data\":{" +
                   $"\"id\":\"{c.Id}\",\"parent_id\":\"{parent}\",\"link_id\":\"t3_{threadId}\"," +
                   $"\"author\":\"u\",\"score\":1,\"created_utc\":1709300000,\"body\":{body},\"replies\":\"\"" + "}}";
        });
        return $"{{\"data\":[{string.Join(",", nodes)}]}}";
    }

    private const string Empty = """{"data":[]}""";
    private const string ArcticShiftTimeout = """{"data":null,"error":"Timeout. Maybe slow down a bit"}""";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static bool IsArcticShiftSearch(Uri u) =>
        u.Host == ArcticShiftHost && u.AbsolutePath == "/api/posts/search";

    private static bool IsTree(Uri u) => u.Host == ArcticShiftHost && u.AbsolutePath == "/api/comments/tree";
    private static bool IsPullPush(Uri u) => u.Host == PullPushHost;
    private static bool IsPullPushSearch(Uri u) => IsPullPush(u) && u.AbsolutePath.Contains("submission");
    private static bool IsPullPushComments(Uri u) => IsPullPush(u) && u.AbsolutePath.Contains("comment");
    private static string Query(Uri u) => Uri.UnescapeDataString(u.Query);

    private IEnumerable<Uri> ArcticShiftSearches => _requests.Where(IsArcticShiftSearch);

    private RedditThoughtProvider CreateProvider(
        Func<Uri, HttpResponseMessage> api,
        RedditOptions? options = null,
        IReadOnlyList<string>? mapped = null,
        IReplyTreeBuilder? treeBuilder = null)
    {
        ISubredditMappingService mapping = Substitute.For<ISubredditMappingService>();
        mapping.GetSubreddits(Arg.Any<MediaContext>()).Returns(mapped ?? ["TheShowTV"]);

        MockHttpMessageHandler handler = new(req =>
        {
            _requests.Enqueue(req.RequestUri!);
            return api(req.RequestUri!);
        });

        return new RedditThoughtProvider(
            new HttpClient(handler),
            new OptionsSnapshotStub<RedditOptions>(options ?? new RedditOptions()),
            _cache, treeBuilder ?? PassThroughTree(), mapping, _resolver,
            NullLogger<RedditThoughtProvider>.Instance);
    }

    private static IReplyTreeBuilder PassThroughTree()
    {
        IReplyTreeBuilder tree = Substitute.For<IReplyTreeBuilder>();
        tree.BuildTree(Arg.Any<IReadOnlyList<Thought>>()).Returns(call => call.Arg<IReadOnlyList<Thought>>());
        return tree;
    }

    // One subreddit window holding the episode's thread, comments for every thread, nothing from PullPush.
    private static Func<Uri, HttpResponseMessage> ArcticShiftWith(string listing, string? tree = null) => u =>
        IsTree(u) ? Json(tree ?? Tree("t", ("c1", null, "great episode")))
        : IsArcticShiftSearch(u) ? Json(listing)
        : Json(Empty, HttpStatusCode.TooManyRequests);

    // ---- Comments ----

    [Fact]
    public async Task GetThoughtsAsync_FindsTheEpisodeThreadAndItsComments()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(
            Listing(
                Post("other", "Who else is rewatching?", s_oldAired),
                Post("ep5", "The Show - 1x05 \"The Thing\" - Episode Discussion", s_oldAired, selftext: "Discuss!")),
            Tree("ep5", ("c1", null, "top"), ("c2", "t1_c1", "a reply"))));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.PostTitle.Should().Be("The Show - 1x05 \"The Thing\" - Episode Discussion");
        result.PostUrl.Should().Be("https://reddit.com/r/TheShowTV/comments/ep5/",
            "Arctic Shift can't return permalinks, so the URL is built from subreddit and id");
        result.Thoughts!.Select(t => t.Id).Should().BeEquivalentTo("reddit:c1", "reddit:c2");
        result.Thoughts!.Single(t => t.Id == "reddit:c1").ParentId.Should().BeNull("t3_ parents are top-level");
        result.Thoughts!.Single(t => t.Id == "reddit:c2").ParentId.Should().Be("reddit:c1");
        result.Thoughts!.Should().AllSatisfy(t => t.PostBody.Should().Be("Discuss!"));
        result.IsTransientFailure.Should().BeFalse();
    }

    [Fact]
    public async Task GetThoughtsAsync_FiltersDeletedAndRemovedComments()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(
            Listing(Post("ep5", "S01E05 Discussion", s_oldAired)),
            Tree("ep5", ("c1", null, "[deleted]"), ("c2", null, "[removed]"), ("c3", null, "Good episode"))));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.Thoughts!.Should().ContainSingle().Which.Content.Should().Be("Good episode");
    }

    [Fact]
    public async Task GetThoughtsAsync_ThreadWithNoComments_IsComplete()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(
            Listing(Post("ep5", "S01E05 Discussion", s_oldAired)), Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.IsTransientFailure.Should().BeFalse("Arctic Shift answered: the thread just has no comments");
    }

    [Fact]
    public async Task GetThoughtsAsync_TreeFailsForAnOldThread_FallsBackToPullPushComments()
    {
        const string pullPushComments = """
                                        {"data":[{"id":"pp1","body":"rescued","author":"e","score":2,"created_utc":"1709300000","parent_id":"t3_ep5"}]}
                                        """;
        RedditThoughtProvider provider = CreateProvider(u =>
            IsTree(u) ? Json(ArcticShiftTimeout)
            : IsArcticShiftSearch(u) ? Json(Listing(Post("ep5", "S01E05 Discussion", s_oldAired)))
            : IsPullPushComments(u) ? Json(pullPushComments)
            : Json(Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.Thoughts!.Should().ContainSingle().Which.Id.Should().Be("reddit:pp1");
        result.Thoughts!.Single().CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1709300000),
            "PullPush sometimes sends created_utc as a string");
        result.IsTransientFailure.Should().BeFalse("PullPush answered for the thread");
    }

    [Fact]
    public async Task GetThoughtsAsync_TreeFailsForANewThread_NeverAsksPullPush_AndFlagsTheResult()
    {
        RedditThoughtProvider provider = CreateProvider(u =>
            IsTree(u) ? Json("", HttpStatusCode.BadGateway)
            : IsArcticShiftSearch(u) ? Json(Listing(Post("ep5", "S01E05 Discussion", s_newAired)))
            : Json(Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_newEpisode);

        result!.PostTitle.Should().Be("S01E05 Discussion");
        result.IsTransientFailure.Should().BeTrue("no archive returned the thread's comments");
        _requests.Should().NotContain(u => IsPullPush(u));
    }

    [Fact]
    public async Task GetThoughtsAsync_TreeIsAskedForMaxCommentsWithoutFields()
    {
        RedditThoughtProvider provider = CreateProvider(
            ArcticShiftWith(Listing(Post("ep5", "S01E05 Discussion", s_oldAired))),
            new RedditOptions { MaxComments = 77 });

        await provider.GetThoughtsAsync(s_oldEpisode);

        Query(_requests.Single(IsTree)).Should().Contain("link_id=t3_ep5").And.Contain("limit=77")
            .And.NotContain("fields=", "the tree endpoint doesn't document field selection");
    }

    // ---- The search budget ----

    [Fact]
    public async Task GetThoughtsAsync_MappedShowWithAMatch_MakesOneSearchAndOneTreeCallPerThread()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(Listing(
            Post("a", "S01E05 Discussion", s_oldAired, comments: 300),
            Post("b", "S01E05 live thread", s_oldAired, comments: 50),
            Post("c", "1x05 thoughts", s_oldAired, comments: 20),
            Post("d", "Season 1 Episode 5 post-episode", s_oldAired, comments: 10))),
            new RedditOptions { MaxThreads = 3 });

        await provider.GetThoughtsAsync(s_oldEpisode);

        ArcticShiftSearches.Should().ContainSingle();
        _requests.Count(IsTree).Should().Be(3, "MaxThreads caps the threads read");
        _requests.Should().NotContain(u => IsPullPush(u), "Arctic Shift found the thread");
        await _resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!);
    }

    [Fact]
    public async Task GetThoughtsAsync_ListsTheSubredditWindowWithTrimmedFields_NotATitleSearch()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(Listing(Post("ep5", "S01E05", s_oldAired))));

        await provider.GetThoughtsAsync(s_oldEpisode);

        string query = Query(ArcticShiftSearches.Single());
        query.Should().Contain("subreddit=TheShowTV")
            .And.Contain($"after={s_oldAirDate.AddDays(-2).ToUnixTimeSeconds()}")
            .And.Contain($"before={s_oldAirDate.AddDays(10).ToUnixTimeSeconds()}")
            .And.Contain("sort=asc")
            .And.Contain($"fields={ArcticShiftSource.PostFields}")
            .And.NotContain("title=", "keyword search times out on active subreddits");
    }

    // Selecting a field Arctic Shift doesn't offer fails the whole request ("'permalink' is not a valid
    // field"), which would make every lookup a failure. The list is from the API's README.
    [Fact]
    public void PostFields_AreAllSelectable()
    {
        string[] selectable =
        [
            "author", "author_fullname", "author_flair_text", "created_utc", "distinguished", "id", "retrieved_on",
            "subreddit", "subreddit_id", "score", "crosspost_parent", "link_flair_text", "num_comments", "over_18",
            "post_hint", "selftext", "spoiler", "title", "url"
        ];

        ArcticShiftSource.PostFields.Split(',').Should().OnlyContain(f => selectable.Contains(f));
        ArcticShiftSource.SubredditFields.Split(',').Should().OnlyContain(f =>
            new[] { "created_utc", "description", "public_description", "display_name", "id", "over18", "retrieved_on", "subscribers", "title" }.Contains(f));
    }

    [Fact]
    public async Task GetThoughtsAsync_FullWindowWithoutAMatch_ReadsTheNextPage_ThenStops()
    {
        string[] noise = Enumerable.Range(0, ArcticShiftSource.PageSize)
            .Select(i => Post($"n{i}", $"Fan theory #{i}", s_oldAired + i)).ToArray();
        RedditThoughtProvider provider = CreateProvider(u =>
            IsTree(u) ? Json(Tree("ep5", ("c1", null, "found it")))
            : IsArcticShiftSearch(u) && Query(u).Contains($"after={s_oldAired + ArcticShiftSource.PageSize - 1}")
                ? Json(Listing(Post("ep5", "S01E05 Discussion", s_oldAired + 200_000)))
            : IsArcticShiftSearch(u) ? Json(Listing(noise))
            : Json(Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.PostTitle.Should().Be("S01E05 Discussion");
        ArcticShiftSearches.Should().HaveCount(2, "the second page starts after the first page's last post");
        _requests.Should().NotContain(u => IsPullPush(u), "the budget went on the second page");
    }

    [Fact]
    public async Task GetThoughtsAsync_NeverMakesMoreThanTwoSearches()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json(Empty),
            mapped: ["SubA", "SubB", "SubC"]);

        await provider.GetThoughtsAsync(s_oldEpisode);

        _requests.Count(u => IsArcticShiftSearch(u) || IsPullPushSearch(u)).Should().Be(2);
    }

    // ---- PullPush: only a fallback, only for old episodes ----

    [Fact]
    public async Task GetThoughtsAsync_OldEpisode_ArcticShiftEmpty_AsksPullPushOnce()
    {
        RedditThoughtProvider provider = CreateProvider(u =>
            IsTree(u) ? Json(Tree("pp5", ("c1", null, "from the tree")))
            : IsPullPushSearch(u) ? Json(Listing(Post("pp5", "S01E05 Discussion", s_oldAired)))
            : Json(Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.PostTitle.Should().Be("S01E05 Discussion");
        _requests.Count(IsPullPushSearch).Should().Be(1);
        Query(_requests.Single(IsPullPushSearch)).Should().Contain("subreddit=TheShowTV");
        _requests.Should().NotContain(u => IsPullPushComments(u), "Arctic Shift's tree had the comments");
        result.IsTransientFailure.Should().BeFalse();
    }

    [Fact]
    public async Task GetThoughtsAsync_OldEpisode_ArcticShiftFailed_PullPushFinds_IsStillFlaggedIncomplete()
    {
        RedditThoughtProvider provider = CreateProvider(u =>
            IsArcticShiftSearch(u) ? Json(ArcticShiftTimeout)
            : IsTree(u) ? Json(Tree("pp5", ("c1", null, "x")))
            : IsPullPushSearch(u) ? Json(Listing(Post("pp5", "S01E05 Discussion", s_oldAired)))
            : Json(Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.PostTitle.Should().Be("S01E05 Discussion");
        result.IsTransientFailure.Should().BeTrue("Arctic Shift, the archive of record, didn't answer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetThoughtsAsync_EpisodeAfterTheCutoff_NeverTouchesPullPush(bool arcticShiftFails)
    {
        RedditThoughtProvider provider = CreateProvider(u =>
            IsArcticShiftSearch(u) ? Json(arcticShiftFails ? ArcticShiftTimeout : Empty) : Json(Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_newEpisode);

        _requests.Should().NotContain(u => IsPullPush(u));
        result!.IsTransientFailure.Should().Be(arcticShiftFails);
    }

    [Fact]
    public async Task GetThoughtsAsync_PullPushDisabled_NeverCallsPullPush()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json(Empty), new RedditOptions { UsePullPush = false });

        await provider.GetThoughtsAsync(s_oldEpisode);

        _requests.Should().NotContain(u => IsPullPush(u));
    }

    [Fact]
    public async Task GetThoughtsAsync_ArcticShiftDisabled_OldEpisode_SearchesPullPushOnly()
    {
        RedditThoughtProvider provider = CreateProvider(u =>
                IsPullPushComments(u) ? Json("""{"data":[{"id":"c1","body":"hi","author":"a","score":1,"created_utc":1709300000,"parent_id":"t3_pp5"}]}""")
                : IsPullPushSearch(u) ? Json(Listing(Post("pp5", "S01E05 Discussion", s_oldAired)))
                : Json(Empty),
            new RedditOptions { UseArcticShift = false }, mapped: []);

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.Thoughts!.Should().ContainSingle();
        _requests.Should().NotContain(u => u.Host == ArcticShiftHost);
        Query(_requests.First(IsPullPushSearch)).Should().Contain("subreddit=theshow",
            "without Arctic Shift's subreddit search, the title is the best guess");
    }

    [Fact]
    public async Task GetThoughtsAsync_ArcticShiftDisabled_NewEpisode_MakesNoRequests()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json(Empty), new RedditOptions { UseArcticShift = false });

        ThoughtResult? result = await provider.GetThoughtsAsync(s_newEpisode);

        _requests.Should().BeEmpty("no enabled archive covers an episode this recent");
        result!.IsTransientFailure.Should().BeFalse();
    }

    // ---- Subreddit resolution ----

    [Fact]
    public async Task GetThoughtsAsync_UnmappedShow_UsesTheResolvedSubreddit()
    {
        _resolver.ResolveAsync("The Show", Arg.Any<CancellationToken>()).Returns(new SubredditResolution("ResolvedSub"));
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(Listing(Post("ep5", "S01E05", s_oldAired))),
            mapped: []);

        await provider.GetThoughtsAsync(s_oldEpisode);

        Query(ArcticShiftSearches.First()).Should().Contain("subreddit=ResolvedSub");
    }

    [Fact]
    public async Task GetThoughtsAsync_ResolutionFailed_IsNotAConfirmedMiss()
    {
        _resolver.ResolveAsync("The Show", Arg.Any<CancellationToken>())
            .Returns(new SubredditResolution(null, Failed: true));
        RedditThoughtProvider provider = CreateProvider(_ => Json(Empty), mapped: []);

        ThoughtResult? result = await provider.GetThoughtsAsync(s_newEpisode);

        result!.IsTransientFailure.Should().BeTrue();
    }

    // ---- General-subreddit fallbacks ----

    [Fact]
    public async Task GetThoughtsAsync_NoThreadInTheShowSubreddit_TriesATitleSearchOfRTelevision()
    {
        RedditThoughtProvider provider = CreateProvider(u =>
            IsTree(u) ? Json(Tree("tv5", ("c1", null, "x")))
            : IsArcticShiftSearch(u) && Query(u).Contains("subreddit=television")
                ? Json(Listing(
                    Post("tv5", "The Show - 1x05 \"The Thing\" - Episode Discussion", s_newAired, subreddit: "television"),
                    Post("tvx", "Other Show S01E05 Discussion", s_newAired, subreddit: "television")))
            : Json(Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_newEpisode);

        result!.PostUrl.Should().Contain("/r/television/comments/tv5/");
        _requests.Count(IsTree).Should().Be(1, "another show's thread in r/television is not this show's");
        Query(ArcticShiftSearches.Last()).Should().Contain("title=The Show");
    }

    // r/television's title search often times out. That must not turn a clean miss in the show's own
    // subreddit into a failure re-fetched every minute.
    [Fact]
    public async Task GetThoughtsAsync_RTelevisionTimesOut_IsStillAConfirmedMiss()
    {
        RedditThoughtProvider provider = CreateProvider(u =>
            IsArcticShiftSearch(u) && Query(u).Contains("subreddit=television") ? Json(ArcticShiftTimeout) : Json(Empty));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_newEpisode);

        result!.Thoughts.Should().BeEmpty();
        result.IsTransientFailure.Should().BeFalse();
    }

    [Fact]
    public async Task GetThoughtsAsync_Movie_SearchesRMoviesForTheOfficialDiscussion()
    {
        MediaContext movie = new("Dune: Part Two", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero));
        RedditThoughtProvider provider = CreateProvider(u =>
            IsTree(u) ? Json(Tree("dune", ("c1", null, "spice")))
            : IsArcticShiftSearch(u) ? Json(Listing(
                Post("trailer", "Dune: Part Two - Official Trailer 3", s_oldAired, subreddit: "movies"),
                Post("dune", "Official Discussion - Dune: Part Two [SPOILERS]", s_oldAired, comments: 1, subreddit: "movies"),
                Post("other", "Official Discussion - Spaceman [SPOILERS]", s_oldAired, subreddit: "movies")))
            : Json(Empty), mapped: []);

        ThoughtResult? result = await provider.GetThoughtsAsync(movie);

        result!.PostTitle.Should().Be("Official Discussion - Dune: Part Two [SPOILERS]");
        _requests.Count(IsTree).Should().Be(1, "news, trailers and other films' discussions don't count");
        Query(ArcticShiftSearches.Single()).Should().Contain("subreddit=movies")
            .And.Contain("title=Official Discussion Dune Part Two");
    }

    [Fact]
    public async Task GetThoughtsAsync_MovieWithoutAMappedSubreddit_RMoviesTimeout_IsTransient()
    {
        MediaContext movie = new("Dune: Part Two", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero));
        RedditThoughtProvider provider = CreateProvider(_ => Json(ArcticShiftTimeout), mapped: []);

        ThoughtResult? result = await provider.GetThoughtsAsync(movie);

        result!.IsTransientFailure.Should().BeTrue("r/movies was the only place to look");
    }

    // ---- Undated lookups (prefetch) ----

    [Fact]
    public async Task GetThoughtsAsync_NoAirDate_ListsNewestPosts_AndNeverTouchesPullPush()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(Listing(Post("ep5", "S01E05 Discussion", s_newAired))));
        EpisodeContext undated = s_oldEpisode with { ReleaseDate = null };

        ThoughtResult? result = await provider.GetThoughtsAsync(undated);

        result!.PostTitle.Should().Be("S01E05 Discussion");
        Query(ArcticShiftSearches.Single()).Should().Contain("sort=desc").And.NotContain("after=");
        _requests.Should().NotContain(u => IsPullPush(u));
    }

    // Regression: prefetch looks up the next episode with no air date, so only the newest posts are listed
    // and an old episode's thread is missed. That miss must not hide the thread from the dated lookup.
    [Fact]
    public async Task GetThoughtsAsync_UndatedMiss_DoesNotHideThreadsFromDatedLookup()
    {
        RedditThoughtProvider provider = CreateProvider(u =>
            IsTree(u) ? Json(Tree("ep5", ("c1", null, "x")))
            : IsArcticShiftSearch(u) && Query(u).Contains("sort=desc") ? Json(Listing(Post("new", "Season 3 trailer!", s_newAired)))
            : IsArcticShiftSearch(u) ? Json(Listing(Post("ep5", "S01E05 Discussion", s_oldAired)))
            : Json(Empty));

        ThoughtResult? prefetched = await provider.GetThoughtsAsync(s_oldEpisode with { ReleaseDate = null });
        ThoughtResult? real = await provider.GetThoughtsAsync(s_oldEpisode);

        prefetched!.Thoughts.Should().BeEmpty();
        real!.PostTitle.Should().Be("S01E05 Discussion");
    }

    [Fact]
    public async Task GetThoughtsAsync_UndatedMiss_IsStillCachedForUndatedLookups()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json(Empty));
        EpisodeContext undated = s_oldEpisode with { ReleaseDate = null };

        await provider.GetThoughtsAsync(undated);
        int afterFirst = _requests.Count;
        await provider.GetThoughtsAsync(undated);

        _requests.Count.Should().Be(afterFirst, "an undated viewer must not refetch on every poll");
    }

    // ---- Failures and progress ----

    // Regression: Arctic Shift reports timeouts as {"data":null,"error":...}. Counting that as an empty
    // result stored "no threads" for the full TTL instead of retrying soon.
    [Fact]
    public async Task GetThoughtsAsync_ArcticShiftErrorBody_IsATransientFailure()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json(ArcticShiftTimeout));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_newEpisode);

        result!.Thoughts.Should().BeEmpty();
        result.IsTransientFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsOneWeightThreeTickPerSearchAndPerThread()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(Listing(Post("ep5", "S01E05", s_oldAired))));
        ConcurrentBag<SyncProgressTick> ticks = [];

        await provider.GetThoughtsAsync(s_oldEpisode, new CapturingProgress(ticks));

        ticks.Should().HaveCount(2, "one search, one thread");
        ticks.Should().OnlyContain(t => t.Weight == 3 && t.Provider == "Reddit");
    }

    [Fact]
    public void ExpectedWeight_IsTheSearchBudgetPlusMaxThreads_TimesThree()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json(Empty), new RedditOptions { MaxThreads = 4 });

        provider.ExpectedWeight.Should().Be((2 + 4) * 3);
    }

    // Regression: when every search fails (e.g. 429), the empty result used to go uncached, so each poll
    // cycle redid the searches and re-ticked, flashing the loading bar.
    [Fact]
    public async Task GetThoughtsAsync_AfterAFailure_NextCallIsCachedAndReportsNoTicks()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json("", HttpStatusCode.TooManyRequests));
        await provider.GetThoughtsAsync(s_newEpisode);
        int afterFirst = _requests.Count;

        ConcurrentBag<SyncProgressTick> ticks = [];
        await provider.GetThoughtsAsync(s_newEpisode, new CapturingProgress(ticks));

        ticks.Should().BeEmpty("a cached failure does no work and must not arm the loading bar");
        _requests.Count.Should().Be(afterFirst);
    }

    // The loading bar is for the initial load only: a retry after the cached failure expires is silent.
    [Fact]
    public async Task GetThoughtsAsync_RetryAfterFailureExpires_ReportsNoTicks()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json("", HttpStatusCode.TooManyRequests));
        ConcurrentBag<SyncProgressTick> initial = [];
        await provider.GetThoughtsAsync(s_newEpisode, new CapturingProgress(initial));
        initial.Should().NotBeEmpty("the initial load reports progress");
        int afterFirst = _requests.Count;

        _cache.Remove(provider.GetCacheKey(s_newEpisode)); // the failure entry's TTL expiring

        ConcurrentBag<SyncProgressTick> retry = [];
        await provider.GetThoughtsAsync(s_newEpisode, new CapturingProgress(retry));

        _requests.Count.Should().BeGreaterThan(afterFirst, "the retry still hits the network");
        retry.Should().BeEmpty("retries are background work and must not arm the loading bar");
    }

    [Fact]
    public async Task GetThoughtsAsync_NullProgress_DoesNotThrow()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(Listing(Post("ep5", "S01E05", s_oldAired))));

        Func<Task<ThoughtResult?>> act = () => provider.GetThoughtsAsync(s_oldEpisode);

        await act.Should().NotThrowAsync();
    }

    // ---- Ranking ----

    [Fact]
    public async Task GetThoughtsAsync_RanksCodeMatchesAndDiscussionThreadsAboveScore()
    {
        RedditThoughtProvider provider = CreateProvider(ArcticShiftWith(Listing(
                Post("title", "\"The Thing\" was incredible", s_oldAired, comments: 900, score: 900),
                Post("meme", "S01E05 meme", s_oldAired, comments: 500, score: 800),
                Post("disc", "S01E05", s_oldAired, comments: 1, score: 1, flair: "Episode Discussion"))),
            new RedditOptions { MaxThreads = 1 });

        ThoughtResult? result = await provider.GetThoughtsAsync(s_oldEpisode);

        result!.PostUrl.Should().Contain("/disc/",
            "Arctic Shift's counts are often stale; code match and the discussion flair win");
    }

    // ---- Matching ----

    // 3 = episode code (or premiere, or r/movies official discussion), 2 = episode/movie title, 0 = no match.
    // Several cases are regressions: later-episode spoilers, prior-season threads, "Copilot" for "Pilot".
    [Theory]
    [InlineData("S01E05 Discussion", 1, 5, "The Thing", 3)]
    [InlineData("The Show 1x05", 1, 5, "The Thing", 3)]
    [InlineData("Season 3 Episode 2 One Way or Another Rewatch", 3, 2, "One Way or Another", 3)]
    [InlineData("Episode 5 Discussion", 1, 5, "The Thing", 3)]
    [InlineData("Episiode 2 \"FUD\" Discussion Thread", 1, 2, "FUD", 2)]
    [InlineData("Season 11 Episode 12 Discussion", 11, 1, "Episode 1", 0)]
    [InlineData("Episode 1 reactions (no spoilers)", 11, 1, "Episode 1", 3)]
    [InlineData("The Pitt S01E01 - \"Mass Casualty\" Discussion", 2, 1, "Mass Casualty", 0)]
    [InlineData("Mass Casualty - Episode Discussion", 2, 1, "Mass Casualty", 2)]
    [InlineData("Season 1: \"Mass Casualty\" rewatch", 2, 1, "Mass Casualty", 0)]
    [InlineData("Copilot appreciation post", 1, 1, "Pilot", 0)]
    [InlineData("Pilot - Discussion", 1, 1, "Pilot", 2)]
    [InlineData("Series Premiere Discussion", 1, 1, "Good News About Hell", 3)]
    [InlineData("Series Premiere Discussion", 2, 1, "Hello, Ms. Cobel", 0)]
    [InlineData("Season 2 Premiere Discussion", 2, 1, "Hello, Ms. Cobel", 3)]
    [InlineData("Season 1 Premiere Discussion", 2, 1, "Hello, Ms. Cobel", 0)]
    [InlineData("Premiere night!", 1, 2, "Half Loop", 0)]
    public void MatchStrength_Episodes(string title, int season, int episode, string episodeTitle, int expected)
    {
        PullPushSubmissionDto post = new("x", title, null, "Sub", 0, null, 0, false, null);
        EpisodeContext context = new("The Show", s_oldAirDate, episodeTitle, (short)season, (short)episode);

        RedditThoughtProvider.MatchStrength(post, context, context, false).Should().Be(expected);
    }

    [Fact]
    public void MatchStrength_SeriesPremiereFlairAlone_MatchesEpisodeOne()
    {
        PullPushSubmissionDto post = new("x", "Severance - Discussion Thread", null, "Sub", 0, null, 0, false, null,
            LinkFlairText: "Series Premiere");
        EpisodeContext pilot = new("Severance", s_oldAirDate, "Good News About Hell", 1, 1);

        RedditThoughtProvider.MatchStrength(post, pilot, pilot, false).Should().Be(3);
    }

    [Theory]
    [InlineData("The Daily Show - January 20, 2026", 2)]
    [InlineData("The Daily Show - January 21, 2026", 0)]
    public void MatchStrength_SpecialsMatchByDate(string title, int expected)
    {
        EpisodeContext special = new("The Daily Show", new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "", 0, 1);
        PullPushSubmissionDto post = new("x", title, null, "Sub", 0, null, 0, false, null);

        RedditThoughtProvider.MatchStrength(post, special, special, false).Should().Be(expected);
    }

    [Theory]
    [InlineData("Official Discussion - Dune: Part Two [SPOILERS]", true, 3)]
    [InlineData("Dune: Part Two - Official Trailer 3", true, 0)]
    [InlineData("Dune: Part Two - Official Trailer 3", false, 2)]
    [InlineData("Official Discussion - Spaceman [SPOILERS]", true, 0)]
    public void MatchStrength_Movies(string title, bool generalSubreddit, int expected)
    {
        MediaContext movie = new("Dune: Part Two", s_oldAirDate);
        PullPushSubmissionDto post = new("x", title, null, "movies", 0, null, 0, false, null);

        RedditThoughtProvider.MatchStrength(post, movie, null, generalSubreddit).Should().Be(expected);
    }

    // Regression: the catch-all let OperationCanceledException through, and an HttpClient timeout is one,
    // so a slow archive threw out of the provider instead of returning a transient failure.
    [Fact]
    public async Task GetThoughtsAsync_HttpClientTimeout_IsATransientFailure()
    {
        RedditThoughtProvider provider = CreateProvider(_ => throw new TaskCanceledException("timed out"));

        ThoughtResult? result = await provider.GetThoughtsAsync(s_newEpisode);

        result!.IsTransientFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetThoughtsAsync_CallerCancellation_StillPropagates()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        RedditThoughtProvider provider = CreateProvider(_ => throw new TaskCanceledException());

        Func<Task> act = () => provider.GetThoughtsAsync(s_newEpisode, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Provider self-description ----

    [Fact]
    public async Task GetServiceHealthAsync_ArcticShiftUpPullPushDown_IsHealthyAndNamesTheOutage()
    {
        RedditThoughtProvider provider = CreateProvider(u =>
            u.Host == ArcticShiftHost ? Json(Empty) : Json("", HttpStatusCode.TooManyRequests));

        ServiceHealth health = await provider.GetServiceHealthAsync();

        health.IsHealthy.Should().BeTrue("one working archive is enough");
        health.Message.Should().Contain("PullPush").And.Contain("TooManyRequests");
    }

    [Fact]
    public async Task GetServiceHealthAsync_BothArchivesDown_IsUnhealthy()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json("", HttpStatusCode.TooManyRequests));

        ServiceHealth health = await provider.GetServiceHealthAsync();

        health.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public void SelfDescription_NeedsNoCredentials()
    {
        RedditThoughtProvider provider = CreateProvider(_ => Json(Empty));

        provider.ConfigSection.Should().Be("Reddit");
        provider.IsConfigured.Should().BeTrue();
        ((IDataProvider)provider).RevealSecret("Reddit__MaxThreads").Should().BeNull();
        IReadOnlyList<ProviderConfigField> fields = provider.GetConfigSchema(_ => "", (_, _) => false);
        fields.Select(f => f.Key).Should().BeEquivalentTo("Reddit__MaxThreads", "Reddit__MaxComments");
        fields.Should().AllSatisfy(f => f.Type.Should().Be("number"));
    }

    [Fact]
    public void PullPushSubmissionDto_Stickied_DefaultsFalse_AndDeserializes()
    {
        PullPushSubmissionsResponseDto? plain = JsonSerializer.Deserialize(
            """{"data":[{"id":"a","title":"t","is_self":false}]}""",
            RedditJsonContext.Default.PullPushSubmissionsResponseDto);
        PullPushSubmissionsResponseDto? stickied = JsonSerializer.Deserialize(
            """{"data":[{"id":"a","title":"t","is_self":false,"stickied":true,"link_flair_text":"Discussion"}]}""",
            RedditJsonContext.Default.PullPushSubmissionsResponseDto);

        plain!.Data![0].Stickied.Should().BeFalse();
        stickied!.Data![0].Stickied.Should().BeTrue();
        stickied.Data[0].LinkFlairText.Should().Be("Discussion");
    }
}
