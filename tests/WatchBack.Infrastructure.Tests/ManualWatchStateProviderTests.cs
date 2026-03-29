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
}
