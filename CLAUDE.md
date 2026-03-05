# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# WatchBack project details

- **Project Name**: WatchBack
- **Build & Run**:
  - Dev: `uvicorn main:app --reload`
  - Docker: `docker build -t watchback:dev .`
  - Docker Run: `docker run -d --name watchback -p 8000:8000 --env-file .env watchback:dev`

WatchBack is a self-hosted web app that connects Jellyfin and Trakt.tv. When you start watching a TV episode in Jellyfin, it fetches Trakt comments for that episode and filters them to only show reactions posted within 14 days of the original air date — avoiding spoilers while letting you experience the community's first reactions.

## Running the App

**With Docker (primary method):**
```bash
cp .env.example .env  # create .env with JF_URL, JF_API_KEY, TRAKT_CLIENT_ID
docker compose up -d --build
```
App is available at `http://localhost:8080`.

**Locally for development:**
```bash
uv sync
uv run uvicorn main:app --reload
```
App runs at `http://localhost:8000`.

## Required Environment Variables

| Variable | Description |
|---|---|
| `JF_URL` | Jellyfin base URL (default: `http://jellyfin:8096`) |
| `JF_API_KEY` | Jellyfin API key |
| `TRAKT_CLIENT_ID` | Trakt.tv application Client ID |
| `TRAKT_USERNAME` | Trakt.tv username (enables Trakt `/watching` as session source) |
| `TRAKT_ACCESS_TOKEN` | Trakt.tv OAuth access token (enables private profile `/watching`) |
| `REDDIT_AUTO_OPEN` | If set, attempts to find the actual Reddit discussion thread instead of a Google search link |
| `CONFIG_DIR` | Path for diskcache storage (default: `/config`) |
| `DATA_DIR` | Path for static files and other data (default: `/data`) |

## Architecture

The entire backend is a single file: `main.py` (FastAPI). The frontend is `static/index.html` (Alpine.js + Tailwind via CDN, served as a static SPA).

### Capability Tiers

The app supports progressive configuration — only set what you have:

| Tier | Env vars | Session source | Comments | Auto-refresh |
|------|----------|----------------|----------|--------------|
| 1 | `JF_API_KEY` only | Jellyfin | None (empty arrays, Reddit link) | Webhook only |
| 2 | `TRAKT_CLIENT_ID` + `TRAKT_USERNAME` or `TRAKT_ACCESS_TOKEN` | Trakt `/watching` | Yes | Background task (30s poll) |
| 3 | `JF_API_KEY` + `TRAKT_CLIENT_ID` | Jellyfin | Yes | Manual sync |
| 4 | All + webhook plugin | Jellyfin | Yes | Webhook→SSE auto-refresh |

Session source priority: Jellyfin preferred; if Jellyfin is configured but returns idle, falls through to Trakt watching.

**API flow for `/api/sync`:**
1. Check `jf_session` cache; if miss, query Jellyfin `/Sessions` (cached 10 sec)
2. If Jellyfin idle or unconfigured, call Trakt `/users/{username}/watching` (cached 30 sec)
3. If no session source configured, return `setup_required`; if nothing playing, return `idle`
4. Build Reddit Google search URL (always, even without Trakt comments)
5. If `TRAKT_CLIENT_ID` unset (Tier 1), return early with empty comment arrays
6. Search Trakt for the show slug (skipped if slug already known from Trakt watching)
7. Fetch episode comments sorted by newest
8. Apply the **Time Machine filter**: keep only comments posted within 14 days of `PremiereDate` (skipped if premiere date unavailable)
9. Cache the full response for 24 hours (skipped if the thread is "live" — newest comment < 24 hours old)

**Webhook → SSE auto-refresh (Tier 4):**
- Jellyfin Webhook Plugin POSTs to `/api/webhook` on `PlaybackStart`/`PlaybackStop`
- This clears the session cache and stamps `last_webhook_time` in diskcache
- `/api/stream` is an SSE endpoint the frontend subscribes to; it polls every 2 seconds and emits `data: refresh` when `last_webhook_time` changes
- Frontend Alpine.js component auto-calls `sync()` on each SSE refresh event

**Background polling task (Tier 2):**
- On startup, if `HAS_TRAKT_WATCH` and not `HAS_JELLYFIN`, starts `trakt_watch_poller()` coroutine
- Every 30 seconds, fetches Trakt `/watching`; if the episode fingerprint changes, clears session cache and stamps `last_webhook_time` — triggering the SSE refresh the same way a Jellyfin webhook would

**Frontend state (Alpine.js `app()` component):**
- `mode: 'time'` shows `data.time_machine` (premiere-window comments)
- `mode: 'all'` shows `data.all_comments` (all Trakt comments)
- Defaults to `'all'` mode if `time_machine` is empty
- `data.reddit_url` is either a direct Reddit thread URL (when `REDDIT_AUTO_OPEN` finds one) or a Google search link
- `data.reddit_thread_found` is `true` when a direct thread was found; the button shows 💬 instead of 🔍

**Reddit thread search (`find_best_reddit_thread()`):**
- Only runs when `REDDIT_AUTO_OPEN` is set; queries `reddit.com/search.json` for `{series} S##E##`
- Filters results to posts containing the episode code in the title (guards against general show threads)
- Ranking: Tier 0 = show-specific subreddit (derived heuristically: `base`, `base+"tv"`, etc.), Tier 1 = `r/television`/`r/movies`/`r/tvshows`, Tier 2 = anything else; within tier, highest score wins
- Result cached separately under `reddit_{series}_s{season}e{episode}` key (24h TTL); empty string = searched but not found
