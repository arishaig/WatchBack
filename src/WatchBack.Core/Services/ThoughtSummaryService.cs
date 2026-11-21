using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Core.Services;

/// <summary>Writes the summary shown at the top of the feed.</summary>
public interface IThoughtSummaryService
{
    /// <summary>
    ///     Summarizes <paramref name="thoughts" /> (and their replies). Waits for a short budget; if the model is still
    ///     writing it returns Pending and carries on in the background, so the next call finds it done.
    /// </summary>
    Task<ThoughtSummary> SummarizeAsync(string title, IReadOnlyList<Thought> thoughts, string language,
        CancellationToken ct = default);
}

/// <summary>
///     Singleton: it holds the summaries being written (so every poll joins one rather than starting another) and a
///     pause after a failure so an unreachable model isn't asked again every few seconds.
/// </summary>
public sealed class ThoughtSummaryService(
    Func<ISummarizer> summarizerFactory,
    ILogger<ThoughtSummaryService> logger,
    TimeProvider? timeProvider = null,
    TimeSpan? budget = null) : IThoughtSummaryService
{
    /// <summary>The most comments sent to the model: enough to see the mood, few enough for a small model.</summary>
    public const int MaxComments = 40;

    public const int MaxCommentLength = 500;
    public const int MaxTotalLength = 12_000;

    private static readonly TimeSpan s_defaultBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan s_failurePause = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _budget = budget ?? s_defaultBudget;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new();
    private readonly ConcurrentDictionary<string, Task<SummaryResult>> _inFlight = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ThoughtSummary> SummarizeAsync(string title, IReadOnlyList<Thought> thoughts, string language,
        CancellationToken ct = default)
    {
        ISummarizer summarizer = summarizerFactory();
        if (!summarizer.IsConfigured)
        {
            return new ThoughtSummary(SummaryStatus.Unavailable);
        }

        IReadOnlyList<string> comments = SelectComments(thoughts);
        if (comments.Count == 0)
        {
            return new ThoughtSummary(SummaryStatus.Empty);
        }

        string key = Key(title, language, comments);
        if (_failedUntil.TryGetValue(key, out DateTimeOffset until) && _time.GetUtcNow() < until)
        {
            return new ThoughtSummary(SummaryStatus.Failed, CommentCount: comments.Count);
        }

        Task<SummaryResult> work = _inFlight.GetOrAdd(key, _ => RunAsync(key, summarizer, title, comments, language));
        await Task.WhenAny(work, Task.Delay(_budget, _time, ct));
        ct.ThrowIfCancellationRequested();

        if (!work.IsCompleted)
        {
            return new ThoughtSummary(SummaryStatus.Pending, CommentCount: comments.Count);
        }

        SummaryResult result = await work;
        return result.Text is { Length: > 0 } text
            ? new ThoughtSummary(SummaryStatus.Ready, text, comments.Count)
            : new ThoughtSummary(SummaryStatus.Failed, CommentCount: comments.Count);
    }

    private async Task<SummaryResult> RunAsync(string key, ISummarizer summarizer, string title,
        IReadOnlyList<string> comments, string language)
    {
        // Yield first so GetOrAdd has stored the task before it can complete and remove itself.
        await Task.Yield();
        try
        {
            SummaryResult result = await summarizer.SummarizeAsync(title, comments, language);
            if (result.Text is null or { Length: 0 })
            {
                _failedUntil[key] = _time.GetUtcNow() + s_failurePause;
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Summarizing the discussion failed");
            _failedUntil[key] = _time.GetUtcNow() + s_failurePause;
            return new SummaryResult(null, true);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>
    ///     The comments worth reading, best first: liked ones before unliked, newer before older, each trimmed, within
    ///     a total length. Replies count as comments of their own.
    /// </summary>
    public static IReadOnlyList<string> SelectComments(IReadOnlyList<Thought> thoughts)
    {
        List<Thought> flat = [];
        void Walk(IEnumerable<Thought> items)
        {
            foreach (Thought t in items)
            {
                flat.Add(t);
                Walk(t.Replies);
            }
        }

        Walk(thoughts);

        List<string> chosen = [];
        int total = 0;
        foreach (Thought t in flat
                     .Where(t => !string.IsNullOrWhiteSpace(t.Content))
                     .OrderByDescending(t => t.Score ?? 0)
                     .ThenByDescending(t => t.CreatedAt)
                     .Take(MaxComments))
        {
            string text = t.Content.Trim();
            if (text.Length > MaxCommentLength)
            {
                text = text[..MaxCommentLength] + "…";
            }

            if (total + text.Length > MaxTotalLength)
            {
                break;
            }

            total += text.Length;
            chosen.Add(text);
        }

        return chosen;
    }

    private static string Key(string title, string language, IReadOnlyList<string> comments) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            language + "\n" + title + "\n" + string.Join("\n\u0001", comments))));
}
