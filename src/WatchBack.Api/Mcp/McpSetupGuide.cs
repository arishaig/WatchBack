using System.ComponentModel;

using ModelContextProtocol.Server;

namespace WatchBack.Api.Mcp;

/// <summary>How an assistant should walk someone through setting WatchBack up.</summary>
public static class McpSetupGuide
{
    /// <summary>Sent to every MCP client when it connects.</summary>
    public const string ServerInstructions =
        "WatchBack shows community discussion (Reddit, Trakt, Bluesky, Lemmy) and ratings for the episode or movie " +
        "someone is watching. To help set it up, start with describe_integrations, or use the set_up_watchback prompt. " +
        "Secrets (API keys, passwords, tokens) belong in WatchBack, not in this chat: give the person the link from " +
        "get_settings_link to paste them there, then test with test_integration. Only save a secret with " +
        "update_config if the person chose to paste it here.";

    /// <summary>The walkthrough the set_up_watchback prompt starts.</summary>
    public const string Walkthrough =
        """
        Help me set up WatchBack, one step at a time. Ask before changing anything, and keep each step short.

        1. Call describe_integrations to see what is already set up.
        2. Where WatchBack sees what I'm watching: ask which I use (Jellyfin, Trakt, or picking it by hand),
           set WatchBack__WatchProvider, and set up that integration.
        3. Discussion sources: Reddit and Lemmy need nothing. Ask whether I want Trakt comments and Bluesky posts,
           which need accounts, and turn off any I don't want with set_provider_enabled.
        4. Ratings: OMDb needs a free API key from omdbapi.com.
        5. Preferences: offer the Time Machine window (how many days after airing to show reactions from); leave
           the rest at their defaults unless I ask.

        For every secret (API key, password, token), don't ask me to paste it here. Give me the link from
        get_settings_link, tell me where to get the secret, wait for me to say it's saved, then call
        test_integration once with no values. Non-secret settings (server URLs, usernames) you can save with
        update_config after I confirm them.

        When something fails, explain the message in plain words and what to check. At the end, summarise what is
        set up, call trigger_sync, and tell me what get_watch_state reports.
        """;
}

[McpServerPromptType]
public sealed class WatchBackMcpPrompts
{
    [McpServerPrompt(Name = "set_up_watchback")]
    [Description("Walk through setting WatchBack up: where it sees what's playing, discussion sources, ratings, preferences.")]
    public static string SetUpWatchBack() => McpSetupGuide.Walkthrough;
}
