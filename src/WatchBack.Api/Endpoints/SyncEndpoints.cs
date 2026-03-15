using System.Text;
using WatchBack.Core.Interfaces;
using WatchBack.Api.Models;

namespace WatchBack.Api.Endpoints;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api");

        group.MapGet("/sync", GetSync)
            .WithName("GetSync");

        group.MapGet("/sync/stream", GetSyncStream)
            .WithName("GetSyncStream");
    }

    private static async Task<SyncResponse> GetSync(ISyncService syncService, CancellationToken ct)
    {
        var result = await syncService.SyncAsync(ct);
        return MapSyncResult(result);
    }

    private static async Task GetSyncStream(ISyncService syncService, HttpContext context, CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.WriteAsync("data: {\"message\": \"Stream started\"}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await syncService.SyncAsync(ct);
                var response = MapSyncResult(result);
                var json = System.Text.Json.JsonSerializer.Serialize(response, Serialization.WatchBackJsonContext.Default.SyncResponse);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);

                // Poll interval - 5 seconds
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static SyncResponse MapSyncResult(Core.Models.SyncResult result)
    {
        return new SyncResponse(
            Status: result.Status.ToString(),
            Title: result.Title,
            Metadata: result.Metadata != null ? MapMediaContext(result.Metadata) : null,
            AllThoughts: result.AllThoughts.Select(MapThought).ToList(),
            TimeMachineThoughts: result.TimeMachineThoughts.Select(MapThought).ToList(),
            TimeMachineDays: result.TimeMachineDays,
            SourceResults: result.SourceResults.Select(MapSourceResult).ToList());
    }

    private static MediaContextResponse MapMediaContext(Core.Models.MediaContext context)
    {
        if (context is Core.Models.EpisodeContext episode)
        {
            return new MediaContextResponse(
                Title: episode.Title,
                ReleaseDate: episode.ReleaseDate?.DateTime,
                EpisodeTitle: episode.EpisodeTitle,
                SeasonNumber: episode.SeasonNumber,
                EpisodeNumber: episode.EpisodeNumber);
        }

        return new MediaContextResponse(
            Title: context.Title,
            ReleaseDate: context.ReleaseDate?.DateTime,
            EpisodeTitle: null,
            SeasonNumber: null,
            EpisodeNumber: null);
    }

    private static ThoughtResponse MapThought(Core.Models.Thought thought)
    {
        return new ThoughtResponse(
            Id: thought.Id,
            ParentId: thought.ParentId,
            Title: thought.Title,
            Content: thought.Content,
            Url: thought.Url,
            Images: thought.Images.Select(i => new ThoughtImageResponse(i.Url, i.Alt)).ToList(),
            Author: thought.Author,
            Score: thought.Score,
            CreatedAt: thought.CreatedAt.DateTime,
            Source: thought.Source,
            Replies: thought.Replies.Select(MapThought).ToList(),
            PostTitle: thought.PostTitle,
            PostUrl: thought.PostUrl,
            PostBody: thought.PostBody);
    }

    private static SourceResultResponse MapSourceResult(Core.Models.ThoughtResult result)
    {
        return new SourceResultResponse(
            Source: result.Source,
            PostTitle: result.PostTitle,
            PostUrl: result.PostUrl,
            ImageUrl: result.ImageUrl,
            Thoughts: result.Thoughts?.Select(MapThought).ToList() ?? [],
            NextPageToken: result.NextPageToken);
    }
}
