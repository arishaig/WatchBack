using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using WatchBack.Core.Models;
using WatchBack.Infrastructure.Services;

namespace WatchBack.Infrastructure.WatchStateProviders;

// Live TV: a channel is followed by what its guide says is airing, and the guide's names are matched to
// the library (where Tunarr's programs come from) and then to the media search provider, for the ids
// and air dates guides leave out.
public sealed partial class JellyfinWatchStateProvider
{
    private static readonly TimeSpan s_libraryLookupLifetime = TimeSpan.FromHours(6);

    // Guides with no year for a program give 1970 (the Unix epoch), which would mismatch every title.
    private const int EarliestPlausibleYear = 1900;
    private const int EpochYear = 1970;

    // Movie channels' guides often call a film a "Series" with no year: a long program with no episode
    // title that isn't news, sports or music may be one, and is looked up as one before being taken for a
    // live broadcast.
    private static readonly TimeSpan s_filmLength = TimeSpan.FromMinutes(80);

    private async Task<MediaContext> IdentifyLiveAsync(JellyfinItemDto channel, string? userId, CancellationToken ct)
    {
        MediaContext media = FromLiveChannel(channel);
        int? year = YearHint(channel.CurrentProgram?.ProductionYear);
        if (media is LiveBroadcastContext live)
        {
            if (!MightBeAFilm(channel.CurrentProgram, live))
            {
                return live;
            }

            MediaContext film = await IdentifyByNameAsync(
                new MediaContext(live.Title, null) { LiveChannel = live.LiveChannel }, year, userId, ct);
            return film.ExternalIds is { Count: > 0 } ? film : live;
        }

        return media.ExternalIds is { Count: > 0 } ? media : await IdentifyByNameAsync(media, year, userId, ct);
    }

    internal static bool MightBeAFilm(JellyfinProgramDto? program, LiveBroadcastContext live) =>
        program is { IsNews: not true, IsSports: not true, StartDate: { } start, EndDate: { } end } &&
        !live.IsMusic && string.IsNullOrWhiteSpace(program.EpisodeTitle) && end - start >= s_filmLength;

