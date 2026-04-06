using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Api.Endpoints;

public static class SubredditMappingEndpoints
{
    public static void MapSubredditMappingEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/subreddit-mappings")
            .WithTags("Subreddit Mappings");

        group.MapGet("/", GetAll)
            .WithName("GetSubredditMappings")
            .WithSummary("Get all subreddit mapping sources and entries")
            .Produces<SubredditMappingSourceDto[]>();

        group.MapPost("/local", AddLocal)
            .WithName("AddLocalSubredditMapping")
            .WithSummary("Add an entry to the local (user-managed) source")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapDelete("/local/{title}", DeleteLocal)
            .WithName("DeleteLocalSubredditMapping")
            .WithSummary("Delete an entry from the local source by title")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/import", Import)
            .WithName("ImportSubredditMappings")
            .WithSummary("Import a JSON mapping file as a new source")
            .Produces<SubredditMappingSourceDto>()
            .Produces(StatusCodes.Status400BadRequest);

        group.MapDelete("/sources/{id}", DeleteSource)
            .WithName("DeleteSubredditMappingSource")
            .WithSummary("Delete an imported source and its entries")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/sources/{id}/export", ExportSource)
            .WithName("ExportSubredditMappingSource")
            .WithSummary("Export a source as a JSON mapping file")
            .Produces<string>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/sources/{id}/promote/{index}", PromoteEntry)
            .WithName("PromoteSubredditMappingEntry")
            .WithSummary("Copy an entry from any source into the local source")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static IResult GetAll(ISubredditMappingService service)
    {
        IReadOnlyList<SubredditMappingSource> sources = service.GetSources();
        return Results.Ok(sources.Select(ToDto).ToArray());
    }

    private static async Task<IResult> AddLocal(
        AddLocalMappingRequest request,
        ISubredditMappingService service)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Results.BadRequest("Title is required.");
        }

        if (request.Subreddits is not { Count: > 0 })
        {
            return Results.BadRequest("At least one subreddit is required.");
        }

        SubredditMappingEntry entry = new(
            request.Title.Trim(),
            request.ExternalIds is { Count: > 0 } ? request.ExternalIds : null,
            request.Subreddits.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList());

        await service.AddLocalEntryAsync(entry);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteLocal(
        string title,
        ISubredditMappingService service)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Results.BadRequest("Title is required.");
        }

        await service.DeleteLocalEntryAsync(title);
        return Results.NoContent();
    }

    private static async Task<IResult> Import(
        ImportMappingRequest request,
        ISubredditMappingService service)
    {
        if (string.IsNullOrWhiteSpace(request.Json))
        {
            return Results.BadRequest("JSON body is required.");
        }

        string name = string.IsNullOrWhiteSpace(request.Name) ? "Imported" : request.Name.Trim();

        try
        {
            SubredditMappingSource source = await service.ImportAsync(name, request.Json);
            return Results.Ok(ToDto(source));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> DeleteSource(
        string id,
        ISubredditMappingService service)
    {
        IReadOnlyList<SubredditMappingSource> sources = service.GetSources();
        SubredditMappingSource? source = sources.FirstOrDefault(
            s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            return Results.NotFound();
        }

        if (source.IsBuiltIn)
        {
            return Results.BadRequest("The built-in source cannot be deleted.");
        }

        await service.DeleteSourceAsync(id);
        return Results.NoContent();
    }

    private static IResult ExportSource(string id, ISubredditMappingService service)
    {
        try
        {
            string json = service.ExportSource(id);
            return Results.Content(json, "application/json");
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> PromoteEntry(
        string id,
        int index,
        ISubredditMappingService service)
    {
        IReadOnlyList<SubredditMappingSource> sources = service.GetSources();
        SubredditMappingSource? source = sources.FirstOrDefault(
            s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (source is null || index < 0 || index >= source.Entries.Count)
        {
            return Results.NotFound();
        }

        await service.PromoteEntryAsync(id, index);
        return Results.NoContent();
    }

    private static SubredditMappingSourceDto ToDto(SubredditMappingSource source) =>
        new(source.Id, source.Name, source.IsBuiltIn,
            source.Entries.Select((e, i) => new SubredditMappingEntryDto(
                i, e.Title, e.Subreddits)).ToArray());
}

public sealed record AddLocalMappingRequest(
    string Title,
    IReadOnlyList<string> Subreddits,
    Dictionary<string, string>? ExternalIds = null);


public sealed record ImportMappingRequest(string Json, string? Name = null);

public sealed record SubredditMappingSourceDto(
    string Id,
    string Name,
    bool IsBuiltIn,
    SubredditMappingEntryDto[] Entries);

public sealed record SubredditMappingEntryDto(
    int Index,
    string? Title,
    IReadOnlyList<string> Subreddits);
