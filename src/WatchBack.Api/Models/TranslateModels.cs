namespace WatchBack.Api.Models;

/// <summary>Text the user asked to have translated into the configured target language.</summary>
/// <param name="Text">The thought's original text</param>
public record TranslateRequest(string Text);

/// <summary>The outcome of a manual translation.</summary>
/// <param name="Text">The translated text, or the original when it was already in the target language</param>
/// <param name="TranslatedFrom">Two-letter source language when the text was translated; otherwise null</param>
public record TranslateResponse(string Text, string? TranslatedFrom);
