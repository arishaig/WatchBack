using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Extensions;
using WatchBack.Resources;

namespace WatchBack.Infrastructure.ThoughtProviders;

[JsonSerializable(typeof(BlueskyAuthResponseDto))]
[JsonSerializable(typeof(BlueskySearchResponseDto))]
internal sealed partial class BlueskyJsonContext : JsonSerializerContext;

public sealed class BlueskyThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<BlueskyOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    ILogger<BlueskyThoughtProvider> logger)
    : IThoughtProvider
{
    private static readonly ThoughtResult s_empty = new("Bluesky", null, null, null, [], null);

    // Returned (and briefly cached) when the lookup itself failed, as opposed to finding nothing,
    // so the thought cache never stores an outage as a confirmed miss.
    private static readonly ThoughtResult s_failed = s_empty with { IsTransientFailure = true };

    // Failed lookups are cached briefly so the 5-second poll loop doesn't redo them every cycle.
    private static readonly TimeSpan s_failureCacheTtl = TimeSpan.FromSeconds(60);

    private readonly BlueskyOptions _options = options.Value;

    public const int MaxSearches = 3;

    // Aired within this long ago: newest posts first. Older: top posts, or the newest would be all
    // rewatch chatter from this week.
    private static readonly TimeSpan s_recentFor = TimeSpan.FromDays(7);

    // The hashtag search covers the posts made while an episode aired and just after.
    private static readonly TimeSpan s_airingWindow = TimeSpan.FromDays(2);

    // Bluesky opened its beta in 2023; nobody posted about earlier airings there.
    private static readonly DateTimeOffset s_blueskyLaunch = new(2023, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Placeholder names ("Episode 5") add nothing to a search.
    private static readonly Regex s_genericEpisodeName =
        new(@"^\s*(episode|ep\.?|chapter|part)\s*\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DataProviderMetadata Metadata => new(
        "Bluesky",
        UiStrings.BlueskyThoughtProvider_Metadata_Bluesky_skeets,
        BrandData: new BrandData(
            "#1185FE",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Bluesky</title><path d=\"M5.202 2.857C7.954 4.922 10.913 9.11 12 11.358c1.087-2.247 4.046-6.436 6.798-8.501C20.783 1.366 24 .213 24 3.883c0 .732-.42 6.156-.667 7.037-.856 3.061-3.978 3.842-6.755 3.37 4.854.826 6.089 3.562 3.422 6.299-5.065 5.196-7.28-1.304-7.847-2.97-.104-.305-.152-.448-.153-.327 0-.121-.05.022-.153.327-.568 1.666-2.782 8.166-7.847 2.97-2.667-2.737-1.432-5.473 3.422-6.3-2.777.473-5.899-.308-6.755-3.369C.42 10.04 0 4.615 0 3.883c0-3.67 3.217-2.517 5.202-1.026\"/></svg>"
        )
    );

    public int ExpectedWeight => 1;

    public bool SupportsLiveBroadcasts => true;

    public string GetCacheKey(MediaContext mediaContext)
    {
        return mediaContext is EpisodeContext episode
            ? $"bluesky:thoughts:{mediaContext.Title}:S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}"
            : $"bluesky:thoughts:{mediaContext.Title}";
    }

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        // Cache hits must not report progress ticks: the frontend shows the sync
        // bar whenever it sees intermediate progress, so a fully cached poll
        // cycle has to stay silent or the bar flashes every few seconds.
        // searchPosts rejects unauthenticated requests, so without credentials there is nothing to ask.
        if (!IsConfigured)
        {
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
            string? token = await GetAccessTokenAsync(ct);
            if (string.IsNullOrEmpty(token))
            {
                logger.LogWarning("Bluesky: sign-in failed; skipping search");
                return CacheFailure(cacheKey);
            }

            IReadOnlyList<SearchSpec> searches = PlanSearches(mediaContext, DateTimeOffset.UtcNow);
            DateTimeOffset? dateFloor = IThoughtProvider.GetDateFloor(mediaContext);
            HashSet<string> seenTexts = new();
            HashSet<string> seenUris = new();
            List<Thought> thoughts = new();
            int failed = 0;

            foreach (SearchSpec search in searches)
            {
                // A live broadcast's names go from most to least specific: stop at the first that finds posts.
                if (mediaContext is LiveBroadcastContext && thoughts.Count > 0)
                {
                    break;
                }

                (BlueskyPostDto[]? posts, token) = await SearchAsync(search, token, ct);
                if (posts is null)
                {
                    failed++;
                    continue;
                }

                foreach (BlueskyPostDto post in posts)
                {
                    if (post.Record?.Text == null || (post.Uri is not null && !seenUris.Add(post.Uri)))
                    {
                        continue;
                    }

                    if (dateFloor.HasValue && (post.Record.CreatedAt ?? DateTimeOffset.UtcNow) < dateFloor.Value)
                    {
                        continue;
                    }

                    if (!seenTexts.Add(NormalizeText(post.Record.Text)))
                    {
                        continue;
                    }

                    thoughts.Add(ToThought(post));
                }
            }

            if (failed == searches.Count)
            {
                logger.LogWarning("Bluesky search failed for '{Title}'", mediaContext.Title);
                return CacheFailure(cacheKey);
            }

            ThoughtResult result = new("Bluesky", searches[0].Query, null, null, treeBuilder.BuildTree(thoughts),
                null)
            { IsTransientFailure = failed > 0 };

            cache.Set(cacheKey, result,
                failed > 0 ? s_failureCacheTtl
                : mediaContext is LiveBroadcastContext ? LiveBroadcastContext.CacheLifetime
                : TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bluesky thought fetch failed");
            // A cancelled request (viewer disconnected) isn't a Bluesky failure; let the next viewer retry.
            return ct.IsCancellationRequested ? s_empty : CacheFailure(cacheKey);
        }
        finally
        {
            progress?.Report(new SyncProgressTick(1, "Bluesky"));
        }
    }

    /// <summary>One searchPosts call: the query, its sort, and an optional created-at window.</summary>
    internal sealed record SearchSpec(string Query, string Sort, DateTimeOffset? Since, DateTimeOffset? Until);

    /// <summary>
    ///     The searches for one lookup, at most <see cref="MaxSearches" />. For an episode: the quoted show
    ///     title with the episode code, then with the episode title, then the show's hashtag in the days
    ///     around the air date (posts from while it aired, which the Time Machine view shows). Episodes that
    ///     aired more than a week ago are searched by top posts, recent ones by latest. Movies and specials
    ///     get one search. A live broadcast: the latest posts of the past day under each of its names, until one finds posts.
    /// </summary>
    internal static IReadOnlyList<SearchSpec> PlanSearches(MediaContext media, DateTimeOffset now)
    {
        if (media is LiveBroadcastContext live)
        {
            return live.SearchNames
                .Select(name => new SearchSpec($"\"{name}\"", "latest", now - LiveBroadcastContext.RecentWindow, null))
                .Take(MaxSearches)
                .ToList();
        }

        DateTimeOffset? air = media.ReleaseDate;
        string sort = air is { } aired && now - aired > s_recentFor ? "top" : "latest";
        DateTimeOffset? since = IThoughtProvider.GetDateFloor(media);

        if (media is not EpisodeContext { SeasonNumber: > 0, EpisodeNumber: > 0 } episode)
        {
            return [new SearchSpec(IThoughtProvider.BuildTextQuery(media), sort, since, null)];
        }

        string show = $"\"{media.Title}\"";
        List<SearchSpec> searches = [new($"{show} S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}", sort, since, null)];

        if (!string.IsNullOrWhiteSpace(episode.EpisodeTitle) && !s_genericEpisodeName.IsMatch(episode.EpisodeTitle))
        {
            searches.Add(new SearchSpec($"{show} \"{episode.EpisodeTitle}\"", sort, since, null));
        }

        string hashtag = Hashtag(media.Title);
        if (air is { } airDate && airDate >= s_blueskyLaunch && airDate <= now && hashtag.Length > 1)
        {
            searches.Add(new SearchSpec(hashtag, "top", airDate.AddHours(-12), airDate + s_airingWindow));
        }

        return searches.Take(MaxSearches).ToList();
    }

    // "#TheBear" from "The Bear": the form shows' official hashtags take. Letters and digits only.
    internal static string Hashtag(string title) =>
        "#" + new string(title.Where(char.IsLetterOrDigit).ToArray());

    private async Task<(BlueskyPostDto[]? Posts, string? Token)> SearchAsync(SearchSpec search, string? token,
        CancellationToken ct)
    {
        string url =
            $"https://bsky.social/xrpc/app.bsky.feed.searchPosts?q={Uri.EscapeDataString(search.Query)}&sort={search.Sort}&limit=100";
        if (search.Since is { } since)
        {
            url += $"&since={Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}";
        }

        if (search.Until is { } until)
        {
            url += $"&until={Uri.EscapeDataString(until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}";
        }

        HttpResponseMessage response = await SendSearchAsync(url, token, ct);

        // An access token that expired mid-TTL: refresh (or sign in again) and retry once.
        if (await IsExpiredTokenAsync(response, ct) && !string.IsNullOrEmpty(token))
        {
            response.Dispose();
            ExpireAccessToken();
            token = await GetAccessTokenAsync(ct);
            response = await SendSearchAsync(url, token, ct);
        }

        // A 400 that isn't about the token is about the search itself (a parameter Bluesky won't take).
        // Retrying can't fix it, so it counts as finding nothing rather than as a transient failure.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            logger.LogWarning("Bluesky rejected the search '{Query}' (HTTP 400): {Body}", search.Query,
                await response.Content.ReadAsStringAsync(ct));
            return ([], token);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Bluesky search failed: HTTP {Status} for '{Query}'", (int)response.StatusCode,
                search.Query);
            return (null, token);
        }

        string content = await response.Content.ReadAsStringAsync(ct);
        BlueskySearchResponseDto? result =
            JsonSerializer.Deserialize(content, BlueskyJsonContext.Default.BlueskySearchResponseDto);
        return (result?.Posts ?? [], token);
    }

    // 401, or the 400 {"error":"ExpiredToken"} the PDS sends for an expired access token.
    private static async Task<bool> IsExpiredTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.BadRequest)
        {
            return false;
        }

        string body = await response.Content.ReadAsStringAsync(ct);
        return body.Contains("ExpiredToken", StringComparison.Ordinal) ||
               body.Contains("InvalidToken", StringComparison.Ordinal);
    }

    private static Thought ToThought(BlueskyPostDto post) => new(
        $"bluesky:{post.Uri}",
        null,
        null,
        post.Record!.Text!,
        ToBlueskyWebUrl(post.Uri, post.Author?.Handle),
        post.Record.Embed?.Images?
            .Where(i => !string.IsNullOrEmpty(i.Image?.Link))
            .Select(i => new ThoughtImage(i.Image!.Link!, i.Alt))
            .ToList() ?? [],
        post.Author?.DisplayName ?? post.Author?.Handle ?? "Unknown",
        post.LikeCount,
        post.Record.CreatedAt ?? DateTimeOffset.UtcNow,
        "Bluesky",
        []);

    private ThoughtResult CacheFailure(string cacheKey)
    {
        cache.Set(cacheKey, s_failed, s_failureCacheTtl);
        return s_failed;
    }

    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        try
        {
            string? token = await GetAccessTokenAsync(ct);

            return new ServiceHealth(
                !string.IsNullOrEmpty(token),
                !string.IsNullOrEmpty(token) ? "OK" : "Failed to authenticate",
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

    public string ConfigSection => "Bluesky";

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_options.Handle) && !string.IsNullOrEmpty(_options.AppPassword);

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return
        [
            new ProviderConfigField("Bluesky__Handle",
                UiStrings.ConfigEndpoints_GetConfig_Handle_Email,
                "text",
                "you.bsky.social",
                !string.IsNullOrEmpty(_options.Handle),
                _options.Handle,
                envVal("Bluesky__Handle"),
                isOverridden("Bluesky", "Handle")),
            new ProviderConfigField("Bluesky__AppPassword",
                UiStrings.ConfigEndpoints_GetConfig_Bluesky_App_Password,
                "password",
                "xxxx-xxxx-xxxx-xxxx",
                !string.IsNullOrEmpty(_options.AppPassword),
                "",
                "",
                isOverridden("Bluesky", "AppPassword"))
        ];
    }

    public async Task<ServiceHealth> TestConnectionAsync(
        IReadOnlyDictionary<string, string> formValues,
        CancellationToken ct = default)
    {
        string handle = formValues.ResolveFormValue("Bluesky__Handle", _options.Handle);
        string password = formValues.ResolveFormValue("Bluesky__AppPassword", _options.AppPassword);

        if (string.IsNullOrEmpty(handle))
        {
            return new ServiceHealth(false, UiStrings.ConfigEndpoints_TestBluesky_Handle_required,
                DateTimeOffset.UtcNow);
        }

        if (string.IsNullOrEmpty(password))
        {
            return new ServiceHealth(true, UiStrings.ConfigEndpoints_TestBluesky_Handle_set__no_app_password_to_verify_,
                DateTimeOffset.UtcNow);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        HttpRequestMessage req = new(HttpMethod.Post, "https://bsky.social/xrpc/com.atproto.server.createSession");
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { identifier = handle, password }),
            Encoding.UTF8, "application/json");
        HttpResponseMessage res = await httpClient.SendAsync(req, cts.Token);

        return res.IsSuccessStatusCode
            ? new ServiceHealth(true, UiStrings.ConfigEndpoints_TestJellyfin_Connected, DateTimeOffset.UtcNow)
            : new ServiceHealth(false,
                res.StatusCode == HttpStatusCode.Unauthorized
                    ? UiStrings.ConfigEndpoints_TestBluesky_Invalid_credentials
                    : $"HTTP {(int)res.StatusCode}",
                DateTimeOffset.UtcNow);
    }

    public string? RevealSecret(string key)
    {
        return key == "Bluesky__AppPassword" ? _options.AppPassword : null;
    }

    private Task<HttpResponseMessage> SendSearchAsync(string url, string? token, CancellationToken ct)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        return httpClient.SendAsync(request, ct);
    }

    // Signing in (createSession) is limited per account to a handful a day (bsky.social sent
    // "ratelimit-policy: 10;w=86400"), so the session is kept and its refresh token used instead:
    // the access token lasts a couple of hours, the refresh token weeks.
    private sealed record Session(string AccessJwt, string? RefreshJwt, DateTimeOffset? AccessExpires);

    private static readonly TimeSpan s_sessionLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan s_expiryMargin = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim s_sessionGate = new(1, 1);

    private void ExpireAccessToken()
    {
        if (cache.TryGetValue(GetTokenCacheKey(), out Session? session) && session is not null)
        {
            cache.Set(GetTokenCacheKey(), session with { AccessExpires = DateTimeOffset.MinValue },
                s_sessionLifetime);
        }
    }

    private string GetTokenCacheKey()
    {
        string credentialHash = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes($"{_options.Handle}:{_options.AppPassword}")))[..16];
        return $"bluesky:auth:session:{credentialHash}";
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.Handle) || string.IsNullOrEmpty(_options.AppPassword))
        {
            return null;
        }

        string cacheKey = GetTokenCacheKey();
        if (UsableToken(cacheKey) is { } token)
        {
            return token;
        }

        // The watched episode and the prefetched next one can both find the token expiring. Refresh
        // tokens rotate, so only one may refresh; the other then reuses the new session.
        await s_sessionGate.WaitAsync(ct);
        try
        {
            if (UsableToken(cacheKey) is { } refreshedMeanwhile)
            {
                return refreshedMeanwhile;
            }

            cache.TryGetValue(cacheKey, out Session? session);
            if (session?.RefreshJwt is { } refreshJwt)
            {
                HttpRequestMessage refresh = new(HttpMethod.Post,
                    "https://bsky.social/xrpc/com.atproto.server.refreshSession");
                refresh.Headers.Add("Authorization", $"Bearer {refreshJwt}");
                HttpResponseMessage response = await httpClient.SendAsync(refresh, ct);
                if (await ReadSessionAsync(response, ct) is { } refreshed)
                {
                    cache.Set(cacheKey, refreshed, s_sessionLifetime);
                    return refreshed.AccessJwt;
                }

                // An outage or rate limit says nothing about the refresh token: keep it for the next try
                // rather than spend one of the day's few sign-ins. Only a rejected token means signing in.
                if (!await IsExpiredTokenAsync(response, ct))
                {
                    logger.LogWarning("Bluesky: session refresh failed (HTTP {Status}); will retry",
                        (int)response.StatusCode);
                    return null;
                }
            }

            var authPayload = new { identifier = _options.Handle, password = _options.AppPassword };
            StringContent content = new(JsonSerializer.Serialize(authPayload), Encoding.UTF8, "application/json");
            HttpResponseMessage signIn = await httpClient.PostAsync(
                "https://bsky.social/xrpc/com.atproto.server.createSession", content, ct);
            if (await ReadSessionAsync(signIn, ct) is not { } created)
            {
                logger.LogWarning("Bluesky: sign-in failed (HTTP {Status})", (int)signIn.StatusCode);
                return null;
            }

            cache.Set(cacheKey, created, s_sessionLifetime);
            return created.AccessJwt;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Bluesky: sign-in failed");
            return null;
        }
        finally
        {
            s_sessionGate.Release();
        }
    }

    private string? UsableToken(string cacheKey) =>
        cache.TryGetValue(cacheKey, out Session? session) && session is not null &&
        (session.AccessExpires is null || session.AccessExpires - DateTimeOffset.UtcNow > s_expiryMargin)
            ? session.AccessJwt
            : null;

    private static async Task<Session?> ReadSessionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        BlueskyAuthResponseDto? auth = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct),
            BlueskyJsonContext.Default.BlueskyAuthResponseDto);
        return auth?.AccessJwt is { } access
            ? new Session(access, auth.RefreshJwt, JwtExpiry(access))
            : null;
    }

    // The "exp" claim of a JWT, without verifying it: only used to refresh before it lapses.
    internal static DateTimeOffset? JwtExpiry(string jwt)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using JsonDocument doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out JsonElement exp) && exp.TryGetInt64(out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Converts an AT Protocol URI (at://did:plc:xxx/app.bsky.feed.post/rkey)
    ///     to a browsable https://bsky.app URL.
    /// </summary>
    private static string? ToBlueskyWebUrl(string? atUri, string? handle)
    {
        if (string.IsNullOrEmpty(atUri))
        {
            return null;
        }

        int lastSlash = atUri.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return atUri;
        }

        string rkey = atUri[(lastSlash + 1)..];
        if (string.IsNullOrEmpty(rkey))
        {
            return atUri;
        }

        string? authority = handle;
        if (string.IsNullOrEmpty(authority) && atUri.StartsWith("at://", StringComparison.Ordinal))
        {
            string afterScheme = atUri[5..];
            int slashIdx = afterScheme.IndexOf('/');
            authority = slashIdx > 0 ? afterScheme[..slashIdx] : afterScheme;
        }

        return !string.IsNullOrEmpty(authority)
            ? $"https://bsky.app/profile/{authority}/post/{rkey}"
            : atUri;
    }

    private static string NormalizeText(string text)
    {
        return text.ToLowerInvariant().Trim();
    }
}

