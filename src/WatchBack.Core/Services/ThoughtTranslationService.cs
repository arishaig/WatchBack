using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using WatchBack.Core.Interfaces;
using WatchBack.Core.Models;

namespace WatchBack.Core.Services;

/// <summary>Translates the content of thoughts (and their replies) into a target language.</summary>
public interface IThoughtTranslationService
{
    /// <summary>
    ///     Returns <paramref name="results" /> with <see cref="Thought.Content" /> translated where possible.
    ///     Waits for at most a short budget: thoughts whose translation isn't ready are returned as they were, while
    ///     the work carries on in the background so a later call finds it done.
    /// </summary>
    Task<IReadOnlyList<ThoughtResult>> TranslateAsync(IReadOnlyList<ThoughtResult> results, string targetLanguage,
        CancellationToken ct = default);
}

/// <summary>
///     Singleton. Holds the state that must outlive one sync: translations in flight (so the 5-second poll joins
///     them rather than repeating them), a cap on concurrent requests to the translator, and a short cool-down after
///     the translator fails so an unreachable server isn't hammered every poll.
/// </summary>
public sealed class ThoughtTranslationService(
    Func<ITranslator> translatorFactory,
    ILogger<ThoughtTranslationService> logger,
    TimeProvider? timeProvider = null,
    TimeSpan? budget = null) : IThoughtTranslationService, IDisposable
{
    private static readonly TimeSpan s_defaultBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan s_failureCooldown = TimeSpan.FromSeconds(30);
    private const int MaxConcurrentTranslations = 2;

    private readonly TimeSpan _budget = budget ?? s_defaultBudget;
    private readonly ConcurrentDictionary<string, Task<TranslationResult>> _inFlight = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentTranslations);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _cooldownUntilTicks;

    public void Dispose() => _slots.Dispose();

    public async Task<IReadOnlyList<ThoughtResult>> TranslateAsync(IReadOnlyList<ThoughtResult> results,
        string targetLanguage, CancellationToken ct = default)
    {
        string target = targetLanguage.Trim().ToLowerInvariant();
        if (target.Length == 0)
        {
            return results;
        }

        Dictionary<string, Task<TranslationResult>> pending = new(StringComparer.Ordinal);
        foreach (ThoughtResult result in results)
        {
            foreach (Thought thought in result.Thoughts ?? [])
            {
                Collect(thought, target, pending);
            }
        }

        if (pending.Count == 0)
        {
            return results;
        }

        // Wait for everything, but only up to the budget; unfinished work keeps running in the background.
        Task all = Task.WhenAll(pending.Values);
        Task budgetElapsed = Task.Delay(_budget, _time, ct);
        await Task.WhenAny(all, budgetElapsed);
        ct.ThrowIfCancellationRequested();

        return results.Select(r => r.Thoughts is null
                ? r
                : r with { Thoughts = r.Thoughts.Select(t => Apply(t, pending)).ToList() })
            .ToList();
    }

    private void Collect(Thought thought, string target, Dictionary<string, Task<TranslationResult>> pending)
    {
        if (!string.IsNullOrWhiteSpace(thought.Content) && !pending.ContainsKey(thought.Content))
        {
            pending[thought.Content] = GetOrStart(thought.Content, target);
        }

        // The post title and body repeat on every thought of a thread; identical texts share one translation.
        foreach (string? postText in new[] { thought.PostTitle, thought.PostBody })
        {
            if (!string.IsNullOrWhiteSpace(postText) && !pending.ContainsKey(postText))
            {
                pending[postText] = GetOrStart(postText, target);
            }
        }

        foreach (Thought reply in thought.Replies)
        {
            Collect(reply, target, pending);
        }
    }

    private Task<TranslationResult> GetOrStart(string text, string target)
    {
        string key = $"{target}\n{text}";
        return _inFlight.GetOrAdd(key, _ => RunAsync(key, text, target));
    }

    private async Task<TranslationResult> RunAsync(string key, string text, string target)
    {
        // Yield first so GetOrAdd has stored the task before it can complete and remove itself.
        await Task.Yield();
        try
        {
            if (_time.GetTimestamp() < Interlocked.Read(ref _cooldownUntilTicks))
            {
                return new TranslationResult(text, null, false, true);
            }

            await _slots.WaitAsync();
            try
            {
                TranslationResult result = await translatorFactory().TranslateAsync(text, target);
                if (result.IsTransientFailure)
                {
                    Interlocked.Exchange(ref _cooldownUntilTicks,
                        _time.GetTimestamp() + (long)(s_failureCooldown.TotalSeconds * _time.TimestampFrequency));
                }

                return result;
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Translating a thought failed");
            return new TranslationResult(text, null, false, true);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private static Thought Apply(Thought thought, Dictionary<string, Task<TranslationResult>> pending)
    {
        Thought result = thought;
        if (pending.TryGetValue(thought.Content, out Task<TranslationResult>? task) &&
            task is { IsCompletedSuccessfully: true, Result: { WasTranslated: true } translation })
        {
            result = thought with
            {
                Content = translation.Text,
                OriginalContent = thought.Content,
                TranslatedFrom = translation.DetectedLanguage
            };
        }

        TranslationResult? title = TranslatedOrNull(thought.PostTitle, pending);
        TranslationResult? body = TranslatedOrNull(thought.PostBody, pending);
        if (title is not null || body is not null)
        {
            result = result with
            {
                PostTitle = title?.Text ?? thought.PostTitle,
                PostBody = body?.Text ?? thought.PostBody,
                OriginalPostTitle = title is null ? null : thought.PostTitle,
                OriginalPostBody = body is null ? null : thought.PostBody,
                PostTranslatedFrom = title?.DetectedLanguage ?? body?.DetectedLanguage
            };
        }

        return thought.Replies.Count == 0
            ? result
            : result with { Replies = thought.Replies.Select(r => Apply(r, pending)).ToList() };
    }

    private static TranslationResult? TranslatedOrNull(string? text, Dictionary<string, Task<TranslationResult>> pending) =>
        text is not null && pending.TryGetValue(text, out Task<TranslationResult>? task) &&
        task is { IsCompletedSuccessfully: true, Result: { WasTranslated: true } translation }
            ? translation
            : null;
}
