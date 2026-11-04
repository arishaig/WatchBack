using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using NSubstitute;

using WatchBack.Api;
using WatchBack.Api.Endpoints;
using WatchBack.Api.Mcp;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

using Xunit;

namespace WatchBack.Api.Tests;

public sealed class McpWatchStateToolTests
{
    private static WatchBackMcpTools ToolsReturning(SyncResult result)
    {
        ISyncService sync = Substitute.For<ISyncService>();
        sync.SyncAsync(Arg.Any<IProgress<SyncProgressTick>?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>()).Returns(result);
        return new WatchBackMcpTools(sync, new SyncTrigger(), Substitute.For<IManualWatchStateProvider>(), [], [], [],
            [], Snapshot(new WatchBackOptions()), new UserConfigFile("unused.json"),
            Substitute.For<IHttpContextAccessor>());
    }

    private static IOptionsSnapshot<WatchBackOptions> Snapshot(WatchBackOptions value)
    {
        IOptionsSnapshot<WatchBackOptions> snapshot = Substitute.For<IOptionsSnapshot<WatchBackOptions>>();
        snapshot.Value.Returns(value);
        return snapshot;
    }

    // Regression: a live channel came back as an episode at season 0, episode 0.
    [Fact]
    public async Task GetWatchState_LiveBroadcast_NamesTheChannelAndNoEpisode()
    {
        LiveBroadcastContext live = new("NBC News Daily", "NBC News NOW");
        SyncResult result = new(SyncStatus.Watching, live.Title, live, [], [], 14, []);

        using JsonDocument state = JsonDocument.Parse(await ToolsReturning(result).GetWatchState(default));

        state.RootElement.GetProperty("title").GetString().Should().Be("NBC News Daily");
        state.RootElement.GetProperty("liveChannel").GetString().Should().Be("NBC News NOW");
        state.RootElement.TryGetProperty("episode", out JsonElement episode).Should().BeTrue();
        episode.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
