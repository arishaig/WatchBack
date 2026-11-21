using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using WatchBack.Api.Models;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;
using WatchBack.Core.Options;
using WatchBack.Core.Services;

namespace WatchBack.Api.Endpoints;

public static class SummaryEndpoints
{
    public static void MapSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/summary", GetSummary)
            .WithTags("Summary")
            .WithName("GetSummary")
            .WithSummary("Summarize the discussion of what is being watched")
            .WithDescription(
                "Writes a short summary of the current thoughts with the configured language model. Answers pending " +
                "while it is still writing (ask again shortly), and hidden without calling the model when the summary mode is hidden.")
            .Produces<SummaryResponse>();
    }

    private static async Task<SummaryResponse> GetSummary(
        ISyncService syncService,
        ISummarizer summarizer,
        IThoughtSummaryService summaries,
        IOptionsSnapshot<WatchBackOptions> options,
        CancellationToken ct,
        [FromQuery] string? userId = null,
        [FromQuery] string? clientId = null,
        [FromQuery] bool timeMachine = false)
    {
        WatchBackOptions o = options.Value;
        if (SummaryModes.Resolve(o.SummaryMode, summarizer.IsConfigured) == SummaryModes.Hidden)
        {
            return new SummaryResponse("hidden", null, 0);
        }

        SyncResult sync = await syncService.SyncAsync(null, userId, WatchClientId.Normalize(clientId), ct);
        if (sync.Status != SyncStatus.Watching)
        {
            return new SummaryResponse("empty", null, 0);
        }

        string language = string.IsNullOrWhiteSpace(o.TranslateTargetLanguage) ? "en" : o.TranslateTargetLanguage;
        ThoughtSummary summary = await summaries.SummarizeAsync(sync.Title ?? "",
            timeMachine ? sync.TimeMachineThoughts : sync.AllThoughts, language, ct);
        return new SummaryResponse(summary.Status.ToString().ToLowerInvariant(), summary.Text, summary.CommentCount);
    }
}
