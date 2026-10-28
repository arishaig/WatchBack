using FluentAssertions;

using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Core.Tests;

public class WatchViewerTests
{
    [Fact]
    public void Key_SameUserOnDifferentBrowsers_IsTheSameViewer()
    {
        WatchViewer.Key("aaaa1111aaaa1111aaaa1111aaaa1111", "browser-1")
            .Should().Be(WatchViewer.Key("AAAA1111-AAAA-1111-AAAA-1111AAAA1111", "browser-2"));
    }

    [Fact]
    public void Key_DifferentUsers_AreDifferentViewers()
    {
        WatchViewer.Key("aaaa", "browser-1").Should().NotBe(WatchViewer.Key("bbbb", "browser-1"));
    }

    [Fact]
    public void Key_WithoutUser_UsesTheBrowser()
    {
        WatchViewer.Key(null, "browser-1").Should().Be("c:browser-1");
        WatchViewer.Key("", "browser-1").Should().NotBe(WatchViewer.Key(null, "browser-2"));
    }

    [Fact]
    public void Key_UserAndBrowserIdsNeverCollide()
    {
        WatchViewer.Key("abc", null).Should().NotBe(WatchViewer.Key(null, "abc"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "bad id!")]
    [InlineData("not a user!", null)]
    public void Key_WithNoUsableIdentity_IsNull(string? user, string? client)
    {
        WatchViewer.Key(user, client).Should().BeNull();
    }
}
