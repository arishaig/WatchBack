namespace WatchBack.Infrastructure.Extensions;

internal static class FormValueExtensions
{
    private const string ExistingSentinel = "__EXISTING__";

    /// <summary>
    /// Resolves a form value, falling back to the stored option when the user
    /// has not changed the field (indicated by the <c>__EXISTING__</c> sentinel).
    /// </summary>
    internal static string ResolveFormValue(
        this IReadOnlyDictionary<string, string> form, string key, string? fallback)
    {
        var v = form.GetValueOrDefault(key) ?? string.Empty;
        return v == ExistingSentinel ? (fallback ?? string.Empty) : v;
    }
}