internal sealed record BlueskyAuthResponseDto(
    [property: JsonPropertyName("accessJwt")]
    string? AccessJwt,
    [property: JsonPropertyName("refreshJwt")]
    string? RefreshJwt = null);

internal sealed record BlueskyImageDto(
    [property: JsonPropertyName("link")] string? Link);

internal sealed record BlueskyEmbedImageDto(
    [property: JsonPropertyName("image")] BlueskyImageDto? Image,
    [property: JsonPropertyName("alt")] string? Alt);

internal sealed record BlueskyEmbedDto(
    [property: JsonPropertyName("images")] BlueskyEmbedImageDto[]? Images);

internal sealed record BlueskyRecordDto(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("createdAt")]
    DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("embed")] BlueskyEmbedDto? Embed);

internal sealed record BlueskyAuthorDto(
    [property: JsonPropertyName("handle")] string? Handle,
    [property: JsonPropertyName("displayName")]
    string? DisplayName);

internal sealed record BlueskyPostDto(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("cid")] string? Cid,
    [property: JsonPropertyName("author")] BlueskyAuthorDto? Author,
    [property: JsonPropertyName("record")] BlueskyRecordDto? Record,
    [property: JsonPropertyName("likeCount")]
    int? LikeCount);

internal sealed record BlueskySearchResponseDto(
    [property: JsonPropertyName("posts")] BlueskyPostDto[]? Posts);
