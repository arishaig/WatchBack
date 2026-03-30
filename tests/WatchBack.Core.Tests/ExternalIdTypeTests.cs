using FluentAssertions;

using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Core.Tests;

public class ExternalIdTypeTests
{
    [Fact]
    public void GetShowLookupPriority_AllIds_ReturnImdbFirst()
    {
        Dictionary<string, string> ids = new()
        {
            [ExternalIdType.Imdb] = "tt0903747",
            [ExternalIdType.Tvdb] = "81189",
            [ExternalIdType.Tmdb] = "1396"
        };

        (string Type, string Value)[] result = ExternalIdType.GetShowLookupPriority(ids).ToArray();

        result[0].Type.Should().Be(ExternalIdType.Imdb);
        result[1].Type.Should().Be(ExternalIdType.Tvdb);
        result[2].Type.Should().Be(ExternalIdType.Tmdb);
    }

    [Fact]
    public void GetShowLookupPriority_OnlyTmdb_ReturnsTmdb()
    {
        Dictionary<string, string> ids = new()
        {
            [ExternalIdType.Tmdb] = "1396"
        };

        (string Type, string Value)[] result = ExternalIdType.GetShowLookupPriority(ids).ToArray();

        result.Should().ContainSingle(r => r.Type == ExternalIdType.Tmdb);
    }

    [Fact]
    public void GetShowLookupPriority_NullIds_ReturnsEmpty()
    {
        ExternalIdType.GetShowLookupPriority(null).Should().BeEmpty();
    }

    [Fact]
    public void GetMovieLookupPriority_AllIds_ExcludesTvdb()
    {
        Dictionary<string, string> ids = new()
        {
            [ExternalIdType.Imdb] = "tt0816692",
            [ExternalIdType.Tvdb] = "999999",
            [ExternalIdType.Tmdb] = "157336"
        };

        (string Type, string Value)[] result = ExternalIdType.GetMovieLookupPriority(ids).ToArray();

        result.Should().HaveCount(2);
        result.Should().NotContain(r => r.Type == ExternalIdType.Tvdb);
        result[0].Type.Should().Be(ExternalIdType.Imdb);
        result[1].Type.Should().Be(ExternalIdType.Tmdb);
    }

    [Fact]
    public void GetMovieLookupPriority_NullIds_ReturnsEmpty()
    {
        ExternalIdType.GetMovieLookupPriority(null).Should().BeEmpty();
    }
}
