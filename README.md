# WatchBack

<p align="center">
  <img src="static/watchback.png" alt="WatchBack" width="120">
</p>

<p align="center">
  <a href="https://github.com/arishaig/WatchBack/actions/workflows/ci-cd.yml">
    <img src="https://github.com/arishaig/WatchBack/actions/workflows/ci-cd.yml/badge.svg" alt="CI/CD">
  </a>
  <a href="https://github.com/arishaig/WatchBack/pkgs/container/watchback">
    <img src="https://img.shields.io/badge/docker-ghcr.io-blue?logo=docker" alt="Docker Image">
  </a>
</p>

A self-hosted companion for your TV watching. WatchBack connects to **Jellyfin** or **Trakt** to detect what you're watching, then pulls in reactions from **Trakt comments**, **Bluesky**, and **Reddit** — filtered to the original air-date window so you can relive the "live" experience without spoilers.

## Features

- ⏱️ **Time Machine** — Shows only comments posted within 14 days of the episode's premiere
- 💬 **Multi-Source** — Aggregates reactions from Trakt, Bluesky, and Reddit in one view
- 🗂️ **Thread Grouping** — Group Reddit comments by discussion thread, with persistence
- 🔍 **Source Filtering** — Toggle individual sources; counts update live
- 🔄 **Auto-Refresh** — SSE-based live updates via Jellyfin webhooks or Trakt background polling
- ⚙️ **UI Configuration** — Manage all settings from the web interface (overrides env vars)
- 🐳 **Docker-Ready** — Multi-stage build with Tailwind CSS compilation

## Quick Start

### Docker (recommended)

```bash
# 1. Clone and configure
git clone https://github.com/arishaig/WatchBack.git
cd WatchBack
cp .env.example .env   # Edit with your API keys

# 2. Run
docker compose up -d --build

# Visit http://localhost:8080
```

### Manual

```bash
uv sync
uv run uvicorn main:app --reload
# Visit http://localhost:8000
```

## Configuration

All settings can be configured via environment variables **or** the in-app settings panel. UI overrides take priority over env vars.

| Variable | Description | Default |
|---|---|---|
| `JF_URL` | Jellyfin server URL | `http://jellyfin:8096` |
| `JF_API_KEY` | Jellyfin API key | — |
| `TRAKT_CLIENT_ID` | Trakt application Client ID | — |
| `TRAKT_USERNAME` | Trakt username (enables session detection via Trakt) | — |
| `TRAKT_ACCESS_TOKEN` | Trakt OAuth token (for private profiles) | — |
| `REDDIT_AUTO_OPEN` | Auto-find Reddit discussion threads | — |
| `REDDIT_MAX_THREADS` | Max Reddit threads to return | `3` |
| `BSKY_IDENTIFIER` | Bluesky handle or email | — |
| `BSKY_APP_PASSWORD` | Bluesky app password | — |
| `TIME_MACHINE_DAYS` | Days after premiere to include in Time Machine | `14` |
| `CONFIG_DIR` | Persistent storage path | `/config` |

### Capability Tiers

The app works progressively — configure only what you have:

| Tier | What You Need | What You Get |
|------|---------------|-------------|
| 1 | `JF_API_KEY` | Jellyfin session detection, Reddit search links |
| 2 | + `TRAKT_CLIENT_ID` | Trakt comments + Time Machine filter |
| 3 | + `BSKY_*` credentials | Bluesky post search |
| 4 | + `TRAKT_USERNAME` | Auto-detect playback via Trakt (no Jellyfin required) |

## Architecture

- **Backend**: Single-file FastAPI app ([main.py](main.py))
- **Frontend**: Alpine.js SPA with Tailwind CSS ([static/index.html](static/index.html), [static/app.js](static/app.js), [static/app.css](static/app.css))
- **Storage**: DiskCache for session data, config overrides, and API response caching
- **Refresh**: SSE endpoint (`/api/stream`) driven by Jellyfin webhooks (`/api/webhook`) or a Trakt background poller

## Development

```bash
# Install all dependencies (including dev/test)
uv sync --all-groups

# Run tests
uv run python -m pytest tests -v -m "not a11y"

# Run accessibility tests (requires Playwright browsers)
uv run playwright install chromium
uv run python -m pytest tests -v -m a11y

# Run with auto-reload
uv run uvicorn main:app --reload
```

## License

[GPL-3.0](LICENSE)
