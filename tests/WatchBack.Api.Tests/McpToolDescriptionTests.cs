using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using WatchBack.Api.Mcp;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

using Xunit;

namespace WatchBack.Api.Tests;

// An assistant copies the examples in tool descriptions. Regression: they named Jellyfin__Url (the
// setting is Jellyfin__BaseUrl) and external IDs "imdbId"/"tmdbId" (the keys are imdb/tmdb/tvdb), so
// following them saved a setting nothing reads and IDs nothing looks up.
public sealed class McpToolDescriptionTests
{
    private static IEnumerable<string> Descriptions() =>
        typeof(WatchBackMcpTools).GetMethods()
            .SelectMany(m => m.GetParameters().Select(p => p.GetCustomAttribute<DescriptionAttribute>())
                .Append(m.GetCustomAttribute<DescriptionAttribute>()))
            .Where(d => d is not null)
            .Select(d => d!.Description);

    [Fact]
    public void EverySettingNamedInADescription_Exists()
    {
        using WebApplicationFactory<Program> factory = new();
        using IServiceScope scope = factory.Services.CreateScope();
        HashSet<string> known = scope.ServiceProvider.GetServices<IWatchStateProvider>().Cast<IDataProvider>()
            .Concat(scope.ServiceProvider.GetServices<IThoughtProvider>())
            .Concat(scope.ServiceProvider.GetServices<IRatingsProvider>())
            .SelectMany(p => p.GetConfigSchema(_ => "", (_, _) => false))
            .Select(f => f.Key)
            .Concat(IntegrationCatalog.PreferenceKeys)
            .ToHashSet(StringComparer.Ordinal);

        List<string> named = Descriptions()
            .SelectMany(d => Regex.Matches(d, @"\b[A-Z][A-Za-z]+__[A-Za-z]+\b").Select(m => m.Value))
            .Where(k => k != "Section__Key") // the format's own name, not an example
            .Distinct()
            .ToList();

        named.Should().NotBeEmpty();
        named.Should().OnlyContain(k => known.Contains(k), "an assistant will copy these setting names");
    }

    [Fact]
    public void ExternalIdExamples_UseTheKeysWatchBackReads()
    {
        string[] keys = [ExternalIdType.Imdb, ExternalIdType.Tmdb, ExternalIdType.Tvdb];

        List<string> exampleKeys = Descriptions()
            .Where(d => d.Contains("tt0903747", StringComparison.Ordinal))
            .SelectMany(d => Regex.Matches(d, "\"([a-zA-Z]+)\":").Select(m => m.Groups[1].Value))
            .ToList();

        exampleKeys.Should().NotBeEmpty().And.OnlyContain(k => keys.Contains(k));
    }
}
