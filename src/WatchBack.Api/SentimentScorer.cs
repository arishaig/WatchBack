using VaderSharp2;

namespace WatchBack.Api;

/// <summary>
///     Thin singleton wrapper around VaderSharp2's <see cref="SentimentIntensityAnalyzer" />.
///     Extracted from <c>SyncEndpoints</c> so the analyzer can be injected and tested in isolation.
/// </summary>
public sealed class SentimentScorer
{
    private readonly SentimentIntensityAnalyzer _analyzer = new();

    /// <summary>
    ///     Returns the VADER compound sentiment score for <paramref name="content" />,
    ///     or <c>null</c> when the input is blank.
    /// </summary>
    public float? Score(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return (float)_analyzer.PolarityScores(content).Compound;
    }
}