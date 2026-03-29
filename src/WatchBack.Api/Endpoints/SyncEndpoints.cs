using System.Threading.Channels;

using Microsoft.AspNetCore.Mvc;

using WatchBack.Api.Logging;
using WatchBack.Api.Models;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Api.Endpoints;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .WithTags("Sync");

        group.MapGet("/sync", GetSync)
            .WithName("GetSync")
            .WithSummary("Get current sync status")
            .WithDescription("Retrieves the current media context, all associated thoughts from providers, and filtered time-machine thoughts")
            .Produces<SyncResponse>(StatusCodes.Status200OK);

        group.MapPost("/sync/trigger", (SyncTrigger trigger) =>
            {
                trigger.Signal();
                return Results.NoContent();
            })
            .WithName("TriggerSync")
            .WithSummary("Trigger an immediate sync")
            .WithDescription("Wakes the SSE polling loop so it syncs immediately instead of waiting up to 5 seconds")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/sync/stream", GetSyncStream)
            .WithName("GetSyncStream")
            .WithSummary("Stream sync status updates")
            .WithDescription("Server-sent events stream that polls sync status every 5 seconds and sends updates to the client")
            .Produces(StatusCodes.Status200OK);
    }

    private static Dictionary<string, BrandData?> BuildBrandLookup(IEnumerable<IThoughtProvider> providers) =>
        providers.ToDictionary(
            p => p.Metadata.Name,
            p => p.Metadata.BrandData,
            StringComparer.OrdinalIgnoreCase);

    private static async Task<SyncResponse> GetSync(
        ISyncService syncService,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        CancellationToken ct)
    {
        var result = await syncService.SyncAsync(null, ct);
        return MapSyncResult(result, BuildBrandLookup(thoughtProviders));
    }

    private static async Task GetSyncStream(
        ISyncService syncService,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        SyncHistoryStore syncHistory,
        SyncTrigger syncTrigger,
        HttpContext context,
        CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var providerList = thoughtProviders.ToList();
        var totalWeight = providerList.Sum(p => p.ExpectedWeight);

        // Pre-build provider metadata for the segmented bar: name → (color, totalWeight)
        var providerMeta = providerList.ToDictionary(
            p => p.Metadata.Name,
            p => (Color: p.Metadata.BrandData?.Color ?? "var(--wb-accent)", Total: p.ExpectedWeight));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Channel lets providers report ticks from any thread without blocking
                var channel = Channel.CreateUnbounded<SyncProgressTick>(
                    new UnboundedChannelOptions { SingleReader = true });
                var progress = new Progress<SyncProgressTick>(
                    tick => channel.Writer.TryWrite(tick));

                // Emit 0% so the bar appears immediately
                await context.Response.WriteAsync(
                    BuildProgressEvent(0, totalWeight, []), ct);
                await context.Response.Body.FlushAsync(ct);

                // Start sync; complete the channel when it finishes so ReadAllAsync terminates
                var syncStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var syncTask = syncService.SyncAsync(progress, ct);
                _ = syncTask.ContinueWith(
                    t => channel.Writer.Complete(t.IsFaulted ? t.Exception : null),
                    TaskScheduler.Default);

                // Drain progress ticks and forward them to the SSE stream
                var completed = 0;
                var providerCompleted = new Dictionary<string, int>();
                await foreach (var tick in channel.Reader.ReadAllAsync(ct))
                {
                    completed += tick.Weight;
                    providerCompleted[tick.Provider] = providerCompleted.GetValueOrDefault(tick.Provider) + tick.Weight;

                    var segments = providerMeta
                        .OrderBy(kv => kv.Value.Total)
                        .Select(kv => new ProgressSegment(
                            kv.Key, kv.Value.Color,
                            Math.Min(providerCompleted.GetValueOrDefault(kv.Key), kv.Value.Total),
                            kv.Value.Total)).ToArray();

                    await context.Response.WriteAsync(
                        BuildProgressEvent(Math.Min(completed, totalWeight), totalWeight, segments), ct);
                    await context.Response.Body.FlushAsync(ct);
                }

                // Force 100% in case providers reported fewer ticks than expected (e.g. cache hits)
                if (completed < totalWeight)
                {
                    var fullSegments = providerMeta
                        .OrderBy(kv => kv.Value.Total)
                        .Select(kv => new ProgressSegment(
                            kv.Key, kv.Value.Color, kv.Value.Total, kv.Value.Total)).ToArray();
                    await context.Response.WriteAsync(
                        BuildProgressEvent(totalWeight, totalWeight, fullSegments), ct);
                    await context.Response.Body.FlushAsync(ct);
                }

                var result = await syncTask;
                syncStopwatch.Stop();

                // Record sync history for the diagnostics panel and persist to database
                var sourceRecords = result.SourceResults
                    .Select(sr => new ProviderSyncRecord(sr.Source, sr.Thoughts?.Count ?? 0))
                    .ToList();
                syncHistory.Record(
                    new SyncSnapshot(DateTimeOffset.UtcNow, result.Status.ToString(), result.Title, sourceRecords),
                    syncStopwatch.ElapsedMilliseconds);

                var response = MapSyncResult(result, BuildBrandLookup(providerList));
                var json = System.Text.Json.JsonSerializer.Serialize(response, Serialization.WatchBackJsonContext.Default.SyncResponse);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);

                // Wait up to 5 s, but wake immediately if the user hits the Sync button
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delayCts.CancelAfter(5000);
                try { await syncTrigger.WaitAsync(delayCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* normal timeout */ }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Provider fault — pause before retrying the sync loop
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    internal sealed record ProgressSegment(string Provider, string Color, int Completed, int Total);
    internal sealed record ProgressEvent(int Completed, int Total, ProgressSegment[]? Providers);

    private static string BuildProgressEvent(int completed, int total, ProgressSegment[] segments)
    {
        var evt = new ProgressEvent(completed, total, segments.Length > 0 ? segments : null);
        var json = System.Text.Json.JsonSerializer.Serialize(evt, Serialization.WatchBackJsonContext.Default.ProgressEvent);
        return $"data: {json}\n\n";
    }

    private static SyncResponse MapSyncResult(
        Core.Models.SyncResult result,
        IReadOnlyDictionary<string, Core.Models.BrandData?> brandBySource)
    {
        return new SyncResponse(
            Status: result.Status.ToString(),
            Title: result.Title,
            Metadata: result.Metadata != null ? MapMediaContext(result.Metadata) : null,
            AllThoughts: result.AllThoughts.Select(t => MapThought(t, brandBySource)).ToList(),
            TimeMachineThoughts: result.TimeMachineThoughts.Select(t => MapThought(t, brandBySource)).ToList(),
            TimeMachineDays: result.TimeMachineDays,
            SourceResults: result.SourceResults.Select(r => MapSourceResult(r, brandBySource)).ToList(),
            WatchProvider: result.WatchProvider,
            SuppressedProvider: result.SuppressedProvider,
            SuppressedTitle: result.SuppressedTitle,
            Ratings: result.Ratings?.Select(r => new MediaRatingResponse(r.Source, r.Value, r.BrandData?.LogoSvg, r.BrandData?.Color)).ToList(),
            RatingsProvider: result.RatingsProvider);
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

    private static ThoughtResponse MapThought(
        Core.Models.Thought thought,
        IReadOnlyDictionary<string, Core.Models.BrandData?> brandBySource)
    {
        var brand = brandBySource.GetValueOrDefault(thought.Source);
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
            Replies: thought.Replies.Select(r => MapThought(r, brandBySource)).ToList(),
            PostTitle: thought.PostTitle,
            PostUrl: thought.PostUrl,
            PostBody: thought.PostBody,
            BrandColor: brand?.Color,
            BrandLogoSvg: brand?.LogoSvg);
    }

    private static SourceResultResponse MapSourceResult(
        Core.Models.ThoughtResult result,
        IReadOnlyDictionary<string, Core.Models.BrandData?> brandBySource)
    {
        var brand = brandBySource.GetValueOrDefault(result.Source);
        return new SourceResultResponse(
            Source: result.Source,
            PostTitle: result.PostTitle,
            PostUrl: result.PostUrl,
            ImageUrl: result.ImageUrl,
            Thoughts: result.Thoughts?.Select(r => MapThought(r, brandBySource)).ToList() ?? [],
            NextPageToken: result.NextPageToken,
            BrandColor: brand?.Color,
            BrandLogoSvg: brand?.LogoSvg);
    }
}
