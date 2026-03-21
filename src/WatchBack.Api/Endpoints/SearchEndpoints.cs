using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Resources;

namespace WatchBack.Api.Endpoints;

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search")
            .WithTags("Search");

        group.MapGet("/", Search)
            .WithName("SearchMedia")
            .WithSummary("Search for movies and TV shows")
            .WithDescription("Searches the configured media search provider. Include S01E05 or 'season 1 episode 5' in the query for direct episode resolution.")
            .Produces<IReadOnlyList<MediaSearchResult>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/show/{imdbId}/seasons", GetSeasons)
            .WithName("GetSeasons")
            .WithSummary("List seasons for a TV show")
            .Produces<IReadOnlyList<SeasonInfo>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/show/{imdbId}/season/{seasonNumber}/episodes", GetEpisodes)
            .WithName("GetEpisodes")
            .WithSummary("List episodes in a season")
            .Produces<IReadOnlyList<EpisodeInfo>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> Search(
        string? q,
        IEnumerable<IMediaSearchProvider> providers,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(ErrorMessages.SearchEndpoints_Search_Query_parameter__q__is_required_);

        var provider = providers.FirstOrDefault();
        if (provider == null)
            return Results.Problem(
                ErrorMessages.SearchEndpoints_Search_No_media_search_provider_is_configured_,
                statusCode: 503);

        var results = await provider.SearchAsync(q.Trim(), ct);
        return Results.Ok(results);
    }

    private static async Task<IResult> GetSeasons(
        string imdbId,
        IEnumerable<IMediaSearchProvider> providers,
        CancellationToken ct)
    {
        var provider = providers.FirstOrDefault();
        if (provider == null)
            return Results.Problem(ErrorMessages.SearchEndpoints_Search_No_media_search_provider_is_configured_, statusCode: 503);

        var seasons = await provider.GetSeasonsAsync(imdbId, ct);
        return Results.Ok(seasons);
    }

    private static async Task<IResult> GetEpisodes(
        string imdbId,
        int seasonNumber,
        IEnumerable<IMediaSearchProvider> providers,
        CancellationToken ct)
    {
        var provider = providers.FirstOrDefault();
        if (provider == null)
            return Results.Problem(ErrorMessages.SearchEndpoints_Search_No_media_search_provider_is_configured_, statusCode: 503);

        var episodes = await provider.GetEpisodesAsync(imdbId, seasonNumber, ct);
        return Results.Ok(episodes);
    }
}
