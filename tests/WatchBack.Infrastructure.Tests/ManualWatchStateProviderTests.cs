using FluentAssertions;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Infrastructure.WatchStateProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public class ManualWatchStateProviderTests
{
    private static ManualWatchStateProvider CreateProvider()
    {
        return new ManualWatchStateProvider();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WhenNothingSet_ReturnsNull()
    {
        ManualWatchStateProvider provider = CreateProvider();
        MediaContext? result = await provider.GetCurrentMediaContextAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_AfterSettingMovieContext_ReturnsIt()
    {
        ManualWatchStateProvider provider = CreateProvider();
        MediaContext context = new("Inception", new DateTimeOffset(2010, 7, 16, 0, 0, 0, TimeSpan.Zero));

        provider.SetCurrentContext(context);
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        result.Should().NotBeNull();
        result.Title.Should().Be("Inception");
        result.Should().NotBeOfType<EpisodeContext>();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_AfterSettingEpisodeContext_ReturnsEpisodeContext()
    {
        ManualWatchStateProvider provider = CreateProvider();
        EpisodeContext context = new(
            "Breaking Bad",
            new DateTimeOffset(2009, 4, 12, 0, 0, 0, TimeSpan.Zero),
            "Breakage",
            2,
            5);

        provider.SetCurrentContext(context);
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        EpisodeContext? episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.Title.Should().Be("Breaking Bad");
        episode.SeasonNumber.Should().Be(2);
        episode.EpisodeNumber.Should().Be(5);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_AfterClearing_ReturnsNull()
    {
        ManualWatchStateProvider provider = CreateProvider();
        provider.SetCurrentContext(new MediaContext("Something", null));
        provider.SetCurrentContext(null);

        MediaContext? result = await provider.GetCurrentMediaContextAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithExternalIds_ContextCarriesIds()
    {
        ManualWatchStateProvider provider = CreateProvider();
        Dictionary<string, string> externalIds = new()
        {
            [ExternalIdType.Imdb] = "tt0903747",
            [ExternalIdType.Tvdb] = "81189"
        };
        EpisodeContext context = new(
            "Breaking Bad", null,
            "Pilot", 1, 1,
            externalIds);

        provider.SetCurrentContext(context);
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        result!.ExternalIds.Should().ContainKey(ExternalIdType.Imdb).WhoseValue.Should().Be("tt0903747");
        result.ExternalIds.Should().ContainKey(ExternalIdType.Tvdb).WhoseValue.Should().Be("81189");
        result.ExternalIds.Should().NotContainKey(ExternalIdType.Tmdb);
    }

    [Fact]
    public async Task GetServiceHealthAsync_IsAlwaysHealthy()
    {
        ManualWatchStateProvider provider = CreateProvider();
        ServiceHealth health = await provider.GetServiceHealthAsync();
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WhenContextSet_MessageIncludesTitle()
    {
        ManualWatchStateProvider provider = CreateProvider();
        provider.SetCurrentContext(new MediaContext("Inception", null));

        ServiceHealth health = await provider.GetServiceHealthAsync();
        health.IsHealthy.Should().BeTrue();
        health.Message.Should().Contain("Inception");
    }

    [Fact]
    public void Metadata_Name_IsManual()
    {
        ManualWatchStateProvider provider = CreateProvider();
        provider.Metadata.Name.Should().Be("Manual");
    }

    [Fact]
    public void Metadata_SupportedExternalIds_ContainsImdbTmdbTvdb()
    {
        ManualWatchStateProvider provider = CreateProvider();
        WatchStateDataProviderMetadata? meta = provider.Metadata.Should().BeOfType<WatchStateDataProviderMetadata>()
            .Subject;
        meta.SupportedExternalIds.Should()
            .BeEquivalentTo(ExternalIdType.Imdb, ExternalIdType.Tmdb, ExternalIdType.Tvdb);
    }

    [Fact]
    public void ConfigSection_IsNull()
    {
        ManualWatchStateProvider provider = CreateProvider();
        // Manual provider has no config panel — ConfigSection must be null
        ((IDataProvider)provider).ConfigSection.Should().BeNull();
    }

    [Fact]
    public void Metadata_RequiresManualInput_IsTrue()
    {
        ManualWatchStateProvider provider = CreateProvider();
        WatchStateDataProviderMetadata? meta = provider.Metadata.Should().BeOfType<WatchStateDataProviderMetadata>()
            .Subject;
        meta.RequiresManualInput.Should().BeTrue();
    }

    [Fact]
    public void GetConfigSchema_ReturnsEmpty()
    {
        ManualWatchStateProvider provider = CreateProvider();
        IReadOnlyList<ProviderConfigField> fields = ((IDataProvider)provider).GetConfigSchema(_ => "", (_, _) => false);
        fields.Should().BeEmpty();
    }

    [Fact]
    public async Task ClientContext_IsIsolatedBetweenClients()
    {
        ManualWatchStateProvider provider = CreateProvider();

        provider.SetCurrentContext("client-a", new MediaContext("Alien", null));
        provider.SetCurrentContext("client-b", new MediaContext("Heat", null));

        (await provider.GetCurrentMediaContextForClientAsync("client-a"))!.Title.Should().Be("Alien");
        (await provider.GetCurrentMediaContextForClientAsync("client-b"))!.Title.Should().Be("Heat");
        (await provider.GetCurrentMediaContextForClientAsync("client-c")).Should().BeNull();
        (await provider.GetCurrentMediaContextAsync()).Should().BeNull();
    }

    [Fact]
    public async Task ClientWithoutOwnContext_FallsBackToSharedContext()
    {
        ManualWatchStateProvider provider = CreateProvider();
        provider.SetCurrentContext(new MediaContext("Shared Pick", null));
        provider.SetCurrentContext("client-a", new MediaContext("Mine", null));

        (await provider.GetCurrentMediaContextForClientAsync("client-a"))!.Title.Should().Be("Mine");
        (await provider.GetCurrentMediaContextForClientAsync("client-b"))!.Title.Should().Be("Shared Pick");
        (await provider.GetCurrentMediaContextForClientAsync(null))!.Title.Should().Be("Shared Pick");
    }

    [Fact]
    public async Task ClearingClientContext_LeavesOtherClientsAndSharedContextAlone()
    {
        ManualWatchStateProvider provider = CreateProvider();
        provider.SetCurrentContext("client-a", new MediaContext("A", null));
        provider.SetCurrentContext("client-b", new MediaContext("B", null));

        provider.SetCurrentContext("client-a", null);

        (await provider.GetCurrentMediaContextForClientAsync("client-a")).Should().BeNull();
        (await provider.GetCurrentMediaContextForClientAsync("client-b"))!.Title.Should().Be("B");
    }

    [Fact]
    public async Task OversizedViewerKey_TargetsTheSharedContext()
    {
        ManualWatchStateProvider provider = CreateProvider();

        provider.SetCurrentContext(new string('x', 500), new MediaContext("Shared", null));

        (await provider.GetCurrentMediaContextAsync())!.Title.Should().Be("Shared");
    }

    [Fact]
    public async Task ClientContexts_AreBounded_EvictingTheStalest()
    {
        ManualWatchStateProvider provider = CreateProvider();

        for (int i = 0; i < 250; i++)
        {
            provider.SetCurrentContext($"client-{i}", new MediaContext($"Title {i}", null));
        }

        (await provider.GetCurrentMediaContextForClientAsync("client-0")).Should().BeNull();
        (await provider.GetCurrentMediaContextForClientAsync("client-249"))!.Title.Should().Be("Title 249");
    }
}
