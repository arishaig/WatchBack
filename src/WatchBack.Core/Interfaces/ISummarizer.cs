namespace WatchBack.Core.Interfaces;

/// <summary>The outcome of summarizing a set of comments.</summary>
/// <param name="Text">The summary, or null when none could be produced</param>
/// <param name="IsTransientFailure">True when the summarizer was unreachable or timed out; the miss must not be remembered</param>
public record SummaryResult(string? Text, bool IsTransientFailure = false);

/// <summary>Summarizes a discussion with a language model.</summary>
public interface ISummarizer
{
    /// <summary>Whether a language model is set up (a server and a model are saved).</summary>
    bool IsConfigured { get; }

    /// <summary>
    ///     Summarizes <paramref name="comments" /> about <paramref name="title" /> in <paramref name="language" /> (a
    ///     two-letter code). With <paramref name="earlyComments" /> (reactions from the first days after release), the
    ///     summary also compares them with <paramref name="comments" />, the later ones. Never throws for upstream
    ///     failures: it returns <c>IsTransientFailure = true</c> instead.
    /// </summary>
    Task<SummaryResult> SummarizeAsync(string title, IReadOnlyList<string> comments, string language,
        IReadOnlyList<string>? earlyComments = null, CancellationToken ct = default);
}
