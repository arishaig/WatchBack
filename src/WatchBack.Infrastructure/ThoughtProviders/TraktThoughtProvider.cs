using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Caching;
using WatchBack.Resources;

namespace WatchBack.Infrastructure.ThoughtProviders;

[JsonSerializable(typeof(TraktSearchItemDto[]))]
[JsonSerializable(typeof(TraktCommentDto[]))]
internal sealed partial class TraktThoughtJsonContext : JsonSerializerContext;

public sealed class TraktThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<TraktOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    IDurableLookupCache ids,
    ILogger<TraktThoughtProvider> logger)
    : IThoughtProvider
{
    private static readonly ThoughtResult s_empty =
        new("Trakt", null, null, null, [], null);

    // Returned (and briefly cached) when the lookup itself failed, as opposed to finding nothing,
    // so the thought cache never stores an outage as a confirmed miss.
    private static readonly ThoughtResult s_failed = s_empty with { IsTransientFailure = true };

    // Failed lookups (429s, unresolved slugs, transient errors) are cached briefly so
    // the 5-second poll loop doesn't redo the work, and re-tick, every cycle.
    private static readonly TimeSpan s_failureCacheTtl = TimeSpan.FromSeconds(60);

    public const string IdStoreKind = "trakt-id";

    // A show's or movie's Trakt ID never changes. "Not on Trakt" may stop being true, so it is re-checked.
    private static readonly TimeSpan s_idLifetime = TimeSpan.FromDays(3650);
    private static readonly TimeSpan s_titleLifetime = TimeSpan.FromDays(180);
    private static readonly TimeSpan s_noneLifetime = TimeSpan.FromDays(7);

    private const int CommentLimit = 50;

    // Below this many episode comments, the season's (then the show's) are added.
    private const int FewComments = 5;

    // Replies cost a call per comment, so only the few most-liked comments with replies get them.
    private const int MaxReplyFetches = 3;
    private const int MinLikesForReplies = 2;

    private readonly TraktOptions _options = options.Value;

    public DataProviderMetadata Metadata => new(
        "Trakt",
        UiStrings.TraktThoughtProvider_Metadata_Trakt_comments_provider,
        BrandData: new BrandData(
            "#9F42C6",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Trakt</title><path d=\"m15.082 15.107-.73-.73 9.578-9.583a4.499 4.499 0 0 0-.115-.575L13.662 14.382l1.08 1.08-.73.73-1.81-1.81L23.422 3.144c-.075-.15-.155-.3-.25-.44L11.508 14.377l2.154 2.155-.73.73-7.193-7.199.73-.73 4.309 4.31L22.546 1.86A5.618 5.618 0 0 0 18.362 0H5.635A5.637 5.637 0 0 0 0 5.634V18.37A5.632 5.632 0 0 0 5.635 24h12.732C21.477 24 24 21.48 24 18.37V6.19l-8.913 8.918zm-4.314-2.155L6.814 8.988l.73-.73 3.954 3.96zm1.075-1.084-3.954-3.96.73-.73 3.959 3.96zm9.853 5.688a4.141 4.141 0 0 1-4.14 4.14H6.438a4.144 4.144 0 0 1-4.139-4.14V6.438A4.141 4.141 0 0 1 6.44 2.3h10.387v1.04H6.438c-1.71 0-3.099 1.39-3.099 3.1V17.55c0 1.71 1.39 3.105 3.1 3.105h11.117c1.71 0 3.1-1.395 3.1-3.105v-1.754h1.04v1.754z\"/></svg>"
        )
    );

    public int ExpectedWeight => 1;

    public string ConfigSection => "Trakt";

    public bool IsConfigured => !string.IsNullOrEmpty(_options.ClientId);

    public string GetCacheKey(MediaContext mediaContext)
    {
        string baseKey = BuildSlugCacheKey("trakt:thoughts", mediaContext);
        return mediaContext is EpisodeContext episode
            ? $"{baseKey}:s{episode.SeasonNumber}e{episode.EpisodeNumber}"
            : baseKey;
    }

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        // Unconfigured and cache-hit calls must not report progress ticks: the
        // frontend shows the sync bar whenever it sees intermediate progress, so
        // a poll cycle that does no network work has to stay silent or the bar
        // flashes every few seconds.
        if (string.IsNullOrEmpty(_options.ClientId))
        {
            logger.LogDebug("Trakt: skipping — no Client ID configured");
            return s_empty;
        }

        string cacheKey = GetCacheKey(mediaContext);
        if (cache.TryGetValue(cacheKey, out ThoughtResult? cached))
        {
            return cached;
        }

        progress = InitialLoadProgress.ForFetch(cache, cacheKey, progress);

        try
        {
            Fetch fetch = new();
            string? traktId = await ResolveIdAsync(mediaContext, fetch, ct);
            if (traktId is null)
            {
                if (fetch.Failed)
                {
                    return CacheFailure(cacheKey);
                }

                logger.LogInformation("Trakt: '{Title}' is not on Trakt", mediaContext.Title);
                cache.Set(cacheKey, s_empty, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
                return s_empty;
            }

            List<Thought> thoughts = await GetCommentsAsync(mediaContext, traktId, fetch, ct);
            if (thoughts.Count == 0 && fetch.Failed)
            {
                return CacheFailure(cacheKey);
            }

            ThoughtResult result = new("Trakt", null, null, null, treeBuilder.BuildTree(thoughts), null)
            {
                IsTransientFailure = fetch.Failed
            };

            cache.Set(cacheKey, result,
                fetch.Failed ? s_failureCacheTtl : TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Trakt thought fetch failed");
            return CacheFailure(cacheKey);
        }
        finally
        {
            progress?.Report(new SyncProgressTick(1, "Trakt"));
        }
    }

    // Episode comments sorted by likes. An episode with few comments gets its season's, then the
    // show's, labelled as such and without comments flagged or marked as spoilers. Replies are
    // fetched only for the most-liked comments that have some.
    private async Task<List<Thought>> GetCommentsAsync(MediaContext mediaContext, string traktId, Fetch fetch,
        CancellationToken ct)
    {
        string id = Uri.EscapeDataString(traktId);
        List<(TraktCommentDto Comment, string? Group)> comments = [];

        if (mediaContext is EpisodeContext episode)
        {
            string show = $"https://api.trakt.tv/shows/{id}";
            AddComments(comments, await GetCommentPageAsync(
                $"{show}/seasons/{episode.SeasonNumber}/episodes/{episode.EpisodeNumber}/comments/likes", fetch, ct), null);

            if (comments.Count < FewComments)
            {
                // The format string is a localized resource (culture-dependent), so it can't be a cached CompositeFormat.
#pragma warning disable CA1863
                string season = string.Format(CultureInfo.CurrentCulture,
                    UiStrings.TraktThoughtProvider_SeasonComments, episode.SeasonNumber);
#pragma warning restore CA1863
                AddComments(comments, SpoilerFree(await GetCommentPageAsync(
                    $"{show}/seasons/{episode.SeasonNumber}/comments/likes", fetch, ct)), season);
            }

            if (comments.Count < FewComments)
            {
                AddComments(comments, SpoilerFree(await GetCommentPageAsync($"{show}/comments/likes", fetch, ct)),
                    UiStrings.TraktThoughtProvider_ShowComments);
            }
        }
        else
        {
            AddComments(comments,
                await GetCommentPageAsync($"https://api.trakt.tv/movies/{id}/comments/likes", fetch, ct), null);
        }

        logger.LogDebug("Trakt: fetched {Count} comment(s) for '{Title}'", comments.Count, mediaContext.Title);

        List<Thought> thoughts = comments.Select(c => ToThought(c.Comment, null, c.Group)).ToList();

        IEnumerable<(TraktCommentDto Comment, string? Group)> worthReplies = comments
            .Where(c => c.Comment is { Id: not null, Replies: > 0, Likes: >= MinLikesForReplies })
            .OrderByDescending(c => c.Comment.Likes)
            .Take(MaxReplyFetches);
        foreach ((TraktCommentDto parent, string? group) in worthReplies)
        {
            TraktCommentDto[]? replies = await GetCommentPageAsync(
                $"https://api.trakt.tv/comments/{parent.Id}/replies", fetch, ct);
            thoughts.AddRange((replies ?? []).Select(r => ToThought(r, $"trakt:{parent.Id}", group)));
        }

        return thoughts;
    }

    private static void AddComments(List<(TraktCommentDto, string?)> into, TraktCommentDto[]? page, string? group)
    {
        HashSet<long?> seen = into.Select(c => c.Item1.Id).ToHashSet();
        into.AddRange((page ?? []).Where(c => seen.Add(c.Id)).Select(c => (c, group)));
    }

    // Season- and show-level comments may discuss later episodes. Trakt flags whole-comment spoilers
    // and users mark parts with [spoiler] tags; skip both.
    private static TraktCommentDto[]? SpoilerFree(TraktCommentDto[]? page) =>
        page?.Where(c => c.Spoiler != true &&
                         c.Comment?.Contains("[spoiler]", StringComparison.OrdinalIgnoreCase) != true)
            .ToArray();

    private static Thought ToThought(TraktCommentDto c, string? parentId, string? group) => new(
        $"trakt:{c.Id}",
        parentId,
        null,
        c.Comment ?? "",
        c.Id != null ? $"https://trakt.tv/comments/{c.Id}" : null,
        [],
        c.User?.Username ?? UiStrings.TraktThoughtProvider_GetThoughtsAsync_Unknown,
        c.Likes,
        c.CreatedAt ?? DateTimeOffset.UtcNow,
        "Trakt",
        [],
        group);

    // Null when the request failed (and marks the fetch failed); empty when there are none.
    private async Task<TraktCommentDto[]?> GetCommentPageAsync(string url, Fetch fetch, CancellationToken ct)
    {
        HttpRequestMessage request = new(HttpMethod.Get, $"{url}?limit={CommentLimit}");
        ConfigureRequest(request);
        HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Trakt comments failed: HTTP {Status} for {Url}", (int)response.StatusCode, url);
            fetch.Failed = true;
            return null;
        }

        string content = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(content, TraktThoughtJsonContext.Default.TraktCommentDtoArray) ?? [];
    }

    private sealed class Fetch
    {
        public bool Failed { get; set; }
    }

    private ThoughtResult CacheFailure(string cacheKey)
    {
        cache.Set(cacheKey, s_failed, s_failureCacheTtl);
        return s_failed;
    }
    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_options.ClientId))
            {
                return new ServiceHealth(
                    false,
                    UiStrings.TraktThoughtProvider_GetServiceHealthAsync_No_Client_ID_configured,
                    DateTimeOffset.UtcNow);
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            HttpRequestMessage request = new(HttpMethod.Get, "https://api.trakt.tv/shows/trending?limit=1");
            IDataProvider.ApplyDefaultHeaders(request);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", _options.ClientId);

            HttpResponseMessage response = await httpClient.SendAsync(request, cts.Token);

            return new ServiceHealth(
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? "OK" : $"HTTP {(int)response.StatusCode}",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new ServiceHealth(
                false,
                ex.Message,
                DateTimeOffset.UtcNow);
        }
    }

    void IDataProvider.ConfigureRequest(HttpRequestMessage request)
    {
        ConfigureRequest(request);
    }

    /// <summary>
    ///     Builds a stable cache key using the best available external ID, falling back
    ///     to title + release year. Title alone is not globally unique (e.g. "The Office" US/UK,
    ///     multiple "Avatar" movies), so a title-only key lets one show's results bleed into another's.
    /// </summary>
    private static string BuildSlugCacheKey(string prefix, MediaContext mediaContext)
    {
        IReadOnlyDictionary<string, string>? ids = mediaContext.ExternalIds;
        if (ids is not null)
        {
            if (ids.TryGetValue(ExternalIdType.Imdb, out string? imdb))
            {
                return $"{prefix}:imdb:{imdb}";
            }

            if (ids.TryGetValue(ExternalIdType.Tvdb, out string? tvdb))
            {
                return $"{prefix}:tvdb:{tvdb}";
            }

            if (ids.TryGetValue(ExternalIdType.Tmdb, out string? tmdb))
            {
                return $"{prefix}:tmdb:{tmdb}";
            }
        }

        int? year = mediaContext.ReleaseDate?.Year;
        return $"{prefix}:title:{mediaContext.Title}:{year?.ToString(CultureInfo.InvariantCulture) ?? ""}";
    }

    /// <summary>
    ///     Finds the Trakt ID of the show (for an episode) or movie, by external ID first and by title
    ///     last. Every answer, including "not on Trakt", is kept in <see cref="IDurableLookupCache" />, so
    ///     an ID is looked up once. A failed request is not kept, and marks the fetch failed.
    /// </summary>
    private async Task<string?> ResolveIdAsync(MediaContext mediaContext, Fetch fetch, CancellationToken ct)
    {
        EpisodeContext? episode = mediaContext as EpisodeContext;
        string kind = episode is null ? "movie" : "show";
        IEnumerable<(string Type, string Value)> externalIds = episode is null
            ? ExternalIdType.GetMovieLookupPriority(mediaContext.ExternalIds)
            : ExternalIdType.GetShowLookupPriority(mediaContext.ExternalIds);

        foreach ((string idType, string idValue) in externalIds)
        {
            string? found = await RememberedAsync($"trakt:{kind}:{idType}:{idValue}", s_idLifetime, fetch,
                () => LookupByIdAsync(idType, idValue, episode, ct), ct);
            if (found is not null)
            {
                return found;
            }
        }

        string title = mediaContext.Title.Trim().ToLowerInvariant();
        int? year = episode is null ? mediaContext.ReleaseDate?.Year : null;
        return await RememberedAsync($"trakt:{kind}:title:{title}:{year}", s_titleLifetime, fetch,
            () => LookupByTitleAsync(mediaContext.Title, year, episode is not null, ct), ct);
    }

    private async Task<string?> RememberedAsync(string key, TimeSpan foundLifetime, Fetch fetch,
        Func<Task<(bool Ok, string? Id)>> lookup, CancellationToken ct)
    {
        if (await ids.GetAsync(key, ct) is { } remembered)
        {
            return remembered.Value;
        }

        (bool ok, string? id) = await lookup();
        if (!ok)
        {
            fetch.Failed = true;
            return null;
        }

        await ids.SetAsync(key, IdStoreKind, id, id is null ? s_noneLifetime : foundLifetime, ct);
        return id;
    }

    // The IDs may be the show's (from Trakt's watch state) or the episode's own (Jellyfin passes the
    // playing item's provider IDs). So for an episode the lookup has no type filter: a show result is
    // taken as is, an episode result gives its show. TVDB and TMDB number shows and episodes separately,
    // so an episode result found by one of those must also have the episode's season and number.
    private async Task<(bool Ok, string? Id)> LookupByIdAsync(string idType, string idValue,
        EpisodeContext? episode, CancellationToken ct)
    {
        string url = $"https://api.trakt.tv/search/{idType}/{Uri.EscapeDataString(idValue)}" +
                     (episode is null ? "?type=movie" : "");
        TraktSearchItemDto[]? items = await SearchAsync(url, ct);
        if (items is null)
        {
            return (false, null);
        }

        if (episode is null)
        {
            return (true, TraktId(items.FirstOrDefault(i => i.Movie is not null)?.Movie?.Ids));
        }

        TraktSearchItemDto? ofEpisode = items.FirstOrDefault(i =>
            i is { Type: "episode", Episode: not null, Show: not null } &&
            (idType == ExternalIdType.Imdb ||
             (i.Episode.Season == episode.SeasonNumber && i.Episode.Number == episode.EpisodeNumber)));
        TraktSearchItemDto? show = ofEpisode ?? items.FirstOrDefault(i => i.Show is not null && i.Type != "episode");
        return (true, TraktId(show?.Show?.Ids));
    }

    // Title search is the last resort: prefer an exact title (and, for movies, year) over the top hit.
    private async Task<(bool Ok, string? Id)> LookupByTitleAsync(string title, int? year, bool isShow,
        CancellationToken ct)
    {
        string type = isShow ? "show" : "movie";
        TraktSearchItemDto[]? items =
            await SearchAsync($"https://api.trakt.tv/search/{type}?query={Uri.EscapeDataString(title)}", ct);
        if (items is null)
        {
            return (false, null);
        }

        TraktShowDto?[] media = items.Select(i => isShow ? i.Show : i.Movie).Where(m => m is not null).ToArray();
        TraktShowDto? best =
            media.FirstOrDefault(m => SameTitle(m!.Title, title) && (year is null || m.Year == year))
            ?? media.FirstOrDefault(m => SameTitle(m!.Title, title))
            ?? media.FirstOrDefault();
        if (best is not null)
        {
            logger.LogDebug("Trakt: '{Title}' matched by title to '{Match}'", title, best.Title);
        }

        return (true, TraktId(best?.Ids));
    }

    private static bool SameTitle(string? a, string b) => string.Equals(a?.Trim(), b.Trim(),
        StringComparison.OrdinalIgnoreCase);

    private static string? TraktId(TraktShowIdsDto? ids) =>
        ids?.Trakt?.ToString(CultureInfo.InvariantCulture) ?? ids?.Slug;

    // Null when the request failed; empty when nothing matched.
    private async Task<TraktSearchItemDto[]?> SearchAsync(string url, CancellationToken ct)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        ConfigureRequest(request);
        HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Trakt search failed: HTTP {Status} for {Url}", (int)response.StatusCode, url);
            return null;
        }

        string content = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(content, TraktThoughtJsonContext.Default.TraktSearchItemDtoArray) ?? [];
    }

    private void ConfigureRequest(HttpRequestMessage request)
    {
        IDataProvider.ApplyDefaultHeaders(request);
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", _options.ClientId);
    }
}

internal sealed record TraktShowIdsDto(
    [property: JsonPropertyName("trakt")] long? Trakt,
    [property: JsonPropertyName("slug")] string? Slug);

// Shows and movies share this shape.
internal sealed record TraktShowDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("ids")] TraktShowIdsDto? Ids,
    [property: JsonPropertyName("year")] int? Year = null);

internal sealed record TraktEpisodeDto(
    [property: JsonPropertyName("season")] int? Season,
    [property: JsonPropertyName("number")] int? Number);

internal sealed record TraktSearchItemDto(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("show")] TraktShowDto? Show,
    [property: JsonPropertyName("movie")] TraktShowDto? Movie,
    [property: JsonPropertyName("episode")] TraktEpisodeDto? Episode);

internal sealed record TraktUserDto(
    [property: JsonPropertyName("username")]
    string? Username);

internal sealed record TraktCommentDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("comment")]
    string? Comment,
    [property: JsonPropertyName("user")] TraktUserDto? User,
    [property: JsonPropertyName("created_at")]
    DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("likes")] int? Likes = null,
    [property: JsonPropertyName("replies")]
    int? Replies = null,
    [property: JsonPropertyName("spoiler")]
    bool? Spoiler = null);
