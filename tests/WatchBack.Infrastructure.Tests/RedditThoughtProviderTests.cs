using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

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
    private readonly RedditOptions _options;

    public RedditThoughtProviderTests()
    {
        _options = new RedditOptions
        {
            MaxThreads = 3,
            MaxComments = 250,
            CacheTtlSeconds = 86400
        };
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    // Helper: returns submission JSON for any submission search, comment JSON for comment searches.
    private static MockHttpMessageHandler SubmissionAndCommentHandler(
        string submissionsJson, string commentsJson)
    {
        return new MockHttpMessageHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            var content = path.Contains("comment") ? commentsJson : submissionsJson;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            };
        });
    }

    [Fact]
    public async Task GetThoughtsAsync_WithValidSubmissions_ReturnsComments()
    {
        // Arrange
        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        // PullPush returns { "data": [...] } — flat array
        var submissionsJson = """
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

        var commentsJson = """
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

        var handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new RedditThoughtProvider(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("Reddit");
        result.PostTitle.Should().Contain("S01E01");
    }

    [Fact]
    public async Task GetThoughtsAsync_FiltersDeletedContent()
    {
        // Arrange
        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var submissionsJson = """
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

        var commentsJson = """
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

        var handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new RedditThoughtProvider(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        // Act
        var result = await provider.GetThoughtsAsync(mediaContext);

        // Assert
        treeBuilder.Received(1).BuildTree(Arg.Is<IEnumerable<Thought>>(t =>
            t.All(th => th.Content != "[deleted]" && th.Content != "[removed]")));
    }

    [Fact]
    public async Task GetThoughtsAsync_StripsPrefixesFromParentIds()
    {
        // Arrange
        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var submissionsJson = """
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

        var commentsJson = """
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

        var handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pullpush.io") };

        var capturedThoughts = new List<Thought>();
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => capturedThoughts.AddRange(x)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new RedditThoughtProvider(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        capturedThoughts.Should().HaveCount(2);
        var topLevel = capturedThoughts.Single(t => t.Content == "Top level comment");
        topLevel.ParentId.Should().BeNull(); // t3_ prefix = reply to submission, treated as top-level

        var reply = capturedThoughts.Single(t => t.Content == "Reply to top level");
        reply.ParentId.Should().Be("reddit:c1"); // Stripped t1_ prefix, prepended "reddit:"
    }

    [Fact]
    public async Task GetServiceHealthAsync_ReturnsHealthy()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();

        var provider = new RedditThoughtProvider(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        // Act
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void ExpectedWeight_EqualsSearchesPlusMaxThreadsTimesThree()
    {
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        var provider = new RedditThoughtProvider(
            new HttpClient(),
            new OptionsSnapshotStub<RedditOptions>(_options),
            _cache, treeBuilder,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        // Default MaxThreads = 3: (4 searches + 3 comment fetches) * 3 weight each
        provider.ExpectedWeight.Should().Be((4 + _options.MaxThreads) * 3);
    }

    [Fact]
    public async Task GetThoughtsAsync_ReportsWeightThreeTickPerSearch()
    {
        // Arrange — 1 matching submission so exactly 4 search ticks + 1 comment tick
        var mediaContext = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 1, 20, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Pilot",
            SeasonNumber: 1,
            EpisodeNumber: 1);

        var submissionsJson = """
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

        var commentsJson = """{"data": []}""";

        var handler = SubmissionAndCommentHandler(submissionsJson, commentsJson);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new RedditThoughtProvider(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        var ticks = new System.Collections.Concurrent.ConcurrentBag<SyncProgressTick>();
        var progress = new CapturingProgress(ticks);

        // Act
        await provider.GetThoughtsAsync(mediaContext, progress);

        // Assert — 4 submission searches (weight 3 each) + 1 comment fetch (weight 3) = 5 ticks
        ticks.Should().HaveCount(5);
        ticks.Should().OnlyContain(t => t.Weight == 3 && t.Provider == "Reddit");
    }

    [Fact]
    public async Task GetThoughtsAsync_NullProgress_DoesNotThrow()
    {
        var handler = SubmissionAndCommentHandler("""{"data":[]}""", """{"data":[]}""");
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pullpush.io") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        var provider = new RedditThoughtProvider(client, new OptionsSnapshotStub<RedditOptions>(_options), _cache, treeBuilder, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedditThoughtProvider>.Instance);

        var mediaContext = new EpisodeContext("Breaking Bad", DateTimeOffset.UtcNow, "Pilot", 1, 1);
        var act = async () => await provider.GetThoughtsAsync(mediaContext, null);
        await act.Should().NotThrowAsync();
    }

    private sealed class CapturingProgress(System.Collections.Concurrent.ConcurrentBag<SyncProgressTick> bag)
        : IProgress<SyncProgressTick>
    {
        public void Report(SyncProgressTick value) => bag.Add(value);
    }
}