using System.Collections.Concurrent;
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

public class RedditThoughtProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private readonly RedditOptions _options = new() { MaxThreads = 3, MaxComments = 250, CacheTtlSeconds = 86400 };

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    // Returns a mapping service stub that resolves no mappings (uses derived subreddit fallback).
    private static ISubredditMappingService NoMappings()
    {
        ISubredditMappingService svc = Substitute.For<ISubredditMappingService>();
        svc.GetSubreddits(Arg.Any<MediaContext>()).Returns([]);
        return svc;
    }

    // Helper: returns submission JSON for any submission search, comment JSON for comment searches.
    private static MockHttpMessageHandler SubmissionAndCommentHandler(
        string submissionsJson, string commentsJson)
    {
        return new MockHttpMessageHandler(req =>
        {
            string path = req.RequestUri?.AbsolutePath ?? "";
            string content = path.Contains("comment") ? commentsJson : submissionsJson;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });
    }

    [Fact]
    public async Task GetThoughtsAsync_WithValidSubmissions_ReturnsComments()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        // PullPush returns { "data": [...] } — flat array
        string submissionsJson = """
                                 {
                                     "data": [
                                         {
                                             "id": "sub1",
                                             "title": "Breaking Bad S01E01 Discussion",
                                             "permalink": "/r/breakingbad/comments/sub1",
                                             "subreddit": "breakingbad",
                                             "score": 100
                                         }
                                     ]
                                 }
                                 """;

        string commentsJson = """
                              {
                                  "data": [
                                      {
                                          "id": "c1",
                                          "body": "Great episode!",
                                          "score": 100,
                                          "created_utc": 1232419200,
                                          "author": "user1",
                                          "parent_id": "t3_sub1"
                                      }
                                  ]
                              }
                              """;

        MockHttpMessageHandler handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result.Source.Should().Be("Reddit");
        result.PostTitle.Should().Contain("S01E01");
    }

    [Fact]
    public async Task GetThoughtsAsync_FiltersDeletedContent()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string submissionsJson = """
                                 {
                                     "data": [
                                         {
                                             "id": "sub1",
                                             "title": "Breaking Bad S01E01",
                                             "subreddit": "breakingbad",
                                             "score": 50
                                         }
                                     ]
                                 }
                                 """;

        string commentsJson = """
                              {
                                  "data": [
                                      {
                                          "id": "c1",
                                          "body": "[deleted]",
                                          "score": 0,
                                          "created_utc": 1232419200,
                                          "author": "[deleted]",
                                          "parent_id": "t3_sub1"
                                      },
                                      {
                                          "id": "c2",
                                          "body": "[removed]",
                                          "score": 0,
                                          "created_utc": 1232419300,
                                          "author": "AutoModerator",
                                          "parent_id": "t3_sub1"
                                      },
                                      {
                                          "id": "c3",
                                          "body": "Good episode",
                                          "score": 10,
                                          "created_utc": 1232419400,
                                          "author": "user1",
                                          "parent_id": "t3_sub1"
                                      }
                                  ]
                              }
                              """;

        MockHttpMessageHandler handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        treeBuilder.Received(1).BuildTree(Arg.Is<IEnumerable<Thought>>(t =>
            t.All(th => th.Content != "[deleted]" && th.Content != "[removed]")));
    }

    [Fact]
    public async Task GetThoughtsAsync_StripsPrefixesFromParentIds()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string submissionsJson = """
                                 {
                                     "data": [
                                         {
                                             "id": "sub1",
                                             "title": "Breaking Bad S01E01",
                                             "subreddit": "breakingbad",
                                             "score": 50
                                         }
                                     ]
                                 }
                                 """;

        string commentsJson = """
                              {
                                  "data": [
                                      {
                                          "id": "c1",
                                          "body": "Top level comment",
                                          "score": 100,
                                          "created_utc": 1232419200,
                                          "author": "user1",
                                          "parent_id": "t3_sub1"
                                      },
                                      {
                                          "id": "c2",
                                          "body": "Reply to top level",
                                          "score": 50,
                                          "created_utc": 1232419300,
                                          "author": "user2",
                                          "parent_id": "t1_c1"
                                      }
                                  ]
                              }
                              """;

        MockHttpMessageHandler handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };

        List<Thought> capturedThoughts = new();
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => capturedThoughts.AddRange(x)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        capturedThoughts.Should().HaveCount(2);
        Thought topLevel = capturedThoughts.Single(t => t.Content == "Top level comment");
        topLevel.ParentId.Should().BeNull(); // t3_ prefix = reply to submission, treated as top-level

        Thought reply = capturedThoughts.Single(t => t.Content == "Reply to top level");
        reply.ParentId.Should().Be("reddit:c1"); // Stripped t1_ prefix, prepended "reddit:"
    }

    [Fact]
    public async Task GetServiceHealthAsync_ReturnsHealthy()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void ExpectedWeight_EqualsSearchesPlusMaxThreadsTimesThree()
    {
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        RedditThoughtProvider provider = new(
            new HttpClient(),
            new OptionsSnapshotStub<RedditOptions>(_options),
            _cache, treeBuilder, NoMappings(),
            NullLogger<RedditThoughtProvider>.Instance);

        // 7 base specs + 1 episode-title spec (upper bound) + MaxThreads comment fetches, 3 weight each
        provider.ExpectedWeight.Should().Be((8 + _options.MaxThreads) * 3);
    }

    // Regression: threads that use the episode title rather than an SxxExx code (e.g.
    // r/HaltAndCatchFire's "Episiode 2 \"FUD\" Discussion Thread") were silently dropped
    // because none of the search queries matched and MatchesEpisode rejected the title.
    // The episode-title search in the show's subreddit should find and include them.
    [Fact]
    public async Task GetThoughtsAsync_EpisodeTitleThread_IsIncludedWhenNoSxxExxInTitle()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Halt and Catch Fire",
            new DateTimeOffset(2014, 6, 8, 0, 0, 0, TimeSpan.Zero),
            "FUD",
            1,
            2);

        // The SxxExx searches return nothing; the episode-title search returns the real thread
        string emptyJson = """{"data": []}""";
        string episodeTitleSubmissionsJson = """
                                             {
                                                 "data": [
                                                     {
                                                         "id": "27lyt2",
                                                         "title": "Episiode 2 \"FUD\" Discussion Thread",
                                                         "permalink": "/r/HaltAndCatchFire/comments/27lyt2",
                                                         "subreddit": "HaltAndCatchFire",
                                                         "score": 15,
                                                         "created_utc": 1402531200
                                                     }
                                                 ]
                                             }
                                             """;

        string commentsJson = """
                              {
                                  "data": [
                                      {
                                          "id": "c1",
                                          "body": "Great episode, the FUD strategy is fascinating.",
                                          "score": 8,
                                          "author": "redditor1",
                                          "created_utc": 1402534800,
                                          "parent_id": "t3_27lyt2"
                                      }
                                  ]
                              }
                              """;

        MockHttpMessageHandler handler = new(req =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            // Episode-title search: title=FUD&subreddit=haltandcatchfire
            string content = url.Contains("title=FUD") ? episodeTitleSubmissionsJson
                : url.Contains("/comment/") ? commentsJson
                : emptyJson;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        List<Thought> capturedThoughts = new();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(t => capturedThoughts.AddRange(t)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        // Act
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert — the episode-title thread must have been fetched and its comments returned
        result.Should().NotBeNull();
        result.PostTitle.Should().Contain("FUD");
        capturedThoughts.Should().ContainSingle(t => t.Content.Contains("FUD strategy"));
    }

    // Regression: "3x02 Joe's Deposition..." style titles use NxNN format and were never searched
    // for. The subreddit NxNN spec (e.g. "3x02" in r/haltandcatchfire) must find and keep them.
    [Fact]
    public async Task GetThoughtsAsync_NxNNTitleInShowSubreddit_IsIncluded()
    {
        EpisodeContext mediaContext = new(
            "Halt and Catch Fire",
            new DateTimeOffset(2015, 7, 25, 0, 0, 0, TimeSpan.Zero),
            "One Way or Another",
            3,
            2);

        string nxnnSubmissionJson = """
                                    {
                                        "data": [
                                            {
                                                "id": "vieozp",
                                                "title": "3x02 Joe's Deposition Offer to Gordon Scene Info",
                                                "permalink": "/r/HaltAndCatchFire/comments/vieozp",
                                                "subreddit": "HaltAndCatchFire",
                                                "score": 5,
                                                "created_utc": 1656892800
                                            }
                                        ]
                                    }
                                    """;

        MockHttpMessageHandler handler = new(req =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            string content = url.Contains("title=3x02") && url.Contains("haltandcatchfire") ? nxnnSubmissionJson
                : """{"data": []}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result.PostTitle.Should().Contain("3x02");
    }

    // Regression: "Season 3 Episode 2 One Way or Another Rewatch" uses the long-form episode
    // identifier and was never searched for. The "Season N Episode N" subreddit spec must find it.
    [Fact]
    public async Task GetThoughtsAsync_LongFormSeasonEpisodeTitleInShowSubreddit_IsIncluded()
    {
        EpisodeContext mediaContext = new(
            "Halt and Catch Fire",
            new DateTimeOffset(2015, 7, 25, 0, 0, 0, TimeSpan.Zero),
            "One Way or Another",
            3,
            2);

        string longFormSubmissionJson = """
                                        {
                                            "data": [
                                                {
                                                    "id": "vi8vxm",
                                                    "title": "Season 3 Episode 2 One Way or Another Rewatch",
                                                    "permalink": "/r/HaltAndCatchFire/comments/vi8vxm",
                                                    "subreddit": "HaltAndCatchFire",
                                                    "score": 12,
                                                    "created_utc": 1656892800
                                                }
                                            ]
                                        }
                                        """;

        MockHttpMessageHandler handler = new(req =>
        {
            string url = Uri.UnescapeDataString(req.RequestUri?.ToString() ?? "");
            string content = url.Contains("Season 3 Episode 2") ? longFormSubmissionJson
                : """{"data": []}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result.PostTitle.Should().Contain("Season 3 Episode 2");
    }

    // Regression: "Halt and Catch Fire 3x01/3x02 Valley of the..." in r/television uses the
    // global NxNN format. The global show-title + NxNN spec must find and keep it.
    [Fact]
    public async Task GetThoughtsAsync_GlobalNxNNTitleWithShowName_IsIncluded()
    {
        EpisodeContext mediaContext = new(
            "Halt and Catch Fire",
            new DateTimeOffset(2015, 7, 25, 0, 0, 0, TimeSpan.Zero),
            "One Way or Another",
            3,
            2);

        string globalNxNnJson = """
                                {
                                    "data": [
                                        {
                                            "id": "4z9v2i",
                                            "title": "Halt and Catch Fire 3x01/3x02 Valley of the Heart's Delight/One Way or Another",
                                            "permalink": "/r/television/comments/4z9v2i",
                                            "subreddit": "television",
                                            "score": 88,
                                            "created_utc": 1471046400
                                        }
                                    ]
                                }
                                """;

        MockHttpMessageHandler handler = new(req =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            // Global NxNN spec: "Halt+and+Catch+Fire+3x02" with no subreddit filter
            string content = url.Contains("Halt") && url.Contains("3x02") && !url.Contains("subreddit") ? globalNxNnJson
                : """{"data": []}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result.PostTitle.Should().Contain("3x01");
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsWeightThreeTickPerSearch()
    {
        // Arrange — 1 matching submission so exactly 8 search ticks + 1 comment tick
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string submissionsJson = """
                                 {
                                     "data": [
                                         {
                                             "id": "sub1",
                                             "title": "Breaking Bad S01E01",
                                             "subreddit": "breakingbad",
                                             "score": 100
                                         }
                                     ]
                                 }
                                 """;

        string commentsJson = """{"data": []}""";

        MockHttpMessageHandler handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ConcurrentBag<SyncProgressTick> ticks = new();
        CapturingProgress progress = new(ticks);

        // Act
        await provider.GetThoughtsAsync(mediaContext, progress);

        // S1E1: padded == unpadded for both fields, so after dedup:
        // 3 global + 4 subreddit + 1 episode-title = 8 specs, then +1 comment fetch = 9 ticks
        ticks.Should().HaveCount(9);
        ticks.Should().OnlyContain(t => t.Weight == 3 && t.Provider == "Reddit");
    }

    // Regression: PullPush sometimes returns created_utc as a JSON string instead of a number.
    // Before the fix, this caused a JsonException and silently dropped all comments for that query.
    [Fact]
    public async Task GetThoughtsAsync_CreatedUtcAsString_DeserializesWithoutThrowing()
    {
        // Arrange
        EpisodeContext mediaContext = new(
            "Halt and Catch Fire",
            new DateTimeOffset(2014, 6, 1, 0, 0, 0, TimeSpan.Zero),
            "I/O",
            1,
            1);

        // created_utc is a quoted string — the API variant that previously caused the crash
        string submissionsJson = """
                                 {
                                     "data": [
                                         {
                                             "id": "sub1",
                                             "title": "Halt and Catch Fire S01E01 Discussion",
                                             "permalink": "/r/HaltAndCatchFire/comments/sub1",
                                             "subreddit": "HaltAndCatchFire",
                                             "score": 42,
                                             "created_utc": "1401753600"
                                         }
                                     ]
                                 }
                                 """;

        string commentsJson = """
                              {
                                  "data": [
                                      {
                                          "id": "c1",
                                          "body": "Great pilot!",
                                          "score": 10,
                                          "author": "user1",
                                          "created_utc": "1401753700",
                                          "parent_id": "t3_sub1"
                                      }
                                  ]
                              }
                              """;

        MockHttpMessageHandler handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();

        List<Thought> capturedThoughts = new();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(t => capturedThoughts.AddRange(t)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        // Act — must not throw
        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // Assert — comment was parsed and passed through
        result.Should().NotBeNull();
        capturedThoughts.Should().ContainSingle(t => t.Content == "Great pilot!");
        capturedThoughts.Single().CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1401753700));
    }

    [Fact]
    public async Task GetThoughtsAsync_NullProgress_DoesNotThrow()
    {
        MockHttpMessageHandler handler = SubmissionAndCommentHandler("""{"data":[]}""", """{"data":[]}""");
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        EpisodeContext mediaContext = new("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        Func<Task<ThoughtResult?>> act = async () => await provider.GetThoughtsAsync(mediaContext);
        await act.Should().NotThrowAsync();
    }

    // ---- Provider self-description ----

    [Fact]
    public void ConfigSection_IsReddit()
    {
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        RedditThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(),
            NullLogger<RedditThoughtProvider>.Instance);

        provider.ConfigSection.Should().Be("Reddit");
    }

    [Fact]
    public void IsConfigured_IsAlwaysTrue()
    {
        // Reddit needs no credentials — always considered configured
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        RedditThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(),
            NullLogger<RedditThoughtProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void GetConfigSchema_ReturnsTwoNumericFields()
    {
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        RedditThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(),
            NullLogger<RedditThoughtProvider>.Instance);

        IReadOnlyList<ProviderConfigField> fields = provider.GetConfigSchema(_ => "", (_, _) => false);

        fields.Should().HaveCount(2);
        fields.Select(f => f.Key).Should().BeEquivalentTo("Reddit__MaxThreads", "Reddit__MaxComments");
        fields.Should().AllSatisfy(f => f.Type.Should().Be("number"));
    }

    [Fact]
    public void RevealSecret_ReturnsNull_NoSecretsToReveal()
    {
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        RedditThoughtProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(),
            NullLogger<RedditThoughtProvider>.Instance);

        // Reddit stores no secrets, so RevealSecret should return null for any key
        // (uses default IDataProvider.RevealSecret DIM — cast to interface to invoke)
        ((IDataProvider)provider).RevealSecret("Reddit__MaxThreads").Should().BeNull();
        ((IDataProvider)provider).RevealSecret("Reddit__MaxComments").Should().BeNull();
    }

    // ---- Movie and season-0 / dateless-episode handling ----

    [Fact]
    public async Task GetThoughtsAsync_WithMovieContext_SearchesWithMovieSuffix()
    {
        MediaContext mediaContext = new(
            "Interstellar",
            new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero));

        string movieSubmissionJson = """
                                     {
                                         "data": [
                                             {
                                                 "id": "sub1",
                                                 "title": "Interstellar [Discussion]",
                                                 "permalink": "/r/movies/comments/sub1",
                                                 "subreddit": "movies",
                                                 "score": 500
                                             }
                                         ]
                                     }
                                     """;

        List<string> searchedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            string url = Uri.UnescapeDataString(req.RequestUri?.ToString() ?? "");
            searchedUrls.Add(url);
            string content = url.Contains("/comment/") ? """{"data": []}"""
                : movieSubmissionJson;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        searchedUrls.Where(u => u.Contains("/submission/")).Should()
            .ContainSingle(u => u.Contains("Interstellar movie"));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithSeasonZeroEpisode_WithDate_SearchesByDate()
    {
        EpisodeContext mediaContext = new(
            "The Daily Show",
            new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "January 20, 2026",
            0,
            1);

        List<string> searchedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            string url = Uri.UnescapeDataString(req.RequestUri?.ToString() ?? "");
            searchedUrls.Add(url);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": []}""") };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        searchedUrls.Where(u => u.Contains("/submission/")).Should()
            .Contain(u => u.Contains("January 20, 2026"));
        searchedUrls.Where(u => u.Contains("/submission/")).Should()
            .NotContain(u => u.Contains("S00") || u.Contains("0x"));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithSeasonZeroEpisode_NoDate_SearchesByEpisodeTitle()
    {
        EpisodeContext mediaContext = new(
            "The Daily Show",
            null,
            "Special Report",
            0,
            1);

        List<string> searchedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            string url = Uri.UnescapeDataString(req.RequestUri?.ToString() ?? "");
            searchedUrls.Add(url);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": []}""") };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        searchedUrls.Where(u => u.Contains("/submission/")).Should()
            .Contain(u => u.Contains("Special Report"));
        searchedUrls.Where(u => u.Contains("/submission/")).Should()
            .NotContain(u => u.Contains("S00") || u.Contains("0x"));
    }

    // Regression: when a show is new and PullPush has no S2 content, the episode-title bypass
    // search may return S1 discussion threads whose titles explicitly name Season 1. Without the
    // MentionsDifferentSeason guard those threads slipped through the bypass filter unchecked.
    [Fact]
    public async Task GetThoughtsAsync_BypassResult_WithExplicitPriorSeasonInTitle_IsRejected()
    {
        EpisodeContext mediaContext = new(
            "The Pitt",
            new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
            "Mass Casualty",
            2,
            1);

        // S1 thread surfaced by the episode-title bypass search — title explicitly says S01E01
        string s1ThreadJson = """
                              {
                                  "data": [
                                      {
                                          "id": "abc123",
                                          "title": "The Pitt S01E01 - \"Mass Casualty\" Discussion",
                                          "permalink": "/r/ThePitt/comments/abc123",
                                          "subreddit": "ThePitt",
                                          "score": 500
                                      }
                                  ]
                              }
                              """;

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("Mass Casualty", s1ThreadJson)
            .Default("""{"data":[]}""");

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        // The episode-title bypass search must have fired (the mock DID return the S1 thread;
        // it's the MentionsDifferentSeason guard that rejects it, not an absence of results)
        handler.RecordedUris.Should().Contain(
            u => Uri.UnescapeDataString(u.ToString()).Contains("Mass Casualty"),
            "episode-title bypass search must have been attempted");

        // The S1 thread must then be rejected — no comment fetch should follow
        result.Should().NotBeNull();
        result.Thoughts.Should().BeEmpty();
        handler.RecordedUris.Should().NotContain(u => u.ToString().Contains("/comment/"));
    }

    // Complement: a bypass result whose title carries no season code must still pass through —
    // many discussion threads are titled "Episode 3 Discussion" with no SxxExx code at all.
    [Fact]
    public async Task GetThoughtsAsync_BypassResult_WithNoSeasonInTitle_IsIncluded()
    {
        EpisodeContext mediaContext = new(
            "The Pitt",
            new DateTimeOffset(2026, 1, 9, 0, 0, 0, TimeSpan.Zero),
            "Mass Casualty",
            2,
            1);

        string unseasonedThreadJson = """
                                      {
                                          "data": [
                                              {
                                                  "id": "xyz789",
                                                  "title": "Mass Casualty - Episode Discussion",
                                                  "permalink": "/r/ThePitt/comments/xyz789",
                                                  "subreddit": "ThePitt",
                                                  "score": 80
                                              }
                                          ]
                                      }
                                      """;

        string commentsJson = """
                              {
                                  "data": [
                                      {
                                          "id": "c1",
                                          "body": "Incredible premiere.",
                                          "score": 50,
                                          "author": "viewer1",
                                          "created_utc": 1736380800,
                                          "parent_id": "t3_xyz789"
                                      }
                                  ]
                              }
                              """;

        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("Mass Casualty", unseasonedThreadJson)
            .RespondTo("/comment/", commentsJson)
            .Default("""{"data":[]}""");

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result.Thoughts.Should().NotBeEmpty();
    }

    // ---- Search query parameter tests ----

    [Fact]
    public async Task GetThoughtsAsync_WithReleaseDate_AppendsAfterParameter()
    {
        DateTimeOffset releaseDate = new(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);
        long expectedEpoch = releaseDate.AddDays(-7).ToUnixTimeSeconds();

        EpisodeContext mediaContext = new("Test Show", releaseDate, "Pilot", 1, 1);

        List<string> searchedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            searchedUrls.Add(req.RequestUri?.ToString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": []}""") };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        searchedUrls.Where(u => u.Contains("/submission/")).Should()
            .OnlyContain(u => u.Contains($"after={expectedEpoch}"));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithNullReleaseDate_OmitsAfterParameter()
    {
        EpisodeContext mediaContext = new("Test Show", null, "Pilot", 1, 1);

        List<string> searchedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            searchedUrls.Add(req.RequestUri?.ToString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": []}""") };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        searchedUrls.Where(u => u.Contains("/submission/")).Should()
            .NotContain(u => u.Contains("after="));
    }

    [Fact]
    public async Task GetThoughtsAsync_SizeScalesWithMaxThreads()
    {
        RedditOptions scaledOptions = new() { MaxThreads = 10, MaxComments = 250, CacheTtlSeconds = 86400 };

        EpisodeContext mediaContext = new(
            "Test Show",
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        List<string> searchedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            searchedUrls.Add(req.RequestUri?.ToString() ?? "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": []}""") };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(scaledOptions), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        // Episode-title spec is bypass (size=40), all others are normal (size=80)
        List<string> submissionUrls = searchedUrls.Where(u => u.Contains("/submission/")).ToList();
        submissionUrls.Should().Contain(u => u.Contains("size=80"));
        submissionUrls.Should().Contain(u => u.Contains("size=40"));
    }

    [Fact]
    public async Task GetThoughtsAsync_SubmissionUrls_ContainNumCommentsFilter()
    {
        EpisodeContext mediaContext = new("Test Show", DateTimeOffset.UtcNow, "Pilot", 1, 1);

        List<string> searchedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            searchedUrls.Add(Uri.UnescapeDataString(req.RequestUri?.ToString() ?? ""));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": []}""") };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        searchedUrls.Where(u => u.Contains("/submission/")).Should()
            .OnlyContain(u => u.Contains("num_comments=>2"));
    }

    [Fact]
    public async Task GetThoughtsAsync_StickiedSubmission_BoostedAboveHigherScore()
    {
        EpisodeContext mediaContext = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            "Pilot",
            1,
            1);

        string submissionsJson = """
                                 {
                                     "data": [
                                         {
                                             "id": "highscore",
                                             "title": "Breaking Bad S01E01 - Amazing!",
                                             "permalink": "/r/breakingbad/comments/highscore",
                                             "subreddit": "breakingbad",
                                             "score": 500,
                                             "stickied": false
                                         },
                                         {
                                             "id": "stickied",
                                             "title": "Breaking Bad S01E01 Official Discussion",
                                             "permalink": "/r/breakingbad/comments/stickied",
                                             "subreddit": "breakingbad",
                                             "score": 50,
                                             "stickied": true
                                         }
                                     ]
                                 }
                                 """;

        string commentsJson = """
                              {
                                  "data": [
                                      {
                                          "id": "c1",
                                          "body": "Great episode!",
                                          "score": 10,
                                          "created_utc": 1232419200,
                                          "author": "user1",
                                          "parent_id": "t3_stickied"
                                      }
                                  ]
                              }
                              """;

        MockHttpMessageHandler handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result.PostTitle.Should().Contain("Official Discussion",
            "stickied submission should be the featured post despite lower score");
    }

    [Fact]
    public void PullPushSubmissionDto_Stickied_DefaultsFalse()
    {
        string json = """{"data":[{"id":"abc","title":"Test","score":10}]}""";
        PullPushSubmissionsResponseDto? result = System.Text.Json.JsonSerializer.Deserialize(
            json, RedditJsonContext.Default.PullPushSubmissionsResponseDto);

        result.Should().NotBeNull();
        result!.Data.Should().ContainSingle();
        result.Data![0].Stickied.Should().BeFalse();
    }

    [Fact]
    public void PullPushSubmissionDto_Stickied_DeserializesTrue()
    {
        string json = """{"data":[{"id":"abc","title":"Test","score":10,"stickied":true}]}""";
        PullPushSubmissionsResponseDto? result = System.Text.Json.JsonSerializer.Deserialize(
            json, RedditJsonContext.Default.PullPushSubmissionsResponseDto);

        result.Should().NotBeNull();
        result!.Data.Should().ContainSingle();
        result.Data![0].Stickied.Should().BeTrue();
    }

    [Fact]
    public async Task GetThoughtsAsync_WithMappedSubreddits_UsesMappedNotDerived()
    {
        EpisodeContext mediaContext = new(
            "Saturday Night Live",
            new DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero),
            "Cynthia Erivo / Doechii",
            51,
            16);

        ISubredditMappingService mappingService = Substitute.For<ISubredditMappingService>();
        mappingService.GetSubreddits(Arg.Any<MediaContext>()).Returns(["snl", "LiveFromNewYork"]);

        List<string> searchedUrls = new();
        MockHttpMessageHandler handler = new(req =>
        {
            searchedUrls.Add(Uri.UnescapeDataString(req.RequestUri?.ToString() ?? ""));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": []}""") };
        });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, mappingService, NullLogger<RedditThoughtProvider>.Instance);

        await provider.GetThoughtsAsync(mediaContext);

        List<string> submissionUrls = searchedUrls.Where(u => u.Contains("/submission/")).ToList();

        // Mapped subreddits appear in subreddit-scoped specs
        submissionUrls.Should().Contain(u => u.Contains("subreddit=snl"));
        submissionUrls.Should().Contain(u => u.Contains("subreddit=LiveFromNewYork"));

        // The derived subreddit ("saturdaynightlive") must NOT appear — mapping replaces it
        submissionUrls.Should().NotContain(u => u.Contains("saturdaynightlive"));
    }

    [Fact]
    public async Task GetThoughtsAsync_WithSeasonZeroEpisode_NoDateNoTitle_ReturnsEmpty()
    {
        EpisodeContext mediaContext = new(
            "The Daily Show",
            null,
            "",
            0,
            1);

        MockHttpMessageHandler handler = new(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": []}""") });

        HttpClient client = new(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        IReplyTreeBuilder? treeBuilder = Substitute.For<IReplyTreeBuilder>();

        RedditThoughtProvider provider = new(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache,
            treeBuilder, NoMappings(), NullLogger<RedditThoughtProvider>.Instance);

        ThoughtResult? result = await provider.GetThoughtsAsync(mediaContext);

        result.Should().NotBeNull();
        result.Thoughts.Should().BeEmpty();
    }
}
