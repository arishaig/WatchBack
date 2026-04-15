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

public sealed class RedditThoughtProviderNormalizeContentTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly RedditThoughtProvider _provider;

    public RedditThoughtProviderNormalizeContentTests()
    {
        IReplyTreeBuilder treeBuilder = Substitute.For<IReplyTreeBuilder>();
        ISubredditMappingService mappingService = Substitute.For<ISubredditMappingService>();
        _provider = new RedditThoughtProvider(
            new HttpClient(),
            new OptionsSnapshotStub<RedditOptions>(new RedditOptions()),
            _cache,
            treeBuilder,
            mappingService,
            NullLogger<RedditThoughtProvider>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NormalizeContent_PlainText_ReturnsUnchanged()
    {
        _provider.NormalizeContent("Just a normal comment.").Should().Be("Just a normal comment.");
    }

    [Fact]
    public void NormalizeContent_SpoilerTag_ConvertsToCanonicalForm()
    {
        _provider.NormalizeContent(">!He was dead all along!<")
            .Should().Be("<spoiler>He was dead all along</spoiler>");
    }

    [Fact]
    public void NormalizeContent_MultipleSpoilerTags_ConvertsAll()
    {
        _provider.NormalizeContent("First >!spoiler one!< and then >!spoiler two!<.")
            .Should().Be("First <spoiler>spoiler one</spoiler> and then <spoiler>spoiler two</spoiler>.");
    }

    [Fact]
    public void NormalizeContent_SpoilerWithSpaces_PreservesInnerContent()
    {
        _provider.NormalizeContent(">!  spaced content  !<")
            .Should().Be("<spoiler>  spaced content  </spoiler>");
    }

    [Fact]
    public void NormalizeContent_MultilineSpoiler_ConvertsWithNewlines()
    {
        _provider.NormalizeContent(">!line one\nline two!<")
            .Should().Be("<spoiler>line one\nline two</spoiler>");
    }

    [Fact]
    public void NormalizeContent_ExcessiveNewlines_CollapsedToDouble()
    {
        _provider.NormalizeContent("paragraph one\n\n\nparagraph two")
            .Should().Be("paragraph one\n\nparagraph two");
    }

    [Fact]
    public void NormalizeContent_ManyNewlines_CollapsedToDouble()
    {
        _provider.NormalizeContent("a\n\n\n\n\n\nb")
            .Should().Be("a\n\nb");
    }

    [Fact]
    public void NormalizeContent_TwoNewlines_LeftUnchanged()
    {
        _provider.NormalizeContent("line one\n\nline two")
            .Should().Be("line one\n\nline two");
    }

    [Fact]
    public void NormalizeContent_LeadingAndTrailingWhitespace_Trimmed()
    {
        _provider.NormalizeContent("  content  ")
            .Should().Be("content");
    }

    [Fact]
    public void NormalizeContent_SpoilerAndExcessiveNewlines_BothApplied()
    {
        _provider.NormalizeContent("Setup.\n\n\n>!The twist!<\n\n\nConclusion.")
            .Should().Be("Setup.\n\n<spoiler>The twist</spoiler>\n\nConclusion.");
    }

    [Fact]
    public void NormalizeContent_EmptyString_ReturnsEmpty()
    {
        _provider.NormalizeContent("").Should().Be("");
    }
}