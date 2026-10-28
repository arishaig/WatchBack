using System.Text.RegularExpressions;

namespace WatchBack.Core.Models;

/// <summary>
///     Identifies one browser so per-viewer state (the manually chosen episode) is not shared between
///     people using the same WatchBack instance. The id is a random value the browser generates itself;
///     it is not a credential, only a key, so anything malformed is simply treated as "no client".
/// </summary>
public static partial class WatchClientId
{
    public const int MaxLength = 64;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidPattern();

    public static string? Normalize(string? clientId)
    {
        return clientId is not null && ValidPattern().IsMatch(clientId) ? clientId : null;
    }
}
