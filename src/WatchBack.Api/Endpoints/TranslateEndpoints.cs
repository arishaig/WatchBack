using Microsoft.Extensions.Options;

using WatchBack.Api.Models;
using WatchBack.Core.Interfaces;
using WatchBack.Core.Options;

namespace WatchBack.Api.Endpoints;

public static class TranslateEndpoints
{
    public static void MapTranslateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/translate", Translate)
            .WithTags("Translate")
            .WithName("TranslateText")
            .WithSummary("Translate one thought into the configured target language")
            .WithDescription(
                "For translating a single thought on demand when auto-translate is off. Answers 400 without text or a target language, and 503 when the translator is unreachable.")
            .Produces<TranslateResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> Translate(
        TranslateRequest request,
        IEnumerable<ITranslator> translators,
        IOptionsSnapshot<WatchBackOptions> options,
        CancellationToken ct)
    {
        string target = options.Value.TranslateTargetLanguage;
        ITranslator? translator = TranslatorSelection.Select(translators, options.Value.TranslationProvider);
        if (string.IsNullOrWhiteSpace(request.Text) || string.IsNullOrWhiteSpace(target) || translator is null)
        {
            return Results.BadRequest();
        }

        TranslationResult result = await translator.TranslateAsync(request.Text, target, ct);
        return result.IsTransientFailure
            ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new TranslateResponse(result.Text, result.WasTranslated ? result.DetectedLanguage : null));
    }
}
