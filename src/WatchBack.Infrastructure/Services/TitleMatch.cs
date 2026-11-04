using System.Globalization;
using System.Text;

namespace WatchBack.Infrastructure.Services;

/// <summary>Whether two titles name the same thing, ignoring case, punctuation and accents ("Pokémon" / "Pokemon").</summary>
internal static class TitleMatch
{
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && Fold(a) is { Length: > 0 } folded && folded == Fold(b);

    public static string Fold(string title)
    {
        StringBuilder folded = new(title.Length);
        foreach (char c in title.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                folded.Append(char.ToLowerInvariant(c));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && folded.Length > 0 &&
                     folded[^1] != ' ')
            {
                folded.Append(' ');
            }
        }

        return folded.ToString().Trim();
    }
}
