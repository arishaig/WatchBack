using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

// ReSharper disable once CheckNamespace
namespace WatchBack.Infrastructure.Omdb;

public partial class OmdbMediaSearchProvider
{
    // Matches S01E05 or S1E5 (case-insensitive)
    [GeneratedRegex(@"[Ss](\d{1,2})[Ee](\d{1,2})", RegexOptions.Compiled)]
    private static partial Regex SxxExxPattern();

    // Matches "season N episode N" (case-insensitive)
    [GeneratedRegex(@"season\s+(\d+)\s+episode\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonEpisodePattern();

    public MediaSearchProviderMetadata Metadata =>
        new(Name: "OMDb", Description: "Open Movie Database", BrandData: OmdbBrandData);

    public async Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var apiKey = options.CurrentValue.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        var sxxMatch = SxxExxPattern().Match(query);
        var longMatch = SeasonEpisodePattern().Match(query);

        if (sxxMatch.Success || longMatch.Success)
        {
            var (season, episode) = sxxMatch.Success
                ? (int.Parse(sxxMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                   int.Parse(sxxMatch.Groups[2].Value, CultureInfo.InvariantCulture))
                : (int.Parse(longMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                   int.Parse(longMatch.Groups[2].Value, CultureInfo.InvariantCulture));

            var titlePart = (sxxMatch.Success
                    ? query[..sxxMatch.Index]
                    : query[..longMatch.Index])
                .Trim(' ', '-', ':');

            return await SearchWithEpisodeAsync(titlePart, season, episode, apiKey, ct);
        }

        var cacheKey = $"omdb:search:{query.ToLowerInvariant()}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<MediaSearchResult>? cached) && cached != null)
            return cached;

        var url = $"https://www.omdbapi.com/?s={Uri.EscapeDataString(query)}&apikey={apiKey}";
        var response = await FetchJsonAsync<OmdbSearchResponse>(url, ct);
        if (response?.Search == null)
            return [];

        var results = response.Search
            .Select(r => new MediaSearchResult(
                ImdbId: r.ImdbId,
                Title: r.Title,
                Year: NullIfNa(r.Year),
                Type: r.Type ?? "movie",
                PosterUrl: NullIfNa(r.Poster)))
            .ToList();

        cache.Set<IReadOnlyList<MediaSearchResult>>(cacheKey, results, SearchCacheDuration);
        return results;
    }

    public async Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string externalId, CancellationToken ct = default)
    {
        var apiKey = options.CurrentValue.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        var cacheKey = $"omdb:seasons:{externalId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<SeasonInfo>? cached) && cached != null)
            return cached;

        // OMDb doesn't have a "list seasons" endpoint — fetch season 1 to get totalSeasons
        var url = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(externalId)}&Season=1&apikey={apiKey}";
        var response = await FetchJsonAsync<OmdbSeasonResponse>(url, ct);
        if (response?.TotalSeasons == null || !int.TryParse(response.TotalSeasons, out var total))
            return [];

        var seasons = Enumerable.Range(1, total)
            .Select(n => new SeasonInfo(
                SeasonNumber: n,
                EpisodeCount: n == 1 ? response.Episodes?.Length ?? 0 : 0))
            .ToList();

        cache.Set<IReadOnlyList<SeasonInfo>>(cacheKey, seasons, EpisodeCacheDuration);
        return seasons;
    }

    public async Task<IReadOnlyList<EpisodeInfo>> GetEpisodesAsync(string externalId, int seasonNumber, CancellationToken ct = default)
    {
        var apiKey = options.CurrentValue.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        var cacheKey = $"omdb:episodes:{externalId}:s{seasonNumber}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<EpisodeInfo>? cached) && cached != null)
            return cached;

        var url = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(externalId)}&Season={seasonNumber}&apikey={apiKey}";
        var response = await FetchJsonAsync<OmdbSeasonResponse>(url, ct);
        if (response?.Episodes == null)
            return [];

        var episodes = response.Episodes
            .Select(e => new EpisodeInfo(
                ImdbId: NullIfNa(e.ImdbId),
                Title: e.Title,
                EpisodeNumber: int.TryParse(e.Episode, out var epNum) ? epNum : 0,
                SeasonNumber: seasonNumber,
                AirDate: ParseOmdbDate(e.Released)))
            .ToList();

        cache.Set<IReadOnlyList<EpisodeInfo>>(cacheKey, episodes, EpisodeCacheDuration);
        return episodes;
    }

    // Single-search path when the user includes episode info in the query
    private async Task<IReadOnlyList<MediaSearchResult>> SearchWithEpisodeAsync(
        string titleQuery, int seasonNumber, int episodeNumber, string apiKey, CancellationToken ct)
    {
        var searchUrl = $"https://www.omdbapi.com/?s={Uri.EscapeDataString(titleQuery)}&type=series&apikey={apiKey}";
        var searchResponse = await FetchJsonAsync<OmdbSearchResponse>(searchUrl, ct);
        var show = searchResponse?.Search?.FirstOrDefault();
        if (show == null)
            return [];

        var episodeUrl = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(show.ImdbId)}&Season={seasonNumber}&Episode={episodeNumber}&apikey={apiKey}";
        var episodeResponse = await FetchJsonAsync<OmdbEpisodeDetailResponse>(episodeUrl, ct);
        if (episodeResponse?.ImdbId == null || episodeResponse.Response == "False")
            return [new MediaSearchResult(show.ImdbId, show.Title, NullIfNa(show.Year), "series", NullIfNa(show.Poster))];

        return [new MediaSearchResult(
            ImdbId: show.ImdbId,
            Title: $"{show.Title} — {episodeResponse.Title} (S{seasonNumber:D2}E{episodeNumber:D2})",
            Year: NullIfNa(show.Year),
            Type: "episode",
            PosterUrl: NullIfNa(episodeResponse.Poster) ?? NullIfNa(show.Poster),
            ReleaseDate: ParseOmdbDate(episodeResponse.Released))];
    }
}

// --- Search DTOs ---

internal sealed record OmdbSearchResponse(
    [property: JsonPropertyName("Search")] OmdbSearchItem[]? Search,
    [property: JsonPropertyName("Response")] string? Response,
    [property: JsonPropertyName("Error")] string? Error);

internal sealed record OmdbSearchItem(
    [property: JsonPropertyName("imdbID")] string ImdbId,
    [property: JsonPropertyName("Title")] string Title,
    [property: JsonPropertyName("Year")] string? Year,
    [property: JsonPropertyName("Type")] string? Type,
    [property: JsonPropertyName("Poster")] string? Poster);

internal sealed record OmdbSeasonResponse(
    [property: JsonPropertyName("totalSeasons")] string? TotalSeasons,
    [property: JsonPropertyName("Episodes")] OmdbEpisodeItem[]? Episodes,
    [property: JsonPropertyName("Response")] string? Response);

internal sealed record OmdbEpisodeItem(
    [property: JsonPropertyName("imdbID")] string? ImdbId,
    [property: JsonPropertyName("Title")] string Title,
    [property: JsonPropertyName("Episode")] string? Episode,
    [property: JsonPropertyName("Released")] string? Released);

internal sealed record OmdbEpisodeDetailResponse(
    [property: JsonPropertyName("imdbID")] string? ImdbId,
    [property: JsonPropertyName("Title")] string? Title,
    [property: JsonPropertyName("Poster")] string? Poster,
    [property: JsonPropertyName("Released")] string? Released,
    [property: JsonPropertyName("Response")] string? Response);
