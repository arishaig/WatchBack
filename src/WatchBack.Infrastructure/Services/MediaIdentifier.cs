using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Infrastructure.Caching;

namespace WatchBack.Infrastructure.Services;

/// <summary>
///     Identifies media known only by name through the media search provider (OMDb): a show by title
///     (and year, between same-titled shows), then the episode by its numbers or, when the guide gave
///     none, by its title. Each answer, found or not, is kept in <see cref="IDurableLookupCache" />, and
///     concurrent askers share one lookup, so the 5-second poll costs nothing after the first.
/// </summary>
public sealed partial class MediaIdentifier(
    IServiceScopeFactory scopeFactory,
    IDurableLookupCache lookups,
    IOptionsMonitor<WatchBackOptions> watchBack,
    ILogger<MediaIdentifier> logger) : IMediaIdentifier
{
    private const string LookupKind = "media-identity";

    // OMDb's free tier is 1,000 requests a day: finding an episode by title reads at most this many seasons.
    internal const int MaxSeasonsScanned = 10;

    private static readonly TimeSpan s_foundLifetime = TimeSpan.FromDays(90);

    // The search provider answers a failed request and "no match" alike, so a miss is asked again tomorrow.
    private static readonly TimeSpan s_missLifetime = TimeSpan.FromDays(1);

    private static readonly TimeSpan s_failureLifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Lazy<Task<Identity?>>> _inFlight = new();

    public async Task<MediaContext> IdentifyAsync(MediaContext media, int? year, CancellationToken ct = default)
    {
        if (media is LiveBroadcastContext || media.ExternalIds is { Count: > 0 })
        {
            return media;
        }

        try
        {
            string key = KeyFor(media, year);
            LookupAnswer? known = await lookups.GetAsync(key, ct);
            Identity? identity;
            if (known is { } answer)
            {
                identity = answer.Value is null
                    ? null
                    : JsonSerializer.Deserialize(answer.Value, CacheJsonContext.Default.Identity);
            }
            else
            {
                Lazy<Task<Identity?>> lookup = _inFlight.GetOrAdd(key,
                    k => new Lazy<Task<Identity?>>(() => LookUpAndRememberAsync(k, media, year)));
                try
                {
                    identity = await lookup.Value.WaitAsync(ct);
                }
                finally
                {
                    _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<Identity?>>>(key, lookup));
                }
            }

            return identity is null ? media : Apply(media, identity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Identifying '{Title}' failed", media.Title);
            return media;
        }
    }

    internal static string KeyFor(MediaContext media, int? year) => media is EpisodeContext e
        ? $"identify:v1:episode:{TitleMatch.Fold(e.Title)}|{year}|{e.SeasonNumber}|{e.EpisodeNumber}|{(e.SeasonNumber > 0 ? "" : TitleMatch.Fold(e.EpisodeTitle))}"
        : $"identify:v1:movie:{TitleMatch.Fold(media.Title)}|{year}";

    // Runs once per key however many viewers ask, in its own scope, so one viewer leaving doesn't cancel it.
    private async Task<Identity?> LookUpAndRememberAsync(string key, MediaContext media, int? year)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        // A provider turned off in settings (to save its quota, say) isn't used here either.
        HashSet<string> disabled = watchBack.CurrentValue.DisabledProviders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IMediaSearchProvider? search = scope.ServiceProvider.GetServices<IMediaSearchProvider>()
            .FirstOrDefault(p => p.IsConfigured && (p.ConfigSection is null || !disabled.Contains(p.ConfigSection)));
        if (search is null)
        {
            return null; // Nothing to ask; not a miss, so nothing is remembered.
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Identity? identity;
        try
        {
            identity = await LookUpAsync(search, media, year, timeout.Token);
        }
        catch (Exception ex)
        {
            // Not asked again on every poll while the search provider is failing.
            logger.LogWarning(ex, "Identifying '{Title}' failed; trying again in an hour", media.Title);
            await lookups.SetAsync(key, LookupKind, null, s_failureLifetime, CancellationToken.None);
            return null;
        }

        await lookups.SetAsync(key, LookupKind,
            identity is null ? null : JsonSerializer.Serialize(identity, CacheJsonContext.Default.Identity),
            identity is null ? s_missLifetime : s_foundLifetime, CancellationToken.None);
        logger.LogInformation("Identified '{Title}' as {Identity}", media.Title, (object?)identity ?? "nothing");
        return identity;
    }

    internal static async Task<Identity?> LookUpAsync(IMediaSearchProvider search, MediaContext media, int? year,
        CancellationToken ct)
    {
        bool isEpisode = media is EpisodeContext;
        IReadOnlyList<MediaSearchResult> results = await search.SearchAsync(media.Title, ct);
        List<MediaSearchResult> sameTitle = results
            .Where(r => r.Type == (isEpisode ? "series" : "movie") && TitleMatch.Same(r.Title, media.Title))
            .ToList();
        MediaSearchResult? title = year is { } y
            ? sameTitle.FirstOrDefault(r => YearFits(r.Year, y, isEpisode))
            : sameTitle.FirstOrDefault();
        if (title is null)
        {
            return null;
        }

        if (media is not EpisodeContext episode)
        {
            return new Identity(title.ImdbId, title.ReleaseDate, null, null);
        }

        if (episode is { SeasonNumber: > 0, EpisodeNumber: > 0 })
        {
            IReadOnlyList<EpisodeInfo> season = await search.GetEpisodesAsync(title.ImdbId, episode.SeasonNumber, ct);
            EpisodeInfo? numbered = season.FirstOrDefault(e => e.EpisodeNumber == episode.EpisodeNumber);
            return new Identity(numbered?.ImdbId ?? title.ImdbId, numbered?.AirDate, null, null);
        }

        // "Episode 8" or "Episodio 44" names no episode: reading every season for it would only spend quota.
        if (!string.IsNullOrWhiteSpace(episode.EpisodeTitle) && !GenericEpisodeTitle().IsMatch(episode.EpisodeTitle))
        {
            IReadOnlyList<SeasonInfo> seasons = await search.GetSeasonsAsync(title.ImdbId, ct);
            foreach (SeasonInfo s in seasons.OrderBy(s => s.SeasonNumber).Take(MaxSeasonsScanned))
            {
                IReadOnlyList<EpisodeInfo> episodes = await search.GetEpisodesAsync(title.ImdbId, s.SeasonNumber, ct);
                if (episodes.FirstOrDefault(e => TitleMatch.Same(e.Title, episode.EpisodeTitle)) is { } titled)
                {
                    return new Identity(titled.ImdbId ?? title.ImdbId, titled.AirDate, titled.SeasonNumber,
                        titled.EpisodeNumber);
                }
            }
        }

        // The show is known even if the episode isn't: its ratings and discussion still apply.
        return new Identity(title.ImdbId, null, null, null);
    }

    // A film's year is within a year of the guide's; an episode's falls within its show's run
    // ("1959–1964", or "2018–" for one still running).
    internal static bool YearFits(string? span, int year, bool isSeries)
    {
        MatchCollection years = YearPattern().Matches(span ?? "");
        if (years.Count == 0)
        {
            return false;
        }

        int first = int.Parse(years[0].Value, CultureInfo.InvariantCulture);
        if (!isSeries)
        {
            return Math.Abs(first - year) <= 1;
        }

        int last = years.Count > 1
            ? int.Parse(years[1].Value, CultureInfo.InvariantCulture)
            : span!.TrimEnd().EndsWith('–') || span.TrimEnd().EndsWith('-') ? int.MaxValue - 1 : first;
        return year >= first - 1 && year <= last + 1;
    }

    private static MediaContext Apply(MediaContext media, Identity identity)
    {
        Dictionary<string, string> ids = new() { [ExternalIdType.Imdb] = identity.ImdbId };
        if (media is EpisodeContext episode)
        {
            return episode with
            {
                ExternalIds = ids,
                ReleaseDate = identity.Aired ?? episode.ReleaseDate,
                SeasonNumber = (short)(identity.Season ?? episode.SeasonNumber),
                EpisodeNumber = (short)(identity.Episode ?? episode.EpisodeNumber)
            };
        }

        return media with { ExternalIds = ids, ReleaseDate = identity.Aired ?? media.ReleaseDate };
    }

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"^\s*(episode|episodio|épisode|folge|ep\.?|chapter|cap[ií]tulo|part|parte)\s*\d+\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GenericEpisodeTitle();
}

/// <summary>What a lookup found: an IMDb id, and for an episode its air date and (if the guide lacked them) numbers.</summary>
public sealed record Identity(string ImdbId, DateTimeOffset? Aired, int? Season, int? Episode);
