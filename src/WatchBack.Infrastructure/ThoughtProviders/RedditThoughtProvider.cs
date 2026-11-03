using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Resources;

namespace WatchBack.Infrastructure.ThoughtProviders;

[JsonSerializable(typeof(PullPushSubmissionsResponseDto))]
[JsonSerializable(typeof(PullPushCommentsResponseDto))]
[JsonSerializable(typeof(ArcticShiftSubmissionsResponseDto))]
[JsonSerializable(typeof(ArcticShiftCommentTreeResponseDto))]
[JsonSerializable(typeof(ArcticShiftCommentNodeDto[]))]
[JsonSerializable(typeof(ArcticShiftSubredditsResponseDto))]
internal sealed partial class RedditJsonContext : JsonSerializerContext;

public sealed class RedditThoughtProvider(
    HttpClient httpClient,
    IOptionsSnapshot<RedditOptions> options,
    IMemoryCache cache,
    IReplyTreeBuilder treeBuilder,
    ISubredditMappingService mappingService,
    ISubredditResolver resolver,
    ILogger<RedditThoughtProvider> logger)
    : IThoughtProvider, IContentNormalizer
{
    // Post searches per lookup (see SearchAsync). Each reports one progress tick, as does each thread's
    // comment fetch; the subreddit lookup, at most one per show, reports none.
    private const int MaxSearches = 2;
    private static readonly ThoughtResult s_empty = new("Reddit", null, null, null, [], null);

    // Returned (and briefly cached) when the lookup itself failed, as opposed to finding nothing,
    // so the thought cache never stores an outage as a confirmed miss.
    private static readonly ThoughtResult s_failed = s_empty with { IsTransientFailure = true };

    // A miss from a lookup with no air date. Prefetch predicts the next episode without knowing its date,
    // so Arctic Shift can only list the subreddit's newest posts: for an old show the thread is never there.
    // That miss is cached as this distinct instance (compared by reference) so a later lookup of the same
    // episode that does know its air date searches properly instead of trusting it.
    private static readonly ThoughtResult s_undatedMiss = new("Reddit", null, null, null, [], null);

    private static readonly Regex s_excessiveNewlines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex s_spoilerTag = new(@">!(.+?)!<", RegexOptions.Compiled | RegexOptions.Singleline);

    // Detects explicit season references in a submission title (SxxEyy, NxYY, or "Season N").
    // Used to reject bypass results that name a different season than the one being fetched.
    private static readonly Regex s_explicitSeasonRef = new(
        @"\bS(\d{1,2})E\d+\b|\b(\d{1,2})x\d+\b|\bSeason\s+(\d+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_episodeCode = new(
        @"\bS(\d{1,2})E(\d{1,3})\b|\b(\d{1,2})x(\d{1,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_premiere = new(@"\bprem(?:ie|ei)re\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly TimeSpan s_failureCacheTtl = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<(int, int), Regex[]> s_episodeRegexCache = new();

    private readonly RedditOptions _options = options.Value;

    public DataProviderMetadata Metadata => new(
        "Reddit",
        UiStrings.RedditThoughtProvider_Metadata_Comments_from_Reddit,
        BrandData: new BrandData(
            "#FF4500",
            "<svg role=\"img\" viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><title>Reddit</title><path d=\"M12 0C5.373 0 0 5.373 0 12c0 3.314 1.343 6.314 3.515 8.485l-2.286 2.286C.775 23.225 1.097 24 1.738 24H12c6.627 0 12-5.373 12-12S18.627 0 12 0Zm4.388 3.199c1.104 0 1.999.895 1.999 1.999 0 1.105-.895 2-1.999 2-.946 0-1.739-.657-1.947-1.539v.002c-1.147.162-2.032 1.15-2.032 2.341v.007c1.776.067 3.4.567 4.686 1.363.473-.363 1.064-.58 1.707-.58 1.547 0 2.802 1.254 2.802 2.802 0 1.117-.655 2.081-1.601 2.531-.088 3.256-3.637 5.876-7.997 5.876-4.361 0-7.905-2.617-7.998-5.87-.954-.447-1.614-1.415-1.614-2.538 0-1.548 1.255-2.802 2.803-2.802.645 0 1.239.218 1.712.585 1.275-.79 2.881-1.291 4.64-1.365v-.01c0-1.663 1.263-3.034 2.88-3.207.188-.911.993-1.595 1.959-1.595Zm-8.085 8.376c-.784 0-1.459.78-1.506 1.797-.047 1.016.64 1.429 1.426 1.429.786 0 1.371-.369 1.418-1.385.047-1.017-.553-1.841-1.338-1.841Zm7.406 0c-.786 0-1.385.824-1.338 1.841.047 1.017.634 1.385 1.418 1.385.785 0 1.473-.413 1.426-1.429-.046-1.017-.721-1.797-1.506-1.797Zm-3.703 4.013c-.974 0-1.907.048-2.77.135-.147.015-.241.168-.183.305.483 1.154 1.622 1.964 2.953 1.964 1.33 0 2.47-.81 2.953-1.964.057-.137-.037-.29-.184-.305-.863-.087-1.795-.135-2.769-.135Z\"/></svg>"
        )
    );

    public int ExpectedWeight => (MaxSearches + _options.MaxThreads) * 3;

    public string GetCacheKey(MediaContext mediaContext)
    {
        return mediaContext is EpisodeContext episode
            ? $"reddit:thoughts:{mediaContext.Title}:S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}:t{_options.MaxThreads}"
            : $"reddit:thoughts:{mediaContext.Title}:t{_options.MaxThreads}";
    }

    public async Task<ThoughtResult?> GetThoughtsAsync(MediaContext mediaContext,
        IProgress<SyncProgressTick>? progress = null, CancellationToken ct = default)
    {
        try
        {
            EpisodeContext? episode = mediaContext as EpisodeContext;

            string cacheKey = GetCacheKey(mediaContext);
            if (cache.TryGetValue(cacheKey, out ThoughtResult? cached) &&
                !(mediaContext.ReleaseDate is not null && ReferenceEquals(cached, s_undatedMiss)))
            {
                return cached;
            }

            progress = InitialLoadProgress.ForFetch(cache, cacheKey, progress);

            Lookup lookup = new(mediaContext, episode, MaxSearches);
            await SearchAsync(lookup, progress, ct);

            List<PullPushSubmissionDto> submissions = lookup.Ranked();
            LogMatches(lookup, submissions);

            if (submissions.Count == 0)
            {
                ThoughtResult emptyResult = lookup.Incomplete ? s_failed
                    : mediaContext.ReleaseDate is null ? s_undatedMiss : s_empty;
                // Confirmed misses get the full TTL. Failures are only cached briefly: retrying every poll
                // cycle would re-report progress ticks and flash the loading bar while the archives fail.
                cache.Set(cacheKey, emptyResult,
                    lookup.Incomplete ? s_failureCacheTtl : TimeSpan.FromSeconds(_options.CacheTtlSeconds));
                return emptyResult;
            }

            int commentFetchFailed = 0;
            Thought[][] threadResults = await Task.WhenAll(
                submissions.Take(_options.MaxThreads).Select(async submission =>
                {
                    string thisPostUrl = PostUrl(submission);
                    PullPushCommentDto[]? comments;
                    try
                    {
                        comments = await GetCommentsAsync(submission, ct);
                    }
                    finally
                    {
                        progress?.Report(new SyncProgressTick(3, "Reddit"));
                    }

                    if (comments is null)
                    {
                        Interlocked.Exchange(ref commentFetchFailed, 1);
                    }

                    logger.LogInformation("Reddit: thread {ThreadId} ({Title}) returned {Count} comment(s)",
                        submission.Id, submission.Title, comments?.Length ?? 0);

                    // Normalization (newlines, spoiler tags) is applied later by SyncService via NormalizeContent.
                    string? postBody = PostBody(submission);
                    return (comments ?? [])
                        .Select(c => MapCommentToThought(c, submission.Title, thisPostUrl, postBody))
                        .Where(t => !IsDeletedOrRemoved(t))
                        .ToArray();
                }));

            List<Thought> allThoughts = threadResults.SelectMany(t => t).ToList();
            IReadOnlyList<Thought> treeThoughts = treeBuilder.BuildTree(allThoughts);

            PullPushSubmissionDto top = submissions[0];

            // A thread whose comments no archive returned, or a failed search, makes this answer incomplete:
            // keep it (it beats nothing) but flag it so it is never persisted as the episode's final result.
            ThoughtResult result = new(
                "Reddit",
                top.Title,
                PostUrl(top),
                null,
                treeThoughts,
                null,
                IsTransientFailure: lookup.Incomplete || commentFetchFailed != 0);

            cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheTtlSeconds));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reddit thought fetch failed");
            return s_failed;
        }
    }

    // At most MaxSearches post searches per lookup, in this order, stopping once something matches:
    //   1. Arctic Shift: list each subreddit's posts around the air date (or its newest posts, undated).
    //      For a movie with no mapped subreddit, search r/movies' titles instead.
    //   2. Only if nothing matched:
    //      a. the next page of a window too busy for one page;
    //      b. PullPush, for episodes aired before PullPushCutoff (Arctic Shift found nothing or failed);
    //      c. a title search of r/television (episodes) or r/movies (movies with a mapped subreddit).
    // Step 1 failing makes the answer incomplete. Step 2b/2c failing doesn't: they are long shots, and
    // flagging them would turn a clean miss into a re-fetch every minute.
    private async Task SearchAsync(Lookup lookup, IProgress<SyncProgressTick>? progress, CancellationToken ct)
    {
        MediaContext media = lookup.Media;
        DateTimeOffset? airDate = media.ReleaseDate;
        bool pullPushEligible = _options.UsePullPush && airDate < _options.PullPushCutoff;
        (long After, long Before)? window = airDate is { } aired
            ? lookup.Episode is null
                ? (aired.AddDays(-14).ToUnixTimeSeconds(), aired.AddDays(90).ToUnixTimeSeconds())
                : (aired.AddDays(-2).ToUnixTimeSeconds(), aired.AddDays(10).ToUnixTimeSeconds())
            : null;

        IReadOnlyList<string> subreddits = await ResolveSubredditsAsync(lookup, ct);

        async Task<ListingPage> Run(Func<Task<ListingPage>> search)
        {
            lookup.SearchesLeft--;
            try
            {
                return await search();
            }
            finally
            {
                progress?.Report(new SyncProgressTick(3, "Reddit"));
            }
        }

        // 1. Primary searches.
        (string Subreddit, long Cursor)? fullWindow = null;
        bool primaryAnswered = false;
        foreach (string subreddit in subreddits.Take(MaxSearches))
        {
            ListingPage page;
            if (_options.UseArcticShift)
            {
                page = await Run(() => window is { } w
                    ? ArcticShiftSource.ListWindowAsync(httpClient, subreddit, w.After, w.Before, logger, ct)
                    : ArcticShiftSource.ListRecentAsync(httpClient, subreddit, logger, ct));
            }
            else if (pullPushEligible && window is { } w)
            {
                page = await Run(() => PullPushSource.ListWindowAsync(httpClient, subreddit, w.After, w.Before, logger, ct));
            }
            else
            {
                break; // No archive covers this lookup.
            }

            if (page.Posts is null)
            {
                lookup.Incomplete = true;
                continue;
            }

            primaryAnswered = true;
            lookup.Collect(page.Posts, requireShowTitle: false);
            if (page.Full && window is not null && page.LastCreated is { } last)
            {
                fullWindow ??= (subreddit, last);
            }
        }

        bool moviesIsPrimary = lookup.Episode is null && subreddits.Count == 0 && _options.UseArcticShift;
        if (moviesIsPrimary && window is { } movieWindow)
        {
            ListingPage page = await Run(() => SearchMoviesAsync(media.Title, movieWindow, ct));
            if (page.Posts is null)
            {
                lookup.Incomplete = true;
            }
            else
            {
                primaryAnswered = true;
                lookup.Collect(page.Posts, requireShowTitle: true);
            }
        }

        if (lookup.HasMatches || lookup.SearchesLeft <= 0 || window is not { } win)
        {
            return;
        }

        // 2a. The window had more posts than one page holds: read on.
        if (fullWindow is { } full && _options.UseArcticShift)
        {
            ListingPage page = await Run(() =>
                ArcticShiftSource.ListWindowAsync(httpClient, full.Subreddit, full.Cursor, win.Before, logger, ct));
            if (page.Posts is null)
            {
                lookup.Incomplete = true;
            }
            else
            {
                lookup.Collect(page.Posts, requireShowTitle: false);
            }

            return;
        }

        // 2b. PullPush, for old episodes only, when Arctic Shift had nothing.
        if (_options.UseArcticShift && pullPushEligible && subreddits.Count > 0)
        {
            ListingPage page = await Run(() =>
                PullPushSource.ListWindowAsync(httpClient, subreddits[0], win.After, win.Before, logger, ct));
            if (page.Posts is not null)
            {
                lookup.Collect(page.Posts, requireShowTitle: false);
            }

            return;
        }

        // 2c. A general subreddit, when Arctic Shift is up but the show's own subreddit had nothing.
        if (_options.UseArcticShift && (primaryAnswered || subreddits.Count == 0) && !moviesIsPrimary)
        {
            ListingPage page = await Run(() => lookup.Episode is null
                ? SearchMoviesAsync(media.Title, win, ct)
                : ArcticShiftSource.SearchTitleAsync(httpClient, "television", media.Title,
                    win.After, Math.Min(win.Before, media.ReleaseDate!.Value.AddDays(3).ToUnixTimeSeconds()),
                    logger, ct));
            if (page.Posts is not null)
            {
                lookup.Collect(page.Posts, requireShowTitle: true);
            }
        }
    }

    // r/movies' "Official Discussion - <Title>" threads, by title search: the subreddit is too busy to list.
    private Task<ListingPage> SearchMoviesAsync(string title, (long After, long Before) window, CancellationToken ct)
    {
        string keywords = Regex.Replace($"Official Discussion {title}", "[^A-Za-z0-9]+", " ").Trim();
        return ArcticShiftSource.SearchTitleAsync(httpClient, "movies", keywords, window.After, window.Before,
            logger, ct);
    }

    // Curated mappings first. Otherwise, for a show, the subreddit Arctic Shift's subreddit search finds
    // (looked up once per show and remembered). Without Arctic Shift, PullPush gets the old guess: the
    // title with the spaces and punctuation removed.
    private async Task<IReadOnlyList<string>> ResolveSubredditsAsync(Lookup lookup, CancellationToken ct)
    {
        IReadOnlyList<string> mapped = mappingService.GetSubreddits(lookup.Media);
        if (mapped.Count > 0 || lookup.Episode is null)
        {
            return mapped;
        }

        if (!_options.UseArcticShift)
        {
            return [Regex.Replace(lookup.Media.Title.ToLowerInvariant(), "[^a-z0-9]", "")];
        }

        SubredditResolution resolution = await resolver.ResolveAsync(lookup.Media.Title, ct);
        if (resolution.Failed)
        {
            lookup.Incomplete = true;
        }

        return resolution.Subreddit is { } found ? [found] : [];
    }

    // Arctic Shift has every thread's comments, so it is asked first; PullPush only when that failed and
    // the thread is old enough for PullPush to have it. Null when no archive answered.
    private async Task<PullPushCommentDto[]?> GetCommentsAsync(PullPushSubmissionDto submission, CancellationToken ct)
    {
        PullPushCommentDto[]? comments = null;
        if (_options.UseArcticShift)
        {
            comments = await ArcticShiftSource.GetCommentsAsync(httpClient, submission.Id!, _options.MaxComments,
                logger, ct);
        }

        if (comments is null && _options.UsePullPush &&
            submission.CreatedUtc is { } created &&
            DateTimeOffset.FromUnixTimeSeconds(created) < _options.PullPushCutoff)
        {
            comments = await PullPushSource.GetCommentsAsync(httpClient, submission.Id!, _options.MaxComments,
                logger, ct);
        }

        return comments;
    }

    private void LogMatches(Lookup lookup, List<PullPushSubmissionDto> submissions)
    {
        if (lookup.Episode is { } episode)
        {
            logger.LogInformation(
                "Reddit: found {Count} matching submission(s) for '{Title}' S{Season:D2}E{Episode:D2} in {Searches} search(es)",
                submissions.Count, lookup.Media.Title, episode.SeasonNumber, episode.EpisodeNumber,
                MaxSearches - lookup.SearchesLeft);
        }
        else
        {
            logger.LogInformation(
                "Reddit: found {Count} matching submission(s) for movie '{Title}' in {Searches} search(es)",
                submissions.Count, lookup.Media.Title, MaxSearches - lookup.SearchesLeft);
        }
    }

    private static string PostUrl(PullPushSubmissionDto submission)
    {
        return submission.Permalink != null
            ? $"https://reddit.com{submission.Permalink}"
            : $"https://reddit.com/r/{submission.Subreddit}/comments/{submission.Id}/";
    }

    // Arctic Shift can't return is_self, but a link post's selftext is empty anyway.
    private static string? PostBody(PullPushSubmissionDto submission)
    {
        string? selftext = submission.Selftext;
        return string.IsNullOrWhiteSpace(selftext) ||
               selftext.Equals("[deleted]", StringComparison.Ordinal) ||
               selftext.Equals("[removed]", StringComparison.Ordinal)
            ? null
            : selftext;
    }

    // Healthy while at least one enabled archive answers: the provider keeps working with either.
    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken ct = default)
    {
        List<(string Name, string Url)> probes = [];
        if (_options.UsePullPush)
        {
            probes.Add(("PullPush", PullPushSource.HealthUrl));
        }

        if (_options.UseArcticShift)
        {
            probes.Add(("Arctic Shift", ArcticShiftSource.HealthUrl));
        }

        (string Name, string Status)[] results = await Task.WhenAll(probes.Select(async probe =>
        {
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                HttpResponseMessage response = await httpClient.GetAsync(probe.Url, cts.Token);
                return (probe.Name, response.IsSuccessStatusCode ? "OK" : response.StatusCode.ToString());
            }
            catch (Exception ex)
            {
                return (probe.Name, ex.Message);
            }
        }));

        bool anyHealthy = results.Any(r => r.Status == "OK");
        string message = results.All(r => r.Status == "OK")
            ? "OK"
            : string.Join("; ", results.Where(r => r.Status != "OK").Select(r => $"{r.Name}: {r.Status}"));

        return new ServiceHealth(anyHealthy, message, DateTimeOffset.UtcNow);
    }

    public string ConfigSection => "Reddit";

    // Reddit requires no credentials — always operationally configured.
    public bool IsConfigured => true;

    public string NormalizeContent(string content)
    {
        string result = s_excessiveNewlines.Replace(content.Trim(), "\n\n");
        return s_spoilerTag.Replace(result, "<spoiler>$1</spoiler>");
    }

    public IReadOnlyList<ProviderConfigField> GetConfigSchema(
        Func<string, string> envVal,
        Func<string, string, bool> isOverridden)
    {
        return
        [
            new ProviderConfigField("Reddit__MaxThreads",
                UiStrings.ConfigEndpoints_GetConfig_Max_Threads,
                "number",
                "3",
                true,
                _options.MaxThreads.ToString(CultureInfo.InvariantCulture),
                envVal("Reddit__MaxThreads"),
                isOverridden("Reddit", "MaxThreads")),
            new ProviderConfigField("Reddit__MaxComments",
                UiStrings.ConfigEndpoints_GetConfig_Max_Comments,
                "number",
                "250",
                true,
                _options.MaxComments.ToString(CultureInfo.InvariantCulture),
                envVal("Reddit__MaxComments"),
                isOverridden("Reddit", "MaxComments"))
        ];
    }

    // The state of one lookup: what the searches found so far, how many searches are left, and whether
    // any search that mattered failed (so a miss can't be trusted).
    private sealed class Lookup(MediaContext media, EpisodeContext? episode, int searches)
    {
        private readonly Dictionary<string, (PullPushSubmissionDto Post, int Strength)> _found = new();

        public MediaContext Media { get; } = media;
        public EpisodeContext? Episode { get; } = episode;
        public int SearchesLeft { get; set; } = searches;
        public bool Incomplete { get; set; }
        public bool HasMatches => _found.Count > 0;

        public void Collect(IEnumerable<PullPushSubmissionDto> posts, bool requireShowTitle)
        {
            foreach (PullPushSubmissionDto post in posts)
            {
                if (post.Id is null)
                {
                    continue;
                }

                int strength = MatchStrength(post, Media, Episode, requireShowTitle);
                if (strength > 0 &&
                    (!_found.TryGetValue(post.Id, out (PullPushSubmissionDto Post, int Strength) seen) ||
                     seen.Strength < strength))
                {
                    _found[post.Id] = (post, strength);
                }
            }
        }

        // Best match first. Arctic Shift's score and comment counts are often stale (it archives a post the
        // moment it appears), so match strength and "Discussion" in the title or flair outrank them.
        public List<PullPushSubmissionDto> Ranked()
        {
            return _found.Values
                .OrderByDescending(f => f.Strength)
                .ThenByDescending(f => IsDiscussionThread(f.Post))
                .ThenByDescending(f => f.Post.Stickied)
                .ThenByDescending(f => f.Post.NumComments ?? 0)
                .ThenByDescending(f => f.Post.Score ?? 0)
                .Select(f => f.Post)
                .ToList();
        }
    }

    private static bool IsDiscussionThread(PullPushSubmissionDto post)
    {
        return (post.Title ?? "").Contains("discussion", StringComparison.OrdinalIgnoreCase) ||
               (post.LinkFlairText ?? "").Contains("discussion", StringComparison.OrdinalIgnoreCase);
    }

    // How well a post's title names what is being watched: 3 = the episode code (or, for episode 1, a
    // premiere thread; for a movie, its r/movies official discussion), 2 = the episode's title (or the
    // movie's), 0 = no match. requireShowTitle is for general subreddits, where the show must be named too.
    internal static int MatchStrength(PullPushSubmissionDto post, MediaContext media, EpisodeContext? episode,
        bool requireShowTitle)
    {
        string title = post.Title ?? "";
        if (requireShowTitle && !SquashedContains(title, media.Title))
        {
            return 0;
        }

        if (episode is null)
        {
            if (!SquashedContains(title, media.Title))
            {
                return 0;
            }

            // In r/movies only the official discussion thread will do; news and trailers name the film too.
            return title.Contains("official discussion", StringComparison.OrdinalIgnoreCase) ? 3
                : requireShowTitle ? 0 : 2;
        }

        int season = episode.SeasonNumber;
        if (season > 0 && episode.EpisodeNumber > 0)
        {
            if (MatchesEpisode(title, season, episode.EpisodeNumber))
            {
                return 3;
            }

            if (MentionsDifferentEpisode(title, season, episode.EpisodeNumber))
            {
                return 0;
            }

            if (episode.EpisodeNumber == 1 && IsPremiereThread(post, season))
            {
                return 3;
            }

            return MatchesWholePhrase(title, episode.EpisodeTitle) ? 2 : 0;
        }

        // Specials and daily shows (season or episode 0): the episode codes are meaningless.
        if (MentionsDifferentSeason(title, season) && season > 0)
        {
            return 0;
        }

        string? qualifier = IThoughtProvider.GetTextSearchQualifier(media);
        return MatchesWholePhrase(title, qualifier) || MatchesWholePhrase(title, episode.EpisodeTitle) ? 2 : 0;
    }

    // "Series Premiere Discussion" (flair or title; the typo "Premeire" is real) for S1E1, or a season
    // premiere naming no other season. The window is around the air date, so it's this season's premiere.
    private static bool IsPremiereThread(PullPushSubmissionDto post, int season)
    {
        string text = $"{post.Title} | {post.LinkFlairText}";
        if (!s_premiere.IsMatch(text) || MentionsDifferentSeason(post.Title ?? "", season))
        {
            return false;
        }

        return season == 1 || !text.Contains("series prem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesWholePhrase(string title, string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        // Whole-word match: a generic "Episode 1" title must not pull in "Episode 12" (a spoiler), nor
        // "Pilot" match "Copilot". Lookarounds rather than \b so titles ending in punctuation still work.
        return Regex.IsMatch(title, $"(?<![A-Za-z0-9]){Regex.Escape(phrase)}(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    private static bool SquashedContains(string text, string wanted)
    {
        string squashedWanted = ArcticShiftSubredditResolver.Normalize(wanted);
        return squashedWanted.Length > 0 &&
               ArcticShiftSubredditResolver.Normalize(text).Contains(squashedWanted, StringComparison.Ordinal);
    }

    // True when the title carries an episode code (SxxEyy or NxYY) for some other episode.
    private static bool MentionsDifferentEpisode(string title, int season, int episode)
    {
        foreach (Match m in s_episodeCode.Matches(title))
        {
            string s = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
            string e = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
            if (int.Parse(s, CultureInfo.InvariantCulture) != season ||
                int.Parse(e, CultureInfo.InvariantCulture) != episode)
            {
                return true;
            }
        }

        return MentionsDifferentSeason(title, season);
    }

    private static Thought MapCommentToThought(PullPushCommentDto data, string? postTitle, string? postUrl,
        string? postBody)
    {
        string? parentId = StripRedditPrefix(data.ParentId);

        string? commentUrl = postUrl != null && data.Id != null
            ? $"{postUrl.TrimEnd('/')}/{Uri.EscapeDataString(data.Id)}/"
            : null;

        return new Thought(
            $"reddit:{data.Id}",
            parentId,
            null,
            data.Body ?? "",
            commentUrl,
            [],
            data.Author ?? "Unknown",
            data.Score,
            data.CreatedUtc.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(data.CreatedUtc.Value)
                : DateTimeOffset.UtcNow,
            "Reddit",
            [],
            postTitle,
            postUrl,
            postBody);
    }

    private static string? StripRedditPrefix(string? redditId)
    {
        if (string.IsNullOrEmpty(redditId))
        {
            return null;
        }

        // t3_ prefix = parent is the submission itself → top-level comment
        if (redditId.StartsWith("t3_", StringComparison.Ordinal))
        {
            return null;
        }

        // t1_ prefix = parent is another comment → re-add "reddit:" to match thought IDs
        if (redditId.StartsWith("t1_", StringComparison.Ordinal))
        {
            return string.Concat("reddit:", redditId.AsSpan(3));
        }

        return redditId;
    }

    private static Regex[] GetEpisodePatterns(int season, int episode)
    {
        return s_episodeRegexCache.GetOrAdd((season, episode), key =>
        {
            (int s, int e) = key;
            string sStr = s.ToString(CultureInfo.InvariantCulture);
            string sL = s.ToString("D2", CultureInfo.InvariantCulture);
            string eStr = e.ToString(CultureInfo.InvariantCulture);
            string eL = e.ToString("D2", CultureInfo.InvariantCulture);

            // SxxExx variants with lookbehind/lookahead to reject partial matches like "S01E12" inside "S01E120".
            List<string> patterns = new[] { $"S{sL}E{eL}", $"S{sStr}E{eL}", $"S{sL}E{eStr}", $"S{sStr}E{eStr}" }
                .Select(code => $@"(?<!\d){code}(?!\d)").ToList();

            patterns.Add($@"\b{sStr}X{eL}(?!\d)");
            patterns.Add($@"\b{sStr}X{eStr}(?!\d)");

            patterns.Add($@"\bSEASON\s+{sStr}\b.{{0,30}}\bEPISODE\s+{eStr}\b");

            return patterns.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase)).ToArray();
        });
    }

    // Returns true if the title contains an explicit season reference that does NOT match
    // the given season number. Titles with no season marker are considered a match (return false).
    private static bool MentionsDifferentSeason(string title, int season)
    {
        MatchCollection matches = s_explicitSeasonRef.Matches(title);
        foreach (Match m in matches)
        {
            string raw = m.Groups[1].Success ? m.Groups[1].Value :
                         m.Groups[2].Success ? m.Groups[2].Value :
                         m.Groups[3].Value;

            if (int.TryParse(raw, out int referencedSeason) && referencedSeason != season)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesEpisode(string title, int season, int episode)
    {
        Regex[] patterns = GetEpisodePatterns(season, episode);
        return patterns.Any(regex => regex.IsMatch(title));
    }

    private static bool IsDeletedOrRemoved(Thought thought)
    {
        return thought.Content.Contains("[deleted]", StringComparison.Ordinal) ||
               thought.Content.Contains("[removed]", StringComparison.Ordinal);
    }
}

// PullPush sometimes returns created_utc as a float (e.g. 1234567890.0) rather than an integer.
// AllowReadingFromString handles string-encoded numbers but not float-encoded integers, so we
// need a custom converter that accepts integer, float, and string tokens.
internal sealed class UnixTimestampConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out long l))
                {
                    return l;
                }

                if (reader.TryGetDouble(out double d))
                {
                    return (long)d;
                }

                return null;
            case JsonTokenType.String:
                string? s = reader.GetString();
                if (s == null)
                {
                    return null;
                }

                if (long.TryParse(s, out long ls))
                {
                    return ls;
                }

                if (double.TryParse(s, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double ds))
                {
                    return (long)ds;
                }

                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

internal sealed record PullPushCommentDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("created_utc")]
    [property: JsonConverter(typeof(UnixTimestampConverter))]
    long? CreatedUtc,
    [property: JsonPropertyName("parent_id")]
    string? ParentId);

internal sealed record PullPushSubmissionDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("permalink")]
    string? Permalink,
    [property: JsonPropertyName("subreddit")]
    string? Subreddit,
    [property: JsonPropertyName("created_utc")]
    [property: JsonConverter(typeof(UnixTimestampConverter))]
    long? CreatedUtc,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("is_self")]
    bool IsSelf,
    [property: JsonPropertyName("selftext")]
    string? Selftext,
    [property: JsonPropertyName("stickied")]
    bool Stickied = false,
    [property: JsonPropertyName("num_comments")]
    int? NumComments = null,
    [property: JsonPropertyName("link_flair_text")]
    string? LinkFlairText = null);

internal sealed record PullPushSubmissionsResponseDto(
    [property: JsonPropertyName("data")] PullPushSubmissionDto[]? Data);

internal sealed record PullPushCommentsResponseDto(
    [property: JsonPropertyName("data")] PullPushCommentDto[]? Data);
