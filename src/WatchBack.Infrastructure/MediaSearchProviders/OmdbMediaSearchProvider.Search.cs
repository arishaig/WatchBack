using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;

using WatchBack.Core.Models;

// ReSharper disable once CheckNamespace
namespace WatchBack.Infrastructure.Omdb;

public sealed partial class OmdbMediaSearchProvider
{
    public async Task<IReadOnlyList<MediaSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        string apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        Match sxxMatch = SxxExxPattern().Match(query);
        Match longMatch = SeasonEpisodePattern().Match(query);

        if (sxxMatch.Success || longMatch.Success)
        {
            (int season, int episode) = sxxMatch.Success
                ? (int.Parse(sxxMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(sxxMatch.Groups[2].Value, CultureInfo.InvariantCulture))
                : (int.Parse(longMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(longMatch.Groups[2].Value, CultureInfo.InvariantCulture));

            string titlePart = (sxxMatch.Success
                    ? query[..sxxMatch.Index]
                    : query[..longMatch.Index])
                .Trim(' ', '-', ':');

            return await SearchWithEpisodeAsync(titlePart, season, episode, apiKey, ct);
        }

        string cacheKey = $"omdb:search:{query.ToLowerInvariant()}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<MediaSearchResult>? cached) && cached != null)
        {
            return cached;
        }

        string url = $"https://www.omdbapi.com/?s={Uri.EscapeDataString(query)}&apikey={Uri.EscapeDataString(apiKey)}";
        OmdbSearchResponse? response = await FetchJsonAsync<OmdbSearchResponse>(url, ct);
        if (response?.Search == null)
        {
            return [];
        }

        List<MediaSearchResult> results = response.Search
            .Select(r => new MediaSearchResult(
                r.ImdbId,
                r.Title,
                NullIfNa(r.Year),
                r.Type ?? "movie",
                NullIfNa(r.Poster)))
            .ToList();

        cache.Set<IReadOnlyList<MediaSearchResult>>(cacheKey, results, s_searchCacheDuration);
        return results;
    }

    public async Task<IReadOnlyList<SeasonInfo>> GetSeasonsAsync(string externalId, CancellationToken ct = default)
    {
        string apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        string cacheKey = $"omdb:seasons:{externalId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<SeasonInfo>? cached) && cached != null)
        {
            return cached;
        }

        // OMDb doesn't have a "list seasons" endpoint — fetch season 1 to get totalSeasons
        string url = $"https://www.omdbapi.com/?i={Uri.EscapeDataString(externalId)}&Season=1&apikey={Uri.EscapeDataString(apiKey)}";
        OmdbSeasonResponse? response = await FetchJsonAsync<OmdbSeasonResponse>(url, ct);
        if (response?.TotalSeasons == null || !int.TryParse(response.TotalSeasons, out int total))
        {
            return [];
        }

        // OMDb only returns episode details for the requested season, so we only
        // have an accurate count for season 1. Rather than report 0 for the rest,
        // leave EpisodeCount as 0 for all — the UI fetches episodes on demand.
        List<SeasonInfo> seasons = Enumerable.Range(1, total)
            .Select(n => new SeasonInfo(n, 0))
            .ToList();

        cache.Set<IReadOnlyList<SeasonInfo>>(cacheKey, seasons, s_episodeCacheDuration);
        return seasons;
    }

    public async Task<IReadOnlyList<EpisodeInfo>> GetEpisodesAsync(string externalId, int seasonNumber,
        CancellationToken ct = default)
    {
        string apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        string cacheKey = $"omdb:episodes:{externalId}:s{seasonNumber}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<EpisodeInfo>? cached) && cached != null)
        {
            return cached;
        }

        string url =
            $"https://www.omdbapi.com/?i={Uri.EscapeDataString(externalId)}&Season={seasonNumber}&apikey={Uri.EscapeDataString(apiKey)}";
        OmdbSeasonResponse? response = await FetchJsonAsync<OmdbSeasonResponse>(url, ct);
        if (response?.Episodes == null)
        {
            return [];
        }

        List<EpisodeInfo> episodes = response.Episodes
            .Select(e => new EpisodeInfo(
                NullIfNa(e.ImdbId),
                e.Title,
                int.TryParse(e.Episode, out int epNum) ? epNum : 0,
                seasonNumber,
                ParseOmdbDate(e.Released)))
            .ToList();

        cache.Set<IReadOnlyList<EpisodeInfo>>(cacheKey, episodes, s_episodeCacheDuration);
        return episodes;
    }

    // Matches S01E05 or S1E5 (case-insensitive)
    [GeneratedRegex(@"[Ss](\d{1,2})[Ee](\d{1,2})", RegexOptions.Compiled)]
    private static partial Regex SxxExxPattern();

    // Matches "season N episode N" (case-insensitive)
    [GeneratedRegex(@"season\s+(\d+)\s+episode\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SeasonEpisodePattern();

    // Single-search path when the user includes episode info in the query
    private async Task<IReadOnlyList<MediaSearchResult>> SearchWithEpisodeAsync(
        string titleQuery, int seasonNumber, int episodeNumber, string apiKey, CancellationToken ct)
    {
        string searchUrl = $"https://www.omdbapi.com/?s={Uri.EscapeDataString(titleQuery)}&type=series&apikey={Uri.EscapeDataString(apiKey)}";
        OmdbSearchResponse? searchResponse = await FetchJsonAsync<OmdbSearchResponse>(searchUrl, ct);
        OmdbSearchItem? show = searchResponse?.Search?.FirstOrDefault();
        if (show == null)
        {
            return [];
        }

        string episodeUrl =
            $"https://www.omdbapi.com/?i={Uri.EscapeDataString(show.ImdbId)}&Season={seasonNumber}&Episode={episodeNumber}&apikey={Uri.EscapeDataString(apiKey)}";
        OmdbEpisodeDetailResponse? episodeResponse = await FetchJsonAsync<OmdbEpisodeDetailResponse>(episodeUrl, ct);
        if (episodeResponse?.ImdbId == null || episodeResponse.Response == "False")
        {
            return
            [
                new MediaSearchResult(show.ImdbId, show.Title, NullIfNa(show.Year), "series", NullIfNa(show.Poster))
            ];
        }

        return
        [
            new MediaSearchResult(
                show.ImdbId,
                $"{show.Title} — {episodeResponse.Title} (S{seasonNumber:D2}E{episodeNumber:D2})",
                NullIfNa(show.Year),
                "episode",
                NullIfNa(episodeResponse.Poster) ?? NullIfNa(show.Poster),
                ParseOmdbDate(episodeResponse.Released))
        ];
    }
}

// --- Search DTOs ---

internal sealed record OmdbSearchResponse(
    [property: JsonPropertyName("Search")] OmdbSearchItem[]? Search,
    [property: JsonPropertyName("Response")]
    string? Response,
    [property: JsonPropertyName("Error")] string? Error);

internal sealed record OmdbSearchItem(
    [property: JsonPropertyName("imdbID")] string ImdbId,
    [property: JsonPropertyName("Title")] string Title,
    [property: JsonPropertyName("Year")] string? Year,
    [property: JsonPropertyName("Type")] string? Type,
    [property: JsonPropertyName("Poster")] string? Poster);

internal sealed record OmdbSeasonResponse(
    [property: JsonPropertyName("totalSeasons")]
    string? TotalSeasons,
    [property: JsonPropertyName("Episodes")]
    OmdbEpisodeItem[]? Episodes,
    [property: JsonPropertyName("Response")]
    string? Response);

internal sealed record OmdbEpisodeItem(
    [property: JsonPropertyName("imdbID")] string? ImdbId,
    [property: JsonPropertyName("Title")] string Title,
    [property: JsonPropertyName("Episode")]
    string? Episode,
    [property: JsonPropertyName("Released")]
    string? Released);

internal sealed record OmdbEpisodeDetailResponse(
    [property: JsonPropertyName("imdbID")] string? ImdbId,
    [property: JsonPropertyName("Title")] string? Title,
    [property: JsonPropertyName("Poster")] string? Poster,
    [property: JsonPropertyName("Released")]
    string? Released,
    [property: JsonPropertyName("Response")]
    string? Response);
