using System.Text.RegularExpressions;

namespace WatchBack.Core.Models;

/// <summary>
///     Names the "viewer" that per-viewer state (manual episode choice, sync status, prefetch, sync
///     scheduling) belongs to, so one browser cannot affect another unless they are the same viewer.
///     A viewer is the chosen watch-state user when there is one — so two browsers signed in as the
///     same Jellyfin user are the same viewer — otherwise the browser's own random client id.
///     <c>null</c> means "no identity" (API/MCP callers) and addresses the shared state.
/// </summary>
public static partial class WatchViewer
{
    [GeneratedRegex("^[0-9a-z]{1,64}$")]
    private static partial Regex UserIdPattern();

    public static string? Key(string? userId, string? clientId)
    {
        string? user = NormalizeUserId(userId);
        if (user is not null)
        {
            return "u:" + user;
        }

        string? client = WatchClientId.Normalize(clientId);
        return client is null ? null : "c:" + client;
    }

    // Jellyfin returns ids dashed or dash-less, in either case.
    public static string? NormalizeUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        string normalized = userId.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return UserIdPattern().IsMatch(normalized) ? normalized : null;
    }
}
