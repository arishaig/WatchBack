using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using WatchBack.Core.Models;
using WatchBack.Infrastructure.Services;

using Xunit;

namespace WatchBack.Infrastructure.Tests;

public sealed class SubredditMappingServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"wb-test-{Guid.NewGuid():N}");

    public SubredditMappingServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private SubredditMappingService Build(string? builtInJson = null)
    {
        string builtInPath = Path.Combine(_tempDir, "builtin.json");
        if (builtInJson is not null)
        {
            File.WriteAllText(builtInPath, builtInJson);
        }

        SubredditMappingPaths paths = new(builtInPath, _tempDir);
        return new SubredditMappingService(paths, NullLogger<SubredditMappingService>.Instance);
    }

    // ---- GetSubreddits ----

    [Fact]
    public void GetSubreddits_ExactTitleMatch_ReturnsMappedSubreddits()
    {
        string json = """{"mappings":[{"title":"Saturday Night Live","subreddits":["snl","LiveFromNewYork"]}]}""";
        SubredditMappingService svc = Build(json);

        MediaContext ctx = new("Saturday Night Live", null);

        svc.GetSubreddits(ctx).Should().BeEquivalentTo(["snl", "LiveFromNewYork"]);
    }

    [Fact]
    public void GetSubreddits_CaseInsensitiveTitle_Matches()
    {
        string json = """{"mappings":[{"title":"saturday night live","subreddits":["snl"]}]}""";
        SubredditMappingService svc = Build(json);

        MediaContext ctx = new("Saturday Night Live", null);

        svc.GetSubreddits(ctx).Should().BeEquivalentTo(["snl"]);
    }

    [Fact]
    public void GetSubreddits_ExternalIdMatch_TakesPriorityOverTitle()
    {
        // Entry has wrong title but correct IMDB id
        string json = """{"mappings":[{"title":"Wrong Title","externalIds":{"imdb":"tt0072562"},"subreddits":["snl"]}]}""";
        SubredditMappingService svc = Build(json);

        MediaContext ctx = new("Saturday Night Live", null,
            new Dictionary<string, string> { { "imdb", "tt0072562" } });

        svc.GetSubreddits(ctx).Should().BeEquivalentTo(["snl"]);
    }

    [Fact]
    public void GetSubreddits_NoMatch_ReturnsEmptyList()
    {
        string json = """{"mappings":[{"title":"Breaking Bad","subreddits":["breakingbad"]}]}""";
        SubredditMappingService svc = Build(json);

        MediaContext ctx = new("Better Call Saul", null);

        svc.GetSubreddits(ctx).Should().BeEmpty();
    }

    [Fact]
    public async Task GetSubreddits_MergesSubredditsAcrossSources()
    {
        // Built-in has one subreddit; we'll also add a local entry with another
        string builtInJson = """{"mappings":[{"title":"Saturday Night Live","subreddits":["snl"]}]}""";
        SubredditMappingService svc = Build(builtInJson);

        // Add local entry for the same show with an additional subreddit
        await svc.AddLocalEntryAsync(new SubredditMappingEntry(
            "Saturday Night Live", null, ["LiveFromNewYork"]));

        MediaContext ctx = new("Saturday Night Live", null);

        svc.GetSubreddits(ctx).Should().BeEquivalentTo(["snl", "LiveFromNewYork"]);
    }

    // ---- Import / Delete source ----

    [Fact]
    public async Task ImportAsync_ValidJson_CreatesNewSource()
    {
        SubredditMappingService svc = Build();
        string json = """{"mappings":[{"title":"The Bear","subreddits":["TheBearFX"]}]}""";

        SubredditMappingSource source = await svc.ImportAsync("my-import", json);

        source.Name.Should().Be("my-import");
        source.IsBuiltIn.Should().BeFalse();
        source.Entries.Should().ContainSingle(e => e.Title == "The Bear");

        // File persisted
        File.Exists(Path.Combine(_tempDir, $"{source.Id}.json")).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteSourceAsync_RemovesSourceAndFile()
    {
        SubredditMappingService svc = Build();
        string json = """{"mappings":[{"title":"Severance","subreddits":["SeveranceAppleTVPlus"]}]}""";
        SubredditMappingSource source = await svc.ImportAsync("to-delete", json);

        await svc.DeleteSourceAsync(source.Id);

        svc.GetSources().Should().NotContain(s => s.Id == source.Id);
        File.Exists(Path.Combine(_tempDir, $"{source.Id}.json")).Should().BeFalse();
    }

    [Fact]
    public void DeleteSourceAsync_BuiltIn_Throws()
    {
        string json = """{"mappings":[{"title":"SNL","subreddits":["snl"]}]}""";
        SubredditMappingService svc = Build(json);

        Func<Task> act = () => svc.DeleteSourceAsync("builtin");

        act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- AddLocalEntry / DeleteLocalEntry ----

    [Fact]
    public async Task AddLocalEntry_AppearsInGetSubreddits()
    {
        SubredditMappingService svc = Build();
        await svc.AddLocalEntryAsync(new SubredditMappingEntry("Succession", null, ["SuccessionTV"]));

        svc.GetSubreddits(new MediaContext("Succession", null))
            .Should().BeEquivalentTo(["SuccessionTV"]);
    }

    [Fact]
    public async Task AddLocalEntry_DuplicateTitle_MergesSubreddits()
    {
        SubredditMappingService svc = Build();
        await svc.AddLocalEntryAsync(new SubredditMappingEntry("SNL", null, ["snl"]));
        await svc.AddLocalEntryAsync(new SubredditMappingEntry("SNL", null, ["LiveFromNewYork"]));

        svc.GetSubreddits(new MediaContext("SNL", null))
            .Should().BeEquivalentTo(["snl", "LiveFromNewYork"]);
    }

    [Fact]
    public async Task DeleteLocalEntryAsync_RemovesEntry()
    {
        SubredditMappingService svc = Build();
        await svc.AddLocalEntryAsync(new SubredditMappingEntry("Succession", null, ["SuccessionTV"]));

        await svc.DeleteLocalEntryAsync("Succession");

        svc.GetSubreddits(new MediaContext("Succession", null)).Should().BeEmpty();
    }

    // ---- PromoteEntry ----

    [Fact]
    public async Task PromoteEntryAsync_CopiesEntryToLocal()
    {
        string json = """{"mappings":[{"title":"Industry","subreddits":["IndustryHBO"]}]}""";
        SubredditMappingService svc = Build();
        SubredditMappingSource imported = await svc.ImportAsync("community", json);

        await svc.PromoteEntryAsync(imported.Id, 0);

        SubredditMappingSource? local = svc.GetSources()
            .FirstOrDefault(s => s.Id == "local");

        local.Should().NotBeNull();
        local!.Entries.Should().Contain(e => e.Title == "Industry");

        // Original source entry is still present
        svc.GetSources().First(s => s.Id == imported.Id).Entries
            .Should().Contain(e => e.Title == "Industry");
    }

    // ---- ExportSource ----

    [Fact]
    public async Task ExportSource_ProducesValidJson()
    {
        SubredditMappingService svc = Build();
        await svc.AddLocalEntryAsync(new SubredditMappingEntry("Silo", null, ["SiloSeries"]));

        string json = svc.ExportSource("local");

        json.Should().Contain("\"Silo\"");
        json.Should().Contain("\"SiloSeries\"");
        json.Should().Contain("\"mappings\"");
    }
}
