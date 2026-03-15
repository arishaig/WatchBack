# WatchBack

WatchBack is a self-hosted companion app for your media server. While you watch a TV show or movie, it automatically fetches Reddit threads, Trakt comments, Bluesky posts, and other community discussions about the exact episode you're watching — and surfaces them in a clean, single-page UI.

## Features

- **Automatic sync** — detects what you're currently watching and fetches relevant discussions
- **Time Machine** — filters results to only show reactions posted close to the original air date, so you get the authentic first-watch experience
- **Multiple sources** — Reddit threads (via [PullPush](pullpush.io/)), Trakt comments, and Bluesky posts, all in one place
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

### Running locally

```bash
dotnet run --project src/WatchBack.Api
```

## Configuration

All provider settings can be configured in the UI under the gear icon, or via environment variables / `appsettings.json`. Environment variables use double-underscore as the section separator (e.g. `Jellyfin__ApiKey`).

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
| `Trakt__ClientId` | Trakt API client ID | |
| `Trakt__Username` | Trakt username to fetch comments for | |
| `Bluesky__Handle` | Bluesky handle (e.g. `you.bsky.social`) | |
| `Bluesky__AppPassword` | Bluesky app password | |

### App preferences

| Variable | Description | Default |
|---|---|---|
| `WatchBack__TimeMachineDays` | Days after air date to include in Time Machine | `14` |
| `WatchBack__WatchProvider` | Active watch provider (`jellyfin`) | `jellyfin` |
| `WatchBack__SearchEngine` | Search engine for Reddit shortcut (`google`, `duckduckgo`, `bing`, `custom`) | `google` |
| `WatchBack__CustomSearchUrl` | URL prefix for custom search engine | |

### Forward auth

If you run WatchBack behind an auth proxy that passes a trusted header (e.g. `X-Remote-User`), you can enable forward auth in the Security section of the config UI. When active, the login screen is bypassed entirely.

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
