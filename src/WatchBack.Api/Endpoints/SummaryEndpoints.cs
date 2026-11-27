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
                "Writes a short summary of the current thoughts with the configured language model; the all-time view also " +
                "compares the early (time machine) reactions with the later ones when there are enough of each. Answers pending " +
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
        // The time-machine view is about the early window alone. The all-time view is about everything, and
        // where there are both early and later comments it also says how the early reactions compare.
        ThoughtSummary summary = timeMachine
            ? await summaries.SummarizeAsync(sync.Title ?? "", sync.TimeMachineThoughts, language, ct: ct)
            : await summaries.SummarizeAsync(sync.Title ?? "", sync.AllThoughts, language, sync.TimeMachineThoughts, ct);
        return new SummaryResponse(summary.Status.ToString().ToLowerInvariant(), summary.Text, summary.CommentCount,
            summary.Compared);
    }
}
