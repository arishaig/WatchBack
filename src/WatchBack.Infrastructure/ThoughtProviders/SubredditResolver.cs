using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using WatchBack.Infrastructure.Caching;

namespace WatchBack.Infrastructure.ThoughtProviders;

/// <summary>The subreddit found for a show, or none. Failed means the lookup itself failed.</summary>
public readonly record struct SubredditResolution(string? Subreddit, bool Failed = false);

/// <summary>
///     Finds a show's subreddit when the curated mappings have none. Called at most once per show:
///     the answer, including "no subreddit", is kept in memory and in SQLite.
/// </summary>
public interface ISubredditResolver
{
    Task<SubredditResolution> ResolveAsync(string showTitle, CancellationToken ct = default);
}

/// <summary>
///     Resolves a show's subreddit with Arctic Shift's subreddit search. The search is by name prefix,
///     sorted by subscribers, so the biggest match is often unrelated (r/lostredditors for "Lost") and the
///     exact name is often a dead or unrelated sub (r/Severance is a video game). A candidate must look
///     like the show's community: see <see cref="Pick" />. With no convincing candidate it returns none
///     rather than guess, so a wrong subreddit's threads are never shown.
/// </summary>
public sealed partial class ArcticShiftSubredditResolver(
    IHttpClientFactory httpClientFactory,
    ResultCacheStore store,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<ArcticShiftSubredditResolver> logger) : ISubredditResolver
{
    public const string StoreKind = "subreddit";
    private const long MinSubscribers = 1000;

    private static readonly TimeSpan s_foundLifetime = TimeSpan.FromDays(180);

    // A show without a subreddit may get one, so "none" is re-checked sooner.
    private static readonly TimeSpan s_noneLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan s_memoryLifetime = TimeSpan.FromHours(6);

    // Name suffixes that mark a show's subreddit: r/SeveranceAppleTVPlus, r/ShogunTVShow, r/IndustryHBO.
    private static readonly string[] s_showSuffixes =
    [
        "tv", "tvshow", "show", "theshow", "series", "tvseries", "official", "hbo", "hbomax", "max", "appletv",
        "appletvplus", "netflix", "fx", "fxx", "hulu", "amc", "prime", "primevideo", "amazon", "starz",
        "showtime", "paramount", "paramountplus", "peacock", "disney", "disneyplus", "bbc", "itv", "cbs",
        "nbc", "abc", "cw"
    ];

    private static readonly ConcurrentDictionary<string, Task<SubredditResolution>> s_inFlight = new();

    public async Task<SubredditResolution> ResolveAsync(string showTitle, CancellationToken ct = default)
    {
        string name = Normalize(showTitle);
        if (name.Length == 0)
        {
            return new SubredditResolution(null);
        }

        string key = $"reddit:subreddit:{name}";
        if (cache.TryGetValue(key, out string? remembered))
        {
            return new SubredditResolution(remembered!.Length == 0 ? null : remembered);
        }

        // Concurrent lookups of one show (the episode being watched and the prefetched next one) share a fetch.
        TaskCompletionSource<SubredditResolution> mine = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SubredditResolution> shared = s_inFlight.GetOrAdd(key, mine.Task);
        if (shared != mine.Task)
        {
            return await shared.WaitAsync(ct);
        }

        try
        {
            SubredditResolution result = await LookUpAsync(showTitle, name, key, ct);
            mine.SetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            mine.SetException(ex);
            throw;
        }
        finally
        {
            s_inFlight.TryRemove(new KeyValuePair<string, Task<SubredditResolution>>(key, mine.Task));
        }
    }

    private async Task<SubredditResolution> LookUpAsync(string showTitle, string name, string key,
        CancellationToken ct)
    {
        ResultCacheStore.Entry? stored = await store.GetAsync(key, ct);
        if (stored is not null)
        {
            cache.Set(key, stored.Payload, s_memoryLifetime);
            return new SubredditResolution(stored.Payload.Length == 0 ? null : stored.Payload);
        }

        HttpClient client = httpClientFactory.CreateClient(nameof(RedditThoughtProvider));
        ArcticShiftSubredditDto[]? candidates = await ArcticShiftSource.SearchSubredditsAsync(client, name, logger, ct);
        if (candidates is null)
        {
            return new SubredditResolution(null, Failed: true);
        }

        string? picked = Pick(candidates, showTitle);
        logger.LogInformation("Reddit: resolved '{Title}' to {Subreddit}", showTitle,
            picked is null ? "no subreddit" : $"r/{picked}");

        string payload = picked ?? "";
        await store.SetAsync(key, StoreKind, payload,
            time.GetUtcNow() + (picked is null ? s_noneLifetime : s_foundLifetime), ct);
        cache.Set(key, payload, s_memoryLifetime);
        return new SubredditResolution(picked);
    }

    // A candidate qualifies when it is active (MinSubscribers), safe for work, and either its name is the
    // show's name plus a show suffix, or exactly the show's name with a description that reads like a TV
    // community. Any other name starting with the title is a side or unrelated sub (r/Futurama_Sleepers,
    // r/scrubsgonewild), and an exact name alone can be a city or a game (r/Atlanta, r/Severance).
    // The biggest qualifying candidate wins.
    internal static string? Pick(IEnumerable<ArcticShiftSubredditDto> candidates, string showTitle)
    {
        string name = Normalize(showTitle);
        return candidates
            .Where(c => c.DisplayName is not null && c.Over18 != true && (c.Subscribers ?? 0) >= MinSubscribers)
            .Where(c => LooksLikeShowCommunity(c, name))
            .OrderByDescending(c => c.Subscribers)
            .Select(c => c.DisplayName)
            .FirstOrDefault();
    }

    private static bool LooksLikeShowCommunity(ArcticShiftSubredditDto candidate, string name)
    {
        string subName = Normalize(candidate.DisplayName!);
        if (!subName.StartsWith(name, StringComparison.Ordinal))
        {
            return false;
        }

        // "OnNetflix", "_tv": underscores are dropped by Normalize, a leading "on" here.
        string suffix = subName[name.Length..];
        if (s_showSuffixes.Contains(suffix) ||
            (suffix.StartsWith("on", StringComparison.Ordinal) && s_showSuffixes.Contains(suffix[2..])))
        {
            return true;
        }

        return suffix.Length == 0 && TvWords().IsMatch(candidate.PublicDescription ?? "");
    }

    // Lower-case letters and digits only, accents removed: "Shōgun" → "shogun", "The Bear" → "thebear".
    internal static string Normalize(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\b(tv|television|show|series|season|seasons|episode|episodes|sitcom|miniseries)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TvWords();
}
