using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using WatchBack.Api.Logging;
using WatchBack.Api.Models;
using WatchBack.Api.Serialization;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;

namespace WatchBack.Api.Endpoints;

public static partial class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api")
            .WithTags("Sync");

        group.MapGet("/sync", GetSync)
            .WithName("GetSync")
            .WithSummary("Get current sync status")
            .WithDescription(
                "Retrieves the current media context, all associated thoughts from providers, and filtered time-machine thoughts")
            .Produces<SyncResponse>();

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
            .WithDescription(
                "Server-sent events stream that polls sync status every 5 seconds and sends updates to the client")
            .Produces(StatusCodes.Status200OK);
    }

    private static HashSet<string> ParseDisabled(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, BrandData?> BuildBrandLookup(IEnumerable<IThoughtProvider> providers)
    {
        return providers.ToDictionary(
            p => p.Metadata.Name,
            p => p.Metadata.BrandData,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<SyncResponse> GetSync(
        ISyncService syncService,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        IOptionsSnapshot<WatchBackOptions> watchback,
        SentimentScorer scorer,
        CancellationToken ct)
    {
        HashSet<string> disabled = ParseDisabled(watchback.Value.DisabledProviders);
        SyncResult result = await syncService.SyncAsync(null, ct);
        return MapSyncResult(result, BuildBrandLookup(thoughtProviders.Where(p => p.ConfigSection is null || !disabled.Contains(p.ConfigSection))), watchback.Value.EnableSentimentAnalysis, scorer);
    }

    private static async Task GetSyncStream(
        ISyncService syncService,
        [FromServices] IEnumerable<IThoughtProvider> thoughtProviders,
        IOptionsSnapshot<WatchBackOptions> watchback,
        SyncHistoryStore syncHistory,
        SyncTrigger syncTrigger,
        SyncGate syncGate,
        SentimentScorer scorer,
        ILoggerFactory loggerFactory,
        HttpContext context,
        CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        HashSet<string> disabled = ParseDisabled(watchback.Value.DisabledProviders);
        List<IThoughtProvider> providerList = thoughtProviders
            .Where(p => p.ConfigSection is null || !disabled.Contains(p.ConfigSection))
            .ToList();
        Dictionary<string, BrandData?> brandLookup = BuildBrandLookup(providerList);
        ILogger logger = loggerFactory.CreateLogger("WatchBack.Api.Endpoints.SyncEndpoints");

        int consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SyncProgressReporter reporter = new(providerList);

                // Channel lets providers report ticks from any thread without blocking
                Channel<SyncProgressTick> channel = Channel.CreateUnbounded<SyncProgressTick>(
                    new UnboundedChannelOptions { SingleReader = true });
                Progress<SyncProgressTick> progress = new(tick => channel.Writer.TryWrite(tick));

                // Emit 0% so the bar appears immediately
                await context.Response.WriteAsync(reporter.BuildInitialEvent(), ct);
                await context.Response.Body.FlushAsync(ct);

                // Start sync (gated so only one runs at a time across all SSE clients);
                // complete the channel when it finishes so ReadAllAsync terminates.
                Stopwatch syncStopwatch = Stopwatch.StartNew();
                Task<SyncResult> syncTask = syncGate.ExecuteAsync(
                    () => syncService.SyncAsync(progress, ct), ct);
                _ = syncTask.ContinueWith(
                    t => channel.Writer.Complete(t.IsFaulted ? t.Exception : null),
                    TaskScheduler.Default);

                // Drain progress ticks and forward them to the SSE stream
                await foreach (SyncProgressTick tick in channel.Reader.ReadAllAsync(ct))
                {
                    await context.Response.WriteAsync(reporter.OnTick(tick), ct);
                    await context.Response.Body.FlushAsync(ct);
                }

                // Force 100% in case providers reported fewer ticks than expected (e.g. cache hits)
                if (reporter.BuildFinalEventIfIncomplete() is { } finalEvent)
                {
                    await context.Response.WriteAsync(finalEvent, ct);
                    await context.Response.Body.FlushAsync(ct);
                }

                SyncResult result = await syncTask;
                syncStopwatch.Stop();

                // Record sync history for the diagnostics panel and persist to database
                List<ProviderSyncRecord> sourceRecords = result.SourceResults
                    .Select(sr => new ProviderSyncRecord(sr.Source, sr.Thoughts?.Count ?? 0))
                    .ToList();
                syncHistory.Record(
                    new SyncSnapshot(DateTimeOffset.UtcNow, result.Status.ToString(), result.Title, sourceRecords),
                    syncStopwatch.ElapsedMilliseconds);

                SyncResponse response = MapSyncResult(result, brandLookup, watchback.Value.EnableSentimentAnalysis, scorer);
                string json = JsonSerializer.Serialize(response, WatchBackJsonContext.Default.SyncResponse);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);

                consecutiveErrors = 0;

                // Wait up to 5 s, but wake immediately if the user hits the Sync button
                using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delayCts.CancelAfter(5000);
                try { await syncTrigger.WaitAsync(delayCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    /* normal timeout */
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Provider fault — log and back off exponentially
                consecutiveErrors++;
                LogSyncStreamIterationFailed(logger, consecutiveErrors, ex);
                try { await Task.Delay(SyncProgressReporter.ComputeErrorBackoffMs(consecutiveErrors), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static SyncResponse MapSyncResult(
        SyncResult result,
        IReadOnlyDictionary<string, BrandData?> brandBySource,
        bool computeSentiment,
        SentimentScorer scorer)
    {
        return new SyncResponse(
            result.Status.ToString(),
            result.Title,
            result.Metadata != null ? MapMediaContext(result.Metadata) : null,
            result.AllThoughts.Select(t => MapThought(t, brandBySource, computeSentiment, scorer)).ToList(),
            result.TimeMachineThoughts.Select(t => MapThought(t, brandBySource, computeSentiment, scorer)).ToList(),
            result.TimeMachineDays,
            result.SourceResults.Select(r => MapSourceResult(r, brandBySource, computeSentiment, scorer)).ToList(),
            result.WatchProvider,
            result.SuppressedProvider,
            result.SuppressedTitle,
            result.Ratings?.Select(r =>
                new MediaRatingResponse(r.Source, r.Value, r.BrandData?.LogoSvg, r.BrandData?.Color)).ToList(),
            result.RatingsProvider);
    }

    private static MediaContextResponse MapMediaContext(MediaContext context)
    {
        if (context is EpisodeContext episode)
        {
            return new MediaContextResponse(
                episode.Title,
                episode.ReleaseDate?.DateTime,
                episode.EpisodeTitle,
                episode.SeasonNumber,
                episode.EpisodeNumber);
        }

        return new MediaContextResponse(
            context.Title,
            context.ReleaseDate?.DateTime,
            null,
            null,
            null);
    }

    private static ThoughtResponse MapThought(
        Thought thought,
        IReadOnlyDictionary<string, BrandData?> brandBySource,
        bool computeSentiment,
        SentimentScorer scorer)
    {
        BrandData? brand = brandBySource.GetValueOrDefault(thought.Source);
        float? sentiment = computeSentiment ? scorer.Score(thought.Content) : null;
        return new ThoughtResponse(
            thought.Id,
            thought.ParentId,
            thought.Title,
            thought.Content,
            thought.Url,
            thought.Images.Select(i => new ThoughtImageResponse(i.Url, i.Alt)).ToList(),
            thought.Author,
            thought.Score,
            thought.CreatedAt.DateTime,
            thought.Source,
            thought.Replies.Select(r => MapThought(r, brandBySource, computeSentiment, scorer)).ToList(),
            thought.PostTitle,
            thought.PostUrl,
            thought.PostBody,
            brand?.Color,
            brand?.LogoSvg,
            Sentiment: sentiment);
    }

    private static SourceResultResponse MapSourceResult(
        ThoughtResult result,
        IReadOnlyDictionary<string, BrandData?> brandBySource,
        bool computeSentiment,
        SentimentScorer scorer)
    {
        BrandData? brand = brandBySource.GetValueOrDefault(result.Source);
        return new SourceResultResponse(
            result.Source,
            result.PostTitle,
            result.PostUrl,
            result.ImageUrl,
            result.Thoughts?.Select(r => MapThought(r, brandBySource, computeSentiment, scorer)).ToList() ?? [],
            result.NextPageToken,
            brand?.Color,
            brand?.LogoSvg);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Sync stream iteration failed (consecutive errors: {ConsecutiveErrors}); backing off")]
    private static partial void LogSyncStreamIterationFailed(ILogger logger, int consecutiveErrors, Exception ex);
}
