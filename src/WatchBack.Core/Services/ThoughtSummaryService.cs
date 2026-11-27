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
    ///     writing it returns Pending and carries on in the background, so the next call finds it done. With
    ///     <paramref name="earlyThoughts" /> (the time-machine window, a subset of <paramref name="thoughts" />), the
    ///     summary compares those early reactions with the rest when both sides have enough to say something.
    /// </summary>
    Task<ThoughtSummary> SummarizeAsync(string title, IReadOnlyList<Thought> thoughts, string language,
        IReadOnlyList<Thought>? earlyThoughts = null, CancellationToken ct = default);
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

    /// <summary>Each side of an early-versus-later comparison needs this many comments to be worth comparing.</summary>
    public const int MinCommentsPerSide = 3;

    /// <summary>Each side of a comparison gets half the room.</summary>
    public const int MaxCommentsPerSide = MaxComments / 2;

    private static readonly TimeSpan s_defaultBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan s_failurePause = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _budget = budget ?? s_defaultBudget;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new();
    private readonly ConcurrentDictionary<string, Task<SummaryResult>> _inFlight = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<ThoughtSummary> SummarizeAsync(string title, IReadOnlyList<Thought> thoughts, string language,
        IReadOnlyList<Thought>? earlyThoughts = null, CancellationToken ct = default)
    {
        ISummarizer summarizer = summarizerFactory();
        if (!summarizer.IsConfigured)
        {
            return new ThoughtSummary(SummaryStatus.Unavailable);
        }

        (IReadOnlyList<string> comments, IReadOnlyList<string>? early) = Choose(thoughts, earlyThoughts);
        if (comments.Count == 0)
        {
            return new ThoughtSummary(SummaryStatus.Empty);
        }

        int count = comments.Count + (early?.Count ?? 0);
        bool compared = early is not null;
        string key = Key(title, language, comments, early);
        if (_failedUntil.TryGetValue(key, out DateTimeOffset until) && _time.GetUtcNow() < until)
        {
            return new ThoughtSummary(SummaryStatus.Failed, CommentCount: count, Compared: compared);
        }

        Task<SummaryResult> work = _inFlight.GetOrAdd(key,
            _ => RunAsync(key, summarizer, title, comments, early, language));
        await Task.WhenAny(work, Task.Delay(_budget, _time, ct));
        ct.ThrowIfCancellationRequested();

        if (!work.IsCompleted)
        {
            return new ThoughtSummary(SummaryStatus.Pending, CommentCount: count, Compared: compared);
        }

        SummaryResult result = await work;
        return result.Text is { Length: > 0 } text
            ? new ThoughtSummary(SummaryStatus.Ready, text, count, compared)
            : new ThoughtSummary(SummaryStatus.Failed, CommentCount: count, Compared: compared);
    }

    private async Task<SummaryResult> RunAsync(string key, ISummarizer summarizer, string title,
        IReadOnlyList<string> comments, IReadOnlyList<string>? early, string language)
    {
        // Yield first so GetOrAdd has stored the task before it can complete and remove itself.
        await Task.Yield();
        try
        {
            SummaryResult result = await summarizer.SummarizeAsync(title, comments, language, earlyComments: early);
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
    ///     What to send: everything together, or, when the early reactions and the later ones each have enough comments,
    ///     the two sides apart so the summary can compare them. Replies belong to the side of the thought they answer.
    /// </summary>
    private static (IReadOnlyList<string> Comments, IReadOnlyList<string>? Early) Choose(
        IReadOnlyList<Thought> thoughts, IReadOnlyList<Thought>? earlyThoughts)
    {
        if (earlyThoughts is { Count: > 0 })
        {
            HashSet<string> earlyIds = earlyThoughts.Select(t => t.Id).ToHashSet();
            IReadOnlyList<string> early = SelectComments(earlyThoughts, MaxCommentsPerSide, MaxTotalLength / 2);
            IReadOnlyList<string> later = SelectComments(thoughts.Where(t => !earlyIds.Contains(t.Id)).ToList(),
                MaxCommentsPerSide, MaxTotalLength / 2);
            if (early.Count >= MinCommentsPerSide && later.Count >= MinCommentsPerSide)
            {
                return (later, early);
            }
        }

        return (SelectComments(thoughts), null);
    }

    /// <summary>
    ///     The comments worth reading, best first: liked ones before unliked, newer before older, each trimmed, within
    ///     a total length. Replies count as comments of their own.
    /// </summary>
    public static IReadOnlyList<string> SelectComments(IReadOnlyList<Thought> thoughts,
        int maxComments = MaxComments, int maxTotal = MaxTotalLength)
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
                     .Take(maxComments))
        {
            string text = t.Content.Trim();
            if (text.Length > MaxCommentLength)
            {
                text = text[..MaxCommentLength] + "…";
            }

            if (total + text.Length > maxTotal)
            {
                break;
            }

            total += text.Length;
            chosen.Add(text);
        }

        return chosen;
    }

    private static string Key(string title, string language, IReadOnlyList<string> comments,
        IReadOnlyList<string>? early) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            language + "\n" + title + "\n" + string.Join("\n\u0001", comments) +
            (early is null ? "" : "\n\u0002early\n" + string.Join("\n\u0001", early)))));
}