    // The library first (free, exact, and where Tunarr's programs come from), then the media search provider.
    private async Task<MediaContext> IdentifyByNameAsync(MediaContext media, int? year, string? userId,
        CancellationToken ct)
    {
        try
        {
            media = await FindInLibraryAsync(media, year, userId, ct) ?? media;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Jellyfin: library lookup failed for '{Title}'", media.Title);
        }

        if (media.ExternalIds is { Count: > 0 } || identifier is null)
        {
            return media;
        }

        // The identifier promises not to throw, but a failure here must never turn playback into "Idle".
        try
        {
            return await identifier.IdentifyAsync(media, year, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Identifying '{Title}' failed", media.Title);
            return media;
        }
    }

    internal static int? YearHint(int? year) =>
        year is > EarliestPlausibleYear and not EpochYear ? year : null;

    // The guide entry decides what this is: a movie; an episode, by its numbers or (when the guide has
    // none) its title; or else a live broadcast (news, sports, a program with nothing more specific).
    internal static MediaContext FromLiveChannel(JellyfinItemDto channel)
    {
        string channelName = channel.Name ?? "Unknown";
        JellyfinProgramDto? program = channel.CurrentProgram;
        if (program is null || string.IsNullOrWhiteSpace(program.Name))
        {
            return new LiveBroadcastContext(channelName, channelName) { LiveChannel = channelName };
        }

        DateTimeOffset? premiere = program.PremiereDate is { } date ? new DateTimeOffset(date, TimeSpan.Zero) : null;
        if (program.IsMovie == true)
        {
            return new MediaContext(program.Name, premiere, ExternalIdsOf(program.ProviderIds))
            {
                LiveChannel = channelName
            };
        }

        bool numbered = program is { ParentIndexNumber: > 0, IndexNumber: > 0 };
        bool titled = !string.IsNullOrWhiteSpace(program.EpisodeTitle);
        if (numbered || (titled && program.IsNews != true && program.IsSports != true))
        {
            return new EpisodeContext(
                program.Name,
                premiere,
                titled ? program.EpisodeTitle! : program.Name,
                (short)(numbered ? program.ParentIndexNumber!.Value : 0),
                (short)(numbered ? program.IndexNumber!.Value : 0),
                ExternalIdsOf(program.ProviderIds))
            { LiveChannel = channelName };
        }

        return new LiveBroadcastContext(
            program.Name,
            program.ChannelName ?? channelName,
            program.StartDate is { } start ? new DateTimeOffset(start, TimeSpan.Zero) : null,
            program.EndDate is { } end ? new DateTimeOffset(end, TimeSpan.Zero) : null,
            program.EpisodeTitle,
            IsMusic(program))
        { LiveChannel = program.ChannelName ?? channelName };
    }

    private static bool IsMusic(JellyfinProgramDto program) =>
        program.IsNews != true && program.IsSports != true &&
        program.Genres?.Any(g => g.Equals("Music", StringComparison.OrdinalIgnoreCase)) == true;

    // Tunarr airs library items, so the library knows them exactly: ids, air date, and the episode's
    // numbers when the guide gave only its title. Null when there's no exact match by name.
    private async Task<MediaContext?> FindInLibraryAsync(MediaContext media, int? year, string? userId,
        CancellationToken ct)
    {
        EpisodeContext? episode = media as EpisodeContext;
        string key = episode is null
            ? $"jellyfin:library:movie:{media.Title}|{year}"
            : $"jellyfin:library:episode:{media.Title}|{episode.SeasonNumber}|{episode.EpisodeNumber}|{episode.EpisodeTitle}";
        if (cache.TryGetValue(key, out LibraryMatch? remembered))
        {
            return remembered is null ? null : Apply(media, remembered);
        }

        string user = string.IsNullOrEmpty(userId) ? "" : $"&userId={Uri.EscapeDataString(userId)}";
        JellyfinLibraryItemDto? found;
        if (episode is null)
        {
            JellyfinLibraryItemDto[] movies = await GetItemsAsync(
                $"/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm={Uri.EscapeDataString(media.Title)}&Fields=ProviderIds,PremiereDate,ProductionYear&Limit=20{user}",
                ct);
            found = movies.FirstOrDefault(m => TitleMatch.Same(m.Name, media.Title) &&
                                               (year is null || m.ProductionYear is null ||
                                                Math.Abs(m.ProductionYear.Value - year.Value) <= 1));
        }
        else
        {
            JellyfinLibraryItemDto[] series = await GetItemsAsync(
                $"/Items?Recursive=true&IncludeItemTypes=Series&SearchTerm={Uri.EscapeDataString(media.Title)}&Fields=ProviderIds&Limit=20{user}",
                ct);
            JellyfinLibraryItemDto? show = series.FirstOrDefault(s => TitleMatch.Same(s.Name, media.Title));
            found = null;
            if (show?.Id is not null)
            {
                string season = episode.SeasonNumber > 0 ? $"&season={episode.SeasonNumber}" : "";
                JellyfinLibraryItemDto[] episodes = await GetItemsAsync(
                    $"/Shows/{Uri.EscapeDataString(show.Id)}/Episodes?Fields=ProviderIds,PremiereDate{season}{user}", ct);
                found = episode.SeasonNumber > 0 && episode.EpisodeNumber > 0
                    ? episodes.FirstOrDefault(e =>
                        e.ParentIndexNumber == episode.SeasonNumber && e.IndexNumber == episode.EpisodeNumber)
                    : episodes.FirstOrDefault(e => TitleMatch.Same(e.Name, episode.EpisodeTitle));
                // An episode without ids of its own is still known by its show's.
                if (found is not null && ExternalIdsOf(found.ProviderIds) is null)
                {
                    found = found with { ProviderIds = show.ProviderIds };
                }
            }
        }

        LibraryMatch? match = found is null || ExternalIdsOf(found.ProviderIds) is not { } ids
            ? null
            : new LibraryMatch(ids,
                found.PremiereDate is { } p ? new DateTimeOffset(p, TimeSpan.Zero) : null,
                found.ParentIndexNumber, found.IndexNumber);
        cache.Set(key, match, s_libraryLookupLifetime);
        return match is null ? null : Apply(media, match);
    }

    private static MediaContext Apply(MediaContext media, LibraryMatch match)
    {
        if (media is EpisodeContext episode)
        {
            return episode with
            {
                ExternalIds = match.Ids,
                ReleaseDate = match.Aired ?? episode.ReleaseDate,
                SeasonNumber = (short)(match.Season ?? episode.SeasonNumber),
                EpisodeNumber = (short)(match.Episode ?? episode.EpisodeNumber)
            };
        }

        return media with { ExternalIds = match.Ids, ReleaseDate = match.Aired ?? media.ReleaseDate };
    }

    private async Task<JellyfinLibraryItemDto[]> GetItemsAsync(string pathAndQuery, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{_options.BaseUrl}{pathAndQuery}");
        request.Headers.Add("X-Emby-Token", _options.ApiKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(content, JellyfinJsonContext.Default.JellyfinItemsResponseDto)?.Items ?? [];
    }

    private sealed record LibraryMatch(
        IReadOnlyDictionary<string, string> Ids,
        DateTimeOffset? Aired,
        int? Season,
        int? Episode);
}
