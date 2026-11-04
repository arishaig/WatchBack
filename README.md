# WatchBack

WatchBack is a self-hosted companion app for your media server. While you watch a TV show or movie, it automatically fetches Reddit threads, Trakt comments, Bluesky posts, Lemmy threads, and other community discussions about the exact episode you're watching — and surfaces them in a clean, single-page UI.

## Features

- **Automatic sync** — detects what you're currently watching and fetches relevant discussions
- **Time Machine** — filters results to only show reactions posted close to the original air date, so you get the authentic first-watch experience
- **Multiple sources** — Reddit threads (via [PullPush](https://pullpush.io/) and [Arctic Shift](https://github.com/ArthurHeitmann/arctic_shift)), Trakt comments, Bluesky posts, and Lemmy threads, all in one place
- **Reddit search shortcut** — one-click search for the episode you're watching
- **Multiple themes** — dark, light, Solarized Dark, Solarized Light, and Monokai

## Quick Start

### Docker Compose (recommended)

```yaml
services:
  watchback:
    image: watchback
    build: .
    container_name: watchback
    ports:
      - "5000:5000"
    volumes:
      - watchback-data:/app/data
    environment:
      Jellyfin__BaseUrl: http://jellyfin:8096
      Jellyfin__ApiKey: your-api-key
    restart: unless-stopped

volumes:
  watchback-data:
```

Then open `http://localhost:5000` — you'll be prompted to set a username and password on first run.

### Setting up with an AI assistant

WatchBack has an MCP server at `/mcp`, so an AI assistant can walk you through setup. On first start, choose **Set it up with an AI assistant**. WatchBack creates an API key and shows the command to connect Claude Code, plus the URL and header that other assistants need. Then ask your assistant to "Help me set up WatchBack", or use its `set_up_watchback` prompt.

The assistant can read and change your provider settings and preferences, but not your password or login settings. It never sees a stored secret. Whenever a step needs an API key or password, it gives you a link that opens that provider's settings, so you paste the secret into WatchBack and not into the chat.

Anyone holding the key can still change where WatchBack connects, for example your Jellyfin server's address, so give it only to an assistant you trust. Revoke it under **Security → API keys** in settings once setup is done. The assistant must be able to reach WatchBack at the address shown. An assistant running outside your network can't reach a LAN-only address, and a reverse proxy that requires a login must let `/mcp` through with the bearer key.

### Running locally

```bash
dotnet run --project src/WatchBack.Api
```

## Configuration

All provider settings can be configured in the UI under the gear icon, or via environment variables / `appsettings.json`. In the UI, each provider is a row under **Sources**; changes save as you make them, and a pasted key is tested with the provider before it's saved. A value set by an environment variable is shown as such, and one saved in the UI overrides it until you choose **Use environment value**. Environment variables use double-underscore as the section separator (e.g. `Jellyfin__ApiKey`).

### Watch providers

| Variable | Description |
|---|---|
| `Jellyfin__BaseUrl` | Base URL of your Jellyfin server |
| `Jellyfin__ApiKey` | Jellyfin API key |

### Thought providers

| Variable | Description | Default |
|---|---|---|
| `Reddit__MaxThreads` | Maximum number of Reddit threads to fetch | `3` |
| `Reddit__MaxComments` | Maximum number of comments per thread | `250` |
| `Reddit__UseArcticShift` | Search Reddit through Arctic Shift (the primary archive) | `true` |
| `Reddit__UsePullPush` | Fall back to PullPush when Arctic Shift has nothing, for episodes aired before `Reddit__PullPushCutoff` | `true` |
| `Reddit__PullPushCutoff` | Air date after which PullPush is never asked (it stopped archiving around then) | `2025-05-01` |
| `Trakt__ClientId` | Trakt API client ID | |
| `Trakt__Username` | Trakt username to fetch comments for | |
| `Bluesky__Handle` | Bluesky handle (e.g. `you.bsky.social`) | |
| `Bluesky__AppPassword` | Bluesky [app password](https://bsky.app/settings/app-passwords). Required: Bluesky's search rejects signed-out requests | |
| `Lemmy__InstanceUrl` | Lemmy instance to search (it searches what that instance federates) | `https://lemmy.world` |
| `Lemmy__Community` | Limit the search to one community (e.g. `television`); empty searches all | |
| `Lemmy__MaxPosts` | Maximum number of Lemmy posts to fetch comments for | `3` |
| `Lemmy__MaxComments` | Maximum number of comments per post | `250` |

### App preferences

| Variable | Description | Default |
|---|---|---|
| `WatchBack__TimeMachineDays` | Days after air date to include in Time Machine | `14` |
| `WatchBack__WatchProvider` | Active watch provider (`jellyfin`) | `jellyfin` |
| `WatchBack__SearchEngine` | Search engine for Reddit shortcut (`google`, `duckduckgo`, `bing`, `custom`) | `google` |
| `WatchBack__CustomSearchUrl` | URL prefix for custom search engine | |

### Forward auth

If you run WatchBack behind an auth proxy that passes a trusted header (e.g. `X-Remote-User`), you can enable forward auth under **Security** in settings. When active, the login screen is bypassed entirely.

### Who can change settings

Admins can change WatchBack's settings; everyone else can watch and pick their own theme and language. This protects against accidental changes. It is not a security boundary.

- **Behind Authelia (or another proxy that sends groups):** members of the admin group are admins. The defaults are the `Remote-Groups` header and the `admins` group; you can change both next to the forward auth settings. A proxy that sends no groups header falls back to the next rule.
- **Otherwise:** the first person who picks their name in the Jellyfin user picker becomes the admin. They can make other users admins under Settings → Security → Admins. If there's only one Jellyfin user, or the watch source can't tell users apart (Trakt, manual), everyone is an admin.

With Traefik, the forward-auth middleware must list `Remote-Groups` in `authResponseHeaders`. Without it, WatchBack never sees the groups and uses the picker rule instead.

If your Authelia admin group isn't called `admins`, set `Auth__ForwardAuthAdminGroup` to its name before upgrading. Otherwise nobody will be an admin. Setting `Auth__ForwardAuthGroupsHeader` to an empty value ignores groups completely. Once the forward auth settings have been saved in WatchBack, the saved values win over these environment variables. To change them after that, edit the `Auth` section of `user-settings.json`, which sits next to the database.

To reset the picker-based admin list, delete the `Access` section from `user-settings.json`. The next person to pick their name becomes the admin.

## Credits

Reddit discussion comes from two volunteer-run archives. Please be considerate of them: WatchBack looks each episode up once, caches the answer, and backs off when they ask it to.

- [Arctic Shift](https://github.com/ArthurHeitmann/arctic_shift) by Arthur Heitmann: the primary source for Reddit threads and comments. Service status: [status.arctic-shift.photon-reddit.com](https://status.arctic-shift.photon-reddit.com).
- [PullPush](https://pullpush.io/): a fallback for episodes that aired before May 2025.

## Adding a new provider

WatchBack is built around a provider pattern. To add a new watch state source or thought source:

1. Implement `IWatchStateProvider` or `IThoughtProvider` from `WatchBack.Core`
2. Register it in `Program.cs`

See the existing implementations in `WatchBack.Infrastructure` for reference, and the interface XML docs in `WatchBack.Core/Interfaces` for the full contract.

## Building

```bash
# Build
dotnet build

# Run tests
dotnet test

# Publish (production)
dotnet publish -c Release
```

The Docker build handles Tailwind CSS compilation automatically as a separate stage.
