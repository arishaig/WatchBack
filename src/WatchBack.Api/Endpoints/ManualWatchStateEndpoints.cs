using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Resources;

namespace WatchBack.Api.Endpoints;

public static class ManualWatchStateEndpoints
{
    public static void MapManualWatchStateEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/watchstate/manual")
            .WithTags("Watch State");

        group.MapPost("/", SetManualWatchState)
            .WithName("SetManualWatchState")
            .WithSummary("Set the manual watch state")
            .WithDescription(
                "Sets a movie or episode as the current manual watch state. Pass external IDs when available to improve thought provider search accuracy.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapDelete("/", ClearManualWatchState)
            .WithName("ClearManualWatchState")
            .WithSummary("Clear the manual watch state")
            .WithDescription("Clears the manual watch state, returning the provider to idle.")
            .Produces(StatusCodes.Status204NoContent);
    }

    private static IResult SetManualWatchState(
        SetManualWatchStateRequest request,
        IManualWatchStateProvider provider)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Results.BadRequest(UiStrings.ManualWatchStateEndpoints_SetManualWatchState_Title_is_required_);
        }

        MediaContext context;
        if (request is { EpisodeTitle: { } episodeTitle, SeasonNumber: { } seasonNumber, EpisodeNumber: { } episodeNumber })
        {
            context = new EpisodeContext(
                request.Title.Trim(),
                request.ReleaseDate,
                episodeTitle,
                seasonNumber,
                episodeNumber,
                request.ExternalIds);
        }
        else
        {
            context = new MediaContext(
                request.Title.Trim(),
                request.ReleaseDate,
                request.ExternalIds);
        }

        provider.SetCurrentContext(context);
        return Results.NoContent();
    }

    private static IResult ClearManualWatchState(IManualWatchStateProvider provider)
    {
        provider.SetCurrentContext(null);
        return Results.NoContent();
    }
}

public record SetManualWatchStateRequest(
    string Title,
    DateTimeOffset? ReleaseDate,
    string? EpisodeTitle,
    short? SeasonNumber,
    short? EpisodeNumber,
    Dictionary<string, string>? ExternalIds);
