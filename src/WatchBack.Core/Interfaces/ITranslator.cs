namespace WatchBack.Core.Interfaces;

/// <summary>The outcome of translating one piece of text.</summary>
/// <param name="Text">The translated text, or the original text when it was already in the target language</param>
/// <param name="DetectedLanguage">Two-letter code of the source language, when known</param>
/// <param name="WasTranslated">False when the text was already in the target language or the translator failed</param>
/// <param name="IsTransientFailure">True when the translator was unreachable or timed out; the caller must not remember the miss</param>
public record TranslationResult(
    string Text,
    string? DetectedLanguage,
    bool WasTranslated,
    bool IsTransientFailure = false);

/// <summary>Translates user-generated text into a target language.</summary>
public interface ITranslator
{
    /// <summary>
    ///     Translates <paramref name="text" /> into <paramref name="targetLanguage" /> (a two-letter code). Never throws
    ///     for upstream failures: it returns the original text with <c>IsTransientFailure = true</c> instead.
    /// </summary>
    Task<TranslationResult> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default);
}
