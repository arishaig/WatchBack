using FluentAssertions;
using Xunit;
using NSubstitute;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Net;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Thoughts;

namespace WatchBack.Infrastructure.Tests;

public class RedditThoughtProviderTests
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

        var submissionsJson = """
            {
                "data": {
                    "children": [
                        {
                            "data": {
                                "id": "sub1",
                                "title": "Breaking Bad S01E01 Discussion",
                                "permalink": "/r/breakingbad/comments/sub1"
                            }
                        }
                    ]
                }
            }
            """;

        var commentsJson = """
            {
                "data": {
                    "children": [
                        {
                            "data": {
                                "id": "t3_sub1",
                                "body": "Great episode!",
                                "score": 100,
                                "created_utc": 1232419200,
                                "author": "user1"
                            }
                        },
                        {
                            "data": {
                                "id": "t1_c1",
                                "body": "Agree!",
                                "score": 50,
                                "created_utc": 1232419300,
                                "author": "user2",
                                "parent_id": "t3_sub1"
                            }
                        }
                    ]
                }
            }
            """;

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(submissionsJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        });

        var handler = new MockHttpMessageHandler(() => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pushshift.io") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new RedditThoughtProvider(client, Options.Create(_options), _cache, treeBuilder);

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
                "data": {
                    "children": [
                        {
                            "data": {
                                "id": "sub1",
                                "title": "Breaking Bad S01E01",
                                "permalink": "/r/breakingbad/comments/sub1"
                            }
                        }
                    ]
                }
            }
            """;

        var commentsJson = """
            {
                "data": {
                    "children": [
                        {
                            "data": {
                                "id": "t3_sub1",
                                "body": "[deleted]",
                                "score": 0,
                                "created_utc": 1232419200,
                                "author": "[deleted]"
                            }
                        },
                        {
                            "data": {
                                "id": "t1_c1",
                                "body": "[removed]",
                                "score": 0,
                                "created_utc": 1232419300,
                                "author": "AutoModerator"
                            }
                        },
                        {
                            "data": {
                                "id": "t1_c2",
                                "body": "Good episode",
                                "score": 10,
                                "created_utc": 1232419400,
                                "author": "user1"
                            }
                        }
                    ]
                }
            }
            """;

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(submissionsJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        });

        var handler = new MockHttpMessageHandler(() => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pushshift.io") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Any<IEnumerable<Thought>>()).Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new RedditThoughtProvider(client, Options.Create(_options), _cache, treeBuilder);

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
                "data": {
                    "children": [
                        {
                            "data": {
                                "id": "sub1",
                                "title": "Breaking Bad S01E01",
                                "permalink": "/r/breakingbad/comments/sub1"
                            }
                        }
                    ]
                }
            }
            """;

        var commentsJson = """
            {
                "data": {
                    "children": [
                        {
                            "data": {
                                "id": "t3_sub1",
                                "body": "Top level comment",
                                "score": 100,
                                "created_utc": 1232419200,
                                "author": "user1"
                            }
                        },
                        {
                            "data": {
                                "id": "t1_c1",
                                "body": "Reply to top level",
                                "score": 50,
                                "created_utc": 1232419300,
                                "author": "user2",
                                "parent_id": "t3_sub1"
                            }
                        }
                    ]
                }
            }
            """;

        var responses = new Queue<HttpResponseMessage>(new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(submissionsJson) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(commentsJson) }
        });

        var handler = new MockHttpMessageHandler(() => responses.Dequeue());
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pushshift.io") };

        var capturedThoughts = new List<Thought>();
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();
        treeBuilder.BuildTree(Arg.Do<IEnumerable<Thought>>(x => capturedThoughts.AddRange(x)))
            .Returns(x => ((IEnumerable<Thought>)x[0]).ToList());

        var provider = new RedditThoughtProvider(client, Options.Create(_options), _cache, treeBuilder);

        // Act
        await provider.GetThoughtsAsync(mediaContext);

        // Assert
        capturedThoughts.Should().HaveCount(2);
        var topLevel = capturedThoughts.Single(t => t.Content == "Top level comment");
        topLevel.ParentId.Should().BeNull();

        var reply = capturedThoughts.Single(t => t.Content == "Reply to top level");
        reply.ParentId.Should().Be("sub1"); // Stripped t3_ prefix
    }

    [Fact]
    public async Task GetServiceHealthAsync_ReturnsHealthy()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pushshift.io") };
        var treeBuilder = Substitute.For<IReplyTreeBuilder>();

        var provider = new RedditThoughtProvider(client, Options.Create(_options), _cache, treeBuilder);

        // Act
        var health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }
}
