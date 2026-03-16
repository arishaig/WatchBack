using WatchBack.Core.Models;
using WatchBack.Infrastructure.WatchStateProviders;

namespace WatchBack.Api.Endpoints;

public static class ManualWatchStateEndpoints
{
    public static void MapManualWatchStateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/watchstate/manual")
            .WithTags("Watch State");

        group.MapPost("/", SetManualWatchState)
            .WithName("SetManualWatchState")
            .WithSummary("Set the manual watch state")
            .WithDescription("Sets a movie or episode as the current manual watch state. Pass external IDs when available to improve thought provider search accuracy.")
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
        ManualWatchStateProvider provider)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return Results.BadRequest("Title is required.");

        MediaContext context;
        if (request.EpisodeTitle != null && request.SeasonNumber.HasValue && request.EpisodeNumber.HasValue)
        {
            context = new EpisodeContext(
                Title: request.Title.Trim(),
                ReleaseDate: request.ReleaseDate,
                EpisodeTitle: request.EpisodeTitle,
                SeasonNumber: request.SeasonNumber.Value,
                EpisodeNumber: request.EpisodeNumber.Value,
                ExternalIds: request.ExternalIds);
        }
        else
        {
            context = new MediaContext(
                Title: request.Title.Trim(),
                ReleaseDate: request.ReleaseDate,
                ExternalIds: request.ExternalIds);
        }

        provider.SetCurrentContext(context);
        return Results.NoContent();
    }

    private static IResult ClearManualWatchState(ManualWatchStateProvider provider)
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
