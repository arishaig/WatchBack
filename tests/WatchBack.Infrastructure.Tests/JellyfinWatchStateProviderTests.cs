using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.WatchStateProviders;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public class JellyfinWatchStateProviderTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private readonly JellyfinOptions _options = new()
    {
        BaseUrl = "http://jellyfin:8096",
        ApiKey = "test-api-key",
        CacheTtlSeconds = 10
    };

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithNoActiveSessions_ReturnsNull()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithActiveSessions_ReturnsEpisodeContext()
    {
        // Arrange
        string sessionJson = """
                             [
                                 {
                                     "Id": "session1",
                                     "NowPlayingItem": {
                                         "Name": "Breaking Bad",
                                         "SeriesName": "Breaking Bad",
                                         "ParentIndexNumber": 3,
                                         "IndexNumber": 7,
                                         "PremiereDate": "2010-03-28T00:00:00Z"
                                     }
                                 }
                             ]
                             """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<EpisodeContext>();
        EpisodeContext episode = (EpisodeContext)result;
        episode.Title.Should().Be("Breaking Bad");
        episode.EpisodeTitle.Should().Be("Breaking Bad");
        episode.SeasonNumber.Should().Be(3);
        episode.EpisodeNumber.Should().Be(7);
        episode.ReleaseDate.Should().HaveValue();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithoutPremiereDate_ReturnsNullReleaseDate()
    {
        // Arrange
        string sessionJson = """
                             [
                                 {
                                     "Id": "session1",
                                     "NowPlayingItem": {
                                         "Name": "Breaking Bad",
                                         "SeriesName": "Breaking Bad",
                                         "Season": 1,
                                         "IndexNumber": 1
                                     }
                                 }
                             ]
                             """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().NotBeNull();
        result.ReleaseDate.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithUnauthorized_ReturnsNull()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_SecondCall_UsesCacheWithinTtl()
    {
        // Arrange
        string sessionJson = """
                             [
                                 {
                                     "Id": "session1",
                                     "NowPlayingItem": {
                                         "Name": "Breaking Bad",
                                         "SeriesName": "Breaking Bad",
                                         "Season": 1,
                                         "IndexNumber": 1
                                     }
                                 }
                             ]
                             """;

        int callCount = 0;
        MockHttpMessageHandler handler = new(() =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) };
        });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result1 = await provider.GetCurrentMediaContextAsync();
        MediaContext? result2 = await provider.GetCurrentMediaContextAsync();

        // Assert
        callCount.Should().Be(1); // Second call uses cache
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
    }

    private const string TwoUserSessionsJson = """
                                                [
                                                    {
                                                        "Id": "s1",
                                                        "UserId": "aaaa1111aaaa1111aaaa1111aaaa1111",
                                                        "NowPlayingItem": { "Name": "Pilot", "SeriesName": "Show A", "ParentIndexNumber": 1, "IndexNumber": 1 }
                                                    },
                                                    {
                                                        "Id": "s2",
                                                        "UserId": "bbbb2222bbbb2222bbbb2222bbbb2222",
                                                        "NowPlayingItem": { "Name": "Finale", "SeriesName": "Show B", "ParentIndexNumber": 2, "IndexNumber": 9 }
                                                    },
                                                    {
                                                        "Id": "s3",
                                                        "UserId": "cccc3333cccc3333cccc3333cccc3333"
                                                    }
                                                ]
                                                """;

    private JellyfinWatchStateProvider CreateProvider(string body, Action? onRequest = null)
    {
        MockHttpMessageHandler handler = new(() =>
        {
            onRequest?.Invoke();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        return new JellyfinWatchStateProvider(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithUserId_FollowsThatUsersSession()
    {
        JellyfinWatchStateProvider provider = CreateProvider(TwoUserSessionsJson);

        MediaContext? result = await provider.GetCurrentMediaContextAsync("bbbb2222bbbb2222bbbb2222bbbb2222");

        result.Should().BeOfType<EpisodeContext>().Which.Title.Should().Be("Show B");
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithDashedUpperCaseUserId_StillMatches()
    {
        JellyfinWatchStateProvider provider = CreateProvider(TwoUserSessionsJson);

        MediaContext? result = await provider.GetCurrentMediaContextAsync("AAAA1111-AAAA-1111-AAAA-1111AAAA1111");

        result.Should().BeOfType<EpisodeContext>().Which.Title.Should().Be("Show A");
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithUserNotPlaying_ReturnsNullInsteadOfAnotherUsersSession()
    {
        JellyfinWatchStateProvider provider = CreateProvider(TwoUserSessionsJson);

        MediaContext? result = await provider.GetCurrentMediaContextAsync("cccc3333cccc3333cccc3333cccc3333");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithoutUserId_KeepsFirstPlayingSession()
    {
        JellyfinWatchStateProvider provider = CreateProvider(TwoUserSessionsJson);

        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        result.Should().BeOfType<EpisodeContext>().Which.Title.Should().Be("Show A");
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_CachesPerUser()
    {
        int calls = 0;
        JellyfinWatchStateProvider provider = CreateProvider(TwoUserSessionsJson, () => calls++);

        MediaContext? a = await provider.GetCurrentMediaContextAsync("aaaa1111aaaa1111aaaa1111aaaa1111");
        MediaContext? b = await provider.GetCurrentMediaContextAsync("bbbb2222bbbb2222bbbb2222bbbb2222");
        MediaContext? aAgain = await provider.GetCurrentMediaContextAsync("aaaa1111aaaa1111aaaa1111aaaa1111");

        a.Should().BeOfType<EpisodeContext>().Which.Title.Should().Be("Show A");
        b.Should().BeOfType<EpisodeContext>().Which.Title.Should().Be("Show B");
        aAgain.Should().BeSameAs(a);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetUsersAsync_ReturnsNormalizedUsersSortedByName()
    {
        JellyfinWatchStateProvider provider = CreateProvider("""
                                                            [
                                                                { "Id": "BBBB2222-bbbb-2222-bbbb-2222bbbb2222", "Name": "zoe" },
                                                                { "Id": "aaaa1111aaaa1111aaaa1111aaaa1111", "Name": "Alice" },
                                                                { "Id": "", "Name": "Broken" }
                                                            ]
                                                            """);

        IReadOnlyList<WatchUser> users = await provider.GetUsersAsync();

        users.Should().Equal(
            new WatchUser("aaaa1111aaaa1111aaaa1111aaaa1111", "Alice"),
            new WatchUser("bbbb2222bbbb2222bbbb2222bbbb2222", "zoe"));
    }

    [Fact]
    public async Task GetUsersAsync_WhenServerErrors_ReturnsEmpty()
    {
        MockHttpMessageHandler handler = new(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        (await provider.GetUsersAsync()).Should().BeEmpty();
    }

    [Fact]
    public void Metadata_SupportedExternalIds_ContainsImdbTmdbTvdb()
    {
        // Arrange
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Assert
        provider.Metadata.Should().BeOfType<WatchStateDataProviderMetadata>();
        WatchStateDataProviderMetadata meta = (WatchStateDataProviderMetadata)provider.Metadata;
        meta.SupportedExternalIds.Should()
            .BeEquivalentTo(ExternalIdType.Imdb, ExternalIdType.Tmdb, ExternalIdType.Tvdb);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithAllProviderIds_PopulatesExternalIds()
    {
        // Arrange
        string sessionJson = """
                             [
                                 {
                                     "Id": "session1",
                                     "NowPlayingItem": {
                                         "Name": "Ozymandias",
                                         "SeriesName": "Breaking Bad",
                                         "ParentIndexNumber": 5,
                                         "IndexNumber": 14,
                                         "ProviderIds": {
                                             "Imdb": "tt0903747",
                                             "Tmdb": "1396",
                                             "Tvdb": "81189"
                                         }
                                     }
                                 }
                             ]
                             """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        EpisodeContext? episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().NotBeNull();
        episode.ExternalIds![ExternalIdType.Imdb].Should().Be("tt0903747");
        episode.ExternalIds[ExternalIdType.Tmdb].Should().Be("1396");
        episode.ExternalIds[ExternalIdType.Tvdb].Should().Be("81189");
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithPartialProviderIds_PopulatesOnlyAvailableIds()
    {
        // Arrange
        string sessionJson = """
                             [
                                 {
                                     "Id": "session1",
                                     "NowPlayingItem": {
                                         "Name": "Ozymandias",
                                         "SeriesName": "Breaking Bad",
                                         "ParentIndexNumber": 5,
                                         "IndexNumber": 14,
                                         "ProviderIds": {
                                             "Tvdb": "81189"
                                         }
                                     }
                                 }
                             ]
                             """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        EpisodeContext? episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().NotBeNull();
        episode.ExternalIds![ExternalIdType.Tvdb].Should().Be("81189");
        episode.ExternalIds.Should().NotContainKey(ExternalIdType.Imdb);
        episode.ExternalIds.Should().NotContainKey(ExternalIdType.Tmdb);
    }

    [Fact]
    public async Task GetCurrentMediaContextAsync_WithNoProviderIds_ReturnsNullExternalIds()
    {
        // Arrange
        string sessionJson = """
                             [
                                 {
                                     "Id": "session1",
                                     "NowPlayingItem": {
                                         "Name": "Breaking Bad",
                                         "SeriesName": "Breaking Bad",
                                         "ParentIndexNumber": 1,
                                         "IndexNumber": 1
                                     }
                                 }
                             ]
                             """;

        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        // Assert
        EpisodeContext? episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().BeNull();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WithSuccess_ReturnsHealthy()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task GetServiceHealthAsync_WithFailure_ReturnsUnhealthy()
    {
        // Arrange
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act
        ServiceHealth health = await provider.GetServiceHealthAsync();

        // Assert
        health.IsHealthy.Should().BeFalse();
    }

    // ---- Provider self-description ----

    [Fact]
    public void ConfigSection_IsJellyfin()
    {
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.ConfigSection.Should().Be("Jellyfin");
    }

    [Fact]
    public void IsConfigured_WhenApiKeySet_IsTrue()
    {
        JellyfinOptions opts = new()
        {
            BaseUrl = _options.BaseUrl,
            ApiKey = "my-key",
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(opts), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenApiKeyEmpty_IsFalse()
    {
        JellyfinOptions opts = new()
        {
            BaseUrl = _options.BaseUrl,
            ApiKey = "",
            CacheTtlSeconds = _options.CacheTtlSeconds
        };
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(opts), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void GetConfigSchema_ReturnsBothFields()
    {
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        IReadOnlyList<ProviderConfigField> fields = provider.GetConfigSchema(_ => "", (_, _) => false);

        fields.Should().HaveCount(2);
        fields.Select(f => f.Key).Should().BeEquivalentTo("Jellyfin__BaseUrl", "Jellyfin__ApiKey");
    }

    [Fact]
    public void GetConfigSchema_ApiKeyField_IsPasswordType()
    {
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        ProviderConfigField apiKeyField = provider.GetConfigSchema(_ => "", (_, _) => false)
            .First(f => f.Key == "Jellyfin__ApiKey");

        apiKeyField.Type.Should().Be("password");
        apiKeyField.Value.Should().BeEmpty("password fields must never expose their stored value");
    }

    [Fact]
    public async Task TestConnectionAsync_WithValidServer_ReturnsHealthy()
    {
        string infoJson = """{"Version":"10.9.11","ServerName":"My Jellyfin"}""";
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(infoJson) });
        HttpClient client = new(handler);
        JellyfinWatchStateProvider provider = new(
            client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        ServiceHealth health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Jellyfin__BaseUrl"] = _options.BaseUrl,
            ["Jellyfin__ApiKey"] = _options.ApiKey
        });

        health.IsHealthy.Should().BeTrue();
        health.Message.Should().Contain("10.9.11");
    }

    [Fact]
    public async Task TestConnectionAsync_WithMissingCredentials_ReturnsUnhealthy()
    {
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        ServiceHealth health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Jellyfin__BaseUrl"] = "",
            ["Jellyfin__ApiKey"] = ""
        });

        health.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ExistingPlaceholder_ResolvesToStoredApiKey()
    {
        // Arrange: server responds with 200 OK when the stored key is used
        List<HttpRequestMessage> requestsSeen = new();
        MockHttpMessageHandler handler = new(req =>
        {
            requestsSeen.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Version":"10.9.0"}""")
            };
        });
        HttpClient client = new(handler);
        JellyfinWatchStateProvider provider = new(
            client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        // Act: form sends "__EXISTING__" for the password field
        ServiceHealth health = await provider.TestConnectionAsync(new Dictionary<string, string>
        {
            ["Jellyfin__BaseUrl"] = _options.BaseUrl,
            ["Jellyfin__ApiKey"] = "__EXISTING__"
        });

        // Assert: the stored key was used (request was made, server returned OK)
        health.IsHealthy.Should().BeTrue();
        requestsSeen.Should().ContainSingle();
        requestsSeen[0].Headers.GetValues("X-Emby-Authorization").First()
            .Should().Contain(_options.ApiKey);
    }

    [Fact]
    public void RevealSecret_ApiKey_ReturnsStoredValue()
    {
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.RevealSecret("Jellyfin__ApiKey").Should().Be(_options.ApiKey);
    }

    [Fact]
    public void RevealSecret_UnknownKey_ReturnsNull()
    {
        JellyfinWatchStateProvider provider = new(
            new HttpClient(), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        provider.RevealSecret("Jellyfin__BaseUrl").Should().BeNull();
        provider.RevealSecret("SomeOther__Key").Should().BeNull();
    }

    // Regression: a library movie was reported as an episode of itself at S00E00, so text searches
    // read "Interstellar Interstellar" and Trakt looked for a show.
    [Fact]
    public async Task GetCurrentMediaContextAsync_LibraryMovie_IsAMovie()
    {
        string sessionJson = """
                             [
                                 {
                                     "Id": "session1",
                                     "NowPlayingItem": {
                                         "Name": "Interstellar",
                                         "Type": "Movie",
                                         "PremiereDate": "2014-11-05T00:00:00Z",
                                         "ProviderIds": { "Imdb": "tt0816692", "Tmdb": "157336" }
                                     }
                                 }
                             ]
                             """;
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);

        MediaContext? result = await provider.GetCurrentMediaContextAsync();

        result.Should().BeOfType<MediaContext>();
        result!.Title.Should().Be("Interstellar");
        result.ReleaseDate.Should().Be(new DateTimeOffset(2014, 11, 5, 0, 0, 0, TimeSpan.Zero));
        result.ExternalIds.Should().ContainKey(ExternalIdType.Imdb).WhoseValue.Should().Be("tt0816692");
        IThoughtProvider.BuildTextQuery(result).Should().Be("Interstellar movie");
    }

    // ---- Live TV: the channel resolves to the program airing on it ----

    private async Task<MediaContext?> ContextForChannelAsync(string currentProgramJson)
    {
        string sessionJson = $$"""
                               [
                                   {
                                       "Id": "session1",
                                       "NowPlayingItem": {
                                           "Name": "NBC News NOW",
                                           "Type": "TvChannel",
                                           "ChannelNumber": "334",
                                           "ProviderIds": { "ExternalServiceId": "Emby" }
                                           {{(currentProgramJson.Length > 0 ? ", \"CurrentProgram\": " + currentProgramJson : "")}}
                                       }
                                   }
                               ]
                               """;
        MockHttpMessageHandler handler = new(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sessionJson) });
        HttpClient client = new(handler) { BaseAddress = new Uri(_options.BaseUrl) };
        JellyfinWatchStateProvider provider = new(client, new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance);
        return await provider.GetCurrentMediaContextAsync();
    }

    // Regression: a live channel was reported as an episode named after the channel, at S00E00.
    [Fact]
    public async Task LiveChannel_AiringNews_IsALiveBroadcastOfTheProgram()
    {
        MediaContext? result = await ContextForChannelAsync("""
            {
                "Name": "NBC News Daily With Morgan Radford and Vicky Nguyen",
                "ChannelName": "NBC News NOW",
                "Type": "Program",
                "ProductionYear": 1970,
                "ProviderIds": { "ProgramEtag": "xmltv-sha256-v1:FCC3" },
                "StartDate": "2026-11-04T18:00:00.0000000Z",
                "EndDate": "2026-11-04T19:00:00.0000000Z",
                "IsLive": true,
                "IsNews": true
            }
            """);

        LiveBroadcastContext live = result.Should().BeOfType<LiveBroadcastContext>().Subject;
        live.Title.Should().Be("NBC News Daily With Morgan Radford and Vicky Nguyen");
        live.ChannelName.Should().Be("NBC News NOW");
        live.StartsAt.Should().Be(new DateTimeOffset(2026, 11, 4, 18, 0, 0, TimeSpan.Zero));
        live.EndsAt.Should().Be(new DateTimeOffset(2026, 11, 4, 19, 0, 0, TimeSpan.Zero));
        live.ReleaseDate.Should().BeNull();
        live.ExternalIds.Should().BeNull();
    }

    [Fact]
    public async Task LiveChannel_AiringAnEpisode_IsThatEpisode()
    {
        // As Tunarr's guide describes a rerun: show title, episode title and numbers, no air date or ids.
        MediaContext? result = await ContextForChannelAsync("""
            {
                "Name": "Star Trek: The Next Generation",
                "EpisodeTitle": "The Naked Now",
                "ParentIndexNumber": 1,
                "IndexNumber": 2,
                "ProductionYear": 1987,
                "StartDate": "2026-11-04T17:17:00.0000000Z",
                "EndDate": "2026-11-04T18:03:00.0000000Z",
                "IsSeries": true,
                "IsRepeat": true,
                "ProviderIds": { "ProgramEtag": "xmltv-sha256-v1:1405" },
                "Type": "Program"
            }
            """);

        EpisodeContext episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.Title.Should().Be("Star Trek: The Next Generation");
        episode.EpisodeTitle.Should().Be("The Naked Now");
        episode.SeasonNumber.Should().Be(1);
        episode.EpisodeNumber.Should().Be(2);
        episode.ReleaseDate.Should().BeNull();
    }

    [Fact]
    public async Task LiveChannel_AiringAMovie_IsThatMovie()
    {
        MediaContext? result = await ContextForChannelAsync("""
            {
                "Name": "Galaxy Quest",
                "IsMovie": true,
                "PremiereDate": "1999-12-25T00:00:00.0000000Z",
                "ProviderIds": { "Imdb": "tt0177789" },
                "Type": "Program"
            }
            """);

        result.Should().BeOfType<MediaContext>();
        result!.Title.Should().Be("Galaxy Quest");
        result.ReleaseDate.Should().Be(new DateTimeOffset(1999, 12, 25, 0, 0, 0, TimeSpan.Zero));
        result.ExternalIds.Should().ContainKey(ExternalIdType.Imdb).WhoseValue.Should().Be("tt0177789");
    }

    [Fact]
    public async Task LiveChannel_WithoutGuideData_IsALiveBroadcastOfTheChannel()
    {
        MediaContext? result = await ContextForChannelAsync("");

        LiveBroadcastContext live = result.Should().BeOfType<LiveBroadcastContext>().Subject;
        live.Title.Should().Be("NBC News NOW");
        live.ChannelName.Should().Be("NBC News NOW");
        live.StartsAt.Should().BeNull();
    }

    // ---- Live TV: guide entries without numbers, and finding the ids guides leave out ----

    private static string ChannelSession(string channel, string currentProgramJson) => $$"""
        [ { "Id": "s1", "UserId": "u1",
            "NowPlayingItem": { "Name": "{{channel}}", "Type": "TvChannel", "CurrentProgram": {{currentProgramJson}} } } ]
        """;

    private const string TrekProgram = """
        { "Name": "Star Trek: The Next Generation", "EpisodeTitle": "The Naked Now",
          "ParentIndexNumber": 1, "IndexNumber": 2, "ProductionYear": 1987, "IsSeries": true, "Type": "Program" }
        """;

    private JellyfinWatchStateProvider LiveProvider(HttpMessageHandler handler, IMediaIdentifier? identifier = null) =>
        new(new HttpClient(handler), new OptionsSnapshotStub<JellyfinOptions>(_options), _cache,
            NullLogger<JellyfinWatchStateProvider>.Instance, identifier);

    [Fact]
    public async Task LiveChannel_SeriesWithAnEpisodeTitleButNoNumbers_IsThatEpisodeNotALiveBroadcast()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("Columbo", """
                { "Name": "Columbo", "EpisodeTitle": "Murder Under Glass", "ProductionYear": 1970, "IsLive": true }
                """))
            .Default("""{"Items":[]}""");

        MediaContext? result = await LiveProvider(handler).GetCurrentMediaContextAsync();

        EpisodeContext episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.EpisodeTitle.Should().Be("Murder Under Glass");
        episode.SeasonNumber.Should().Be(0);
        episode.LiveChannel.Should().Be("Columbo");
    }

    [Fact]
    public async Task LiveChannel_SportsWithAMatch_IsALiveBroadcastSearchedByTheMatch()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("beIN Sports Xtra", """
                { "Name": "UCL Match Replay", "EpisodeTitle": "Feyenoord vs. Inter Milan", "IsSports": true, "IsLive": true }
                """));

        MediaContext? result = await LiveProvider(handler).GetCurrentMediaContextAsync();

        LiveBroadcastContext live = result.Should().BeOfType<LiveBroadcastContext>().Subject;
        live.SearchNames[0].Should().Be("Feyenoord vs. Inter Milan");
        live.LiveChannel.Should().Be("beIN Sports Xtra");
    }

    [Fact]
    public async Task LiveChannel_EpisodeFromTheLibrary_GetsItsIdsAndAirDate()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("Trek Time", TrekProgram))
            .RespondTo("IncludeItemTypes=Series", """
                {"Items":[{"Id":"show1","Name":"Star Trek: The Next Generation","ProviderIds":{"Imdb":"tt0092455"}}]}
                """)
            .RespondTo("/Shows/show1/Episodes", """
                {"Items":[
                  {"Id":"e1","Name":"Encounter at Farpoint","ParentIndexNumber":1,"IndexNumber":1,"ProviderIds":{"Imdb":"tt0094030"}},
                  {"Id":"e2","Name":"The Naked Now","ParentIndexNumber":1,"IndexNumber":2,"PremiereDate":"1987-10-05T00:00:00Z",
                   "ProviderIds":{"Imdb":"tt0708810","Tvdb":"46510"}}]}
                """);
        IMediaIdentifier identifier = Substitute.For<IMediaIdentifier>();

        MediaContext? result = await LiveProvider(handler, identifier).GetCurrentMediaContextAsync();

        EpisodeContext episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0708810").And.Contain(ExternalIdType.Tvdb, "46510");
        episode.ReleaseDate.Should().Be(new DateTimeOffset(1987, 10, 5, 0, 0, 0, TimeSpan.Zero));
        episode.LiveChannel.Should().Be("Trek Time");
        handler.RecordedUris.Should().Contain(u => u.Query.Contains("season=1") && u.Query.Contains("userId=u1"));
        await identifier.DidNotReceiveWithAnyArgs().IdentifyAsync(default!, default, default);
    }

    [Fact]
    public async Task LiveChannel_EpisodeKnownOnlyByTitle_GetsItsNumbersFromTheLibrary()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("Classic TV", """
                { "Name": "Columbo", "EpisodeTitle": "Murder Under Glass" }
                """))
            .RespondTo("IncludeItemTypes=Series", """{"Items":[{"Id":"c","Name":"Columbo","ProviderIds":{"Imdb":"tt1466074"}}]}""")
            .RespondTo("/Shows/c/Episodes", """
                {"Items":[{"Id":"x","Name":"Murder Under Glass","ParentIndexNumber":7,"IndexNumber":4,
                  "PremiereDate":"1978-01-30T00:00:00Z","ProviderIds":{}}]}
                """);

        MediaContext? result = await LiveProvider(handler).GetCurrentMediaContextAsync();

        EpisodeContext episode = result.Should().BeOfType<EpisodeContext>().Subject;
        (episode.SeasonNumber, episode.EpisodeNumber).Should().Be(((short)7, (short)4));
        episode.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt1466074", "an episode without ids is known by its show's");
        handler.RecordedUris.Should().NotContain(u => u.Query.Contains("season="), "the season isn't known yet");
    }

    [Fact]
    public async Task LiveChannel_NotInTheLibrary_IsIdentifiedByNameAndTheGuidesYear()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("Pluto TV Sci-Fi", TrekProgram))
            .Default("""{"Items":[]}""");
        IMediaIdentifier identifier = Substitute.For<IMediaIdentifier>();
        identifier.IdentifyAsync(Arg.Any<MediaContext>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<MediaContext>() with
            {
                ExternalIds = new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0708810" }
            });

        MediaContext? result = await LiveProvider(handler, identifier).GetCurrentMediaContextAsync();

        result!.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0708810");
        result.LiveChannel.Should().Be("Pluto TV Sci-Fi");
        await identifier.Received(1).IdentifyAsync(Arg.Any<MediaContext>(), 1987, Arg.Any<CancellationToken>());
    }

    // Guides with no year for a program give 1970, which must not be taken for one.
    [Theory]
    [InlineData(1970, null)]
    [InlineData(null, null)]
    [InlineData(1, null)]
    [InlineData(1987, 1987)]
    public void YearHint_IgnoresTheEpochPlaceholder(int? guideYear, int? expected)
    {
        JellyfinWatchStateProvider.YearHint(guideYear).Should().Be(expected);
    }

    // A failed lookup must never turn playback into "nothing playing".
    [Fact]
    public async Task LiveChannel_WhenLibraryAndIdentifierFail_StillReportsWhatIsAiring()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("Trek Time", TrekProgram))
            .Default("""{"error":"boom"}""", HttpStatusCode.InternalServerError);
        IMediaIdentifier identifier = Substitute.For<IMediaIdentifier>();
        identifier.IdentifyAsync(Arg.Any<MediaContext>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<Task<MediaContext>>(_ => throw new HttpRequestException("OMDb down"));

        MediaContext? result = await LiveProvider(handler, identifier).GetCurrentMediaContextAsync();

        EpisodeContext episode = result.Should().BeOfType<EpisodeContext>().Subject;
        episode.Title.Should().Be("Star Trek: The Next Generation");
        (episode.SeasonNumber, episode.EpisodeNumber).Should().Be(((short)1, (short)2));
    }


    [Fact]
    public async Task LiveChannel_MusicProgramming_IsALiveBroadcastWithNothingToSearch()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("Vevo 2K", """
                { "Name": "Top Hits", "Genres": ["Pop", "Series", "Music"], "IsLive": true,
                  "StartDate": "2026-11-04T18:00:00Z", "EndDate": "2026-11-04T19:00:00Z" }
                """));

        MediaContext? result = await LiveProvider(handler).GetCurrentMediaContextAsync();

        LiveBroadcastContext live = result.Should().BeOfType<LiveBroadcastContext>().Subject;
        live.IsMusic.Should().BeTrue();
        live.SearchNames.Should().BeEmpty();
    }

    // Movie channels' guides call a film a "Series" with no year; a long untitled program is tried as one.
    [Fact]
    public async Task LiveChannel_LongProgramWithoutAnEpisodeTitle_FoundAsAFilm_IsThatFilm()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("Cent Action", """
                { "Name": "Conan the Barbarian", "Genres": ["Series", "Entertainment"], "ProductionYear": 1970,
                  "StartDate": "2026-11-04T18:00:00Z", "EndDate": "2026-11-04T20:00:00Z" }
                """))
            .Default("""{"Items":[]}""");
        IMediaIdentifier identifier = Substitute.For<IMediaIdentifier>();
        identifier.IdentifyAsync(Arg.Is<MediaContext>(m => m.GetType() == typeof(MediaContext)), null,
                Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<MediaContext>() with
            {
                ExternalIds = new Dictionary<string, string> { [ExternalIdType.Imdb] = "tt0082198" }
            });

        MediaContext? result = await LiveProvider(handler, identifier).GetCurrentMediaContextAsync();

        result.Should().BeOfType<MediaContext>();
        result!.Title.Should().Be("Conan the Barbarian");
        result.ExternalIds.Should().Contain(ExternalIdType.Imdb, "tt0082198");
        result.LiveChannel.Should().Be("Cent Action");
    }

    [Fact]
    public async Task LiveChannel_LongProgramNotFoundAsAFilm_StaysALiveBroadcast()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("QVC", """
                { "Name": "Gourmet Holiday", "Genres": ["Series", "Home & Lifestyle"],
                  "StartDate": "2026-11-04T18:00:00Z", "EndDate": "2026-11-04T20:00:00Z" }
                """))
            .Default("""{"Items":[]}""");
        IMediaIdentifier identifier = Substitute.For<IMediaIdentifier>();
        identifier.IdentifyAsync(Arg.Any<MediaContext>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<MediaContext>());

        MediaContext? result = await LiveProvider(handler, identifier).GetCurrentMediaContextAsync();

        result.Should().BeOfType<LiveBroadcastContext>().Which.Title.Should().Be("Gourmet Holiday");
    }

    [Fact]
    public async Task LiveChannel_ShortProgramOrNews_IsNotLookedUpAsAFilm()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("The Price Is Right", """
                { "Name": "The Price Is Right", "StartDate": "2026-11-04T18:00:00Z", "EndDate": "2026-11-04T19:03:00Z" }
                """));
        IMediaIdentifier identifier = Substitute.For<IMediaIdentifier>();

        await LiveProvider(handler, identifier).GetCurrentMediaContextAsync();

        await identifier.DidNotReceiveWithAnyArgs().IdentifyAsync(default!, default, default);
    }


    // A failing library lookup isn't retried on every session refresh: those calls share the
    // Jellyfin client's retries and circuit breaker with /Sessions.
    [Fact]
    public async Task LiveChannel_FailedLibraryLookup_IsNotRetriedOnTheNextRefresh()
    {
        RoutableMockHttpHandler handler = new RoutableMockHttpHandler()
            .RespondTo("/Sessions", ChannelSession("Trek Time", TrekProgram))
            .Default("""{"error":"boom"}""", HttpStatusCode.InternalServerError);
        JellyfinWatchStateProvider provider = LiveProvider(handler);

        await provider.GetCurrentMediaContextAsync();
        _cache.Remove("jellyfin:session:any");
        MediaContext? again = await provider.GetCurrentMediaContextAsync();

        again.Should().BeOfType<EpisodeContext>();
        handler.RecordedUris.Count(u => u.AbsolutePath == "/Items").Should().Be(1);
    }

}

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage? _fixedResponse;
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _requestFactory;
    private readonly Func<HttpResponseMessage>? _responseFactory;

    public MockHttpMessageHandler(HttpResponseMessage fixedResponse)
    {
        _fixedResponse = fixedResponse;
    }

    public MockHttpMessageHandler(Func<HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> requestFactory)
    {
        _requestFactory = requestFactory;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = _fixedResponse ?? _requestFactory?.Invoke(request) ?? _responseFactory!();
        return Task.FromResult(response);
    }

}
