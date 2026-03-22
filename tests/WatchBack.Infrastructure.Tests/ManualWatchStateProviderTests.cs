using FluentAssertions;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Infrastructure.WatchStateProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public class ManualWatchStateProviderTests
{
    private static ManualWatchStateProvider CreateProvider() => new();

    [Fact]
    public async Task GetCurrentMediaContextAsync_WhenNothingSet_ReturnsNull()
    {
        var provider = CreateProvider();
        var result = await provider.GetCurrentMediaContextAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_AfterSettingMovieContext_ReturnsIt()
    {
        var provider = CreateProvider();
        var context = new MediaContext(Title: "Inception", ReleaseDate: new DateTimeOffset(2010, 7, 16, 0, 0, 0, TimeSpan.Zero));

        provider.SetCurrentContext(context);
        var result = await provider.GetCurrentMediaContextAsync();

        result.Should().NotBeNull();
        result!.Title.Should().Be("Inception");
        result.Should().NotBeOfType<EpisodeContext>();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_AfterSettingEpisodeContext_ReturnsEpisodeContext()
    {
        var provider = CreateProvider();
        var context = new EpisodeContext(
            Title: "Breaking Bad",
            ReleaseDate: new DateTimeOffset(2009, 4, 12, 0, 0, 0, TimeSpan.Zero),
            EpisodeTitle: "Breakage",
            SeasonNumber: 2,
            EpisodeNumber: 5);

        provider.SetCurrentContext(context);
        var result = await provider.GetCurrentMediaContextAsync();

        var episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.Title.Should().Be("Breaking Bad");
        episode.SeasonNumber.Should().Be(2);
        episode.EpisodeNumber.Should().Be(5);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_AfterClearing_ReturnsNull()
    {
        var provider = CreateProvider();
        provider.SetCurrentContext(new MediaContext("Something", null));
        provider.SetCurrentContext(null);

        var result = await provider.GetCurrentMediaContextAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithExternalIds_ContextCarriesIds()
    {
        var provider = CreateProvider();
        var externalIds = new Dictionary<string, string>
        {
            [ExternalIdType.Imdb] = "tt0903747",
            [ExternalIdType.Tvdb] = "81189"
        };
        var context = new EpisodeContext(
            Title: "Breaking Bad", ReleaseDate: null,
            EpisodeTitle: "Pilot", SeasonNumber: 1, EpisodeNumber: 1,
            ExternalIds: externalIds);

        provider.SetCurrentContext(context);
        var result = await provider.GetCurrentMediaContextAsync();

        result!.ExternalIds.Should().ContainKey(ExternalIdType.Imdb).WhoseValue.Should().Be("tt0903747");
        result.ExternalIds.Should().ContainKey(ExternalIdType.Tvdb).WhoseValue.Should().Be("81189");
        result.ExternalIds.Should().NotContainKey(ExternalIdType.Tmdb);
    }

    [Fact]
    public async Task GetServiceHealthAsync_IsAlwaysHealthy()
    {
        var provider = CreateProvider();
        var health = await provider.GetServiceHealthAsync();
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WhenContextSet_MessageIncludesTitle()
    {
        var provider = CreateProvider();
        provider.SetCurrentContext(new MediaContext("Inception", null));

        var health = await provider.GetServiceHealthAsync();
        health.IsHealthy.Should().BeTrue();
        health.Message.Should().Contain("Inception");
    }

    [Fact]
    public void Metadata_Name_IsManual()
    {
        var provider = CreateProvider();
        provider.Metadata.Name.Should().Be("Manual");
    }

    [Fact]
    public void Metadata_SupportedExternalIds_ContainsImdbTmdbTvdb()
    {
        var provider = CreateProvider();
        var meta = provider.Metadata.Should().BeOfType<WatchStateDataProviderMetadata>().Subject;
        meta.SupportedExternalIds.Should().BeEquivalentTo(
            [ExternalIdType.Imdb, ExternalIdType.Tmdb, ExternalIdType.Tvdb]);
    }

    [Fact]
    public void ConfigSection_IsNull()
    {
        var provider = CreateProvider();
        // Manual provider has no config panel — ConfigSection must be null
        ((IDataProvider)provider).ConfigSection.Should().BeNull();
    }

    [Fact]
    public void Metadata_RequiresManualInput_IsTrue()
    {
        var provider = CreateProvider();
        var meta = provider.Metadata.Should().BeOfType<WatchStateDataProviderMetadata>().Subject;
        meta.RequiresManualInput.Should().BeTrue();
    }

    [Fact]
    public void GetConfigSchema_ReturnsEmpty()
    {
        var provider = CreateProvider();
        var fields = ((IDataProvider)provider).GetConfigSchema(_ => "", (_, _) => false);
        fields.Should().BeEmpty();
    }
}
