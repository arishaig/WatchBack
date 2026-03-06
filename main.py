import os
import re
import urllib.parse
import requests
import asyncio
import time
import logging
import json
from datetime import datetime, timedelta
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import StreamingResponse
from fastapi.staticfiles import StaticFiles
from diskcache import Cache

# Configure logging
logger = logging.getLogger(__name__)
logger.setLevel(logging.DEBUG)

# --- Configuration & Caching Setup ---
CONFIG_DIR = os.environ.get("CONFIG_DIR", "/config")
os.makedirs(CONFIG_DIR, exist_ok=True)
cache = Cache(os.path.join(CONFIG_DIR, "cache"))

ENV_MAP = {
    "jf_url":              ("JF_URL",              "http://jellyfin:8096"),
    "jf_api_key":          ("JF_API_KEY",          ""),
    "trakt_client_id":     ("TRAKT_CLIENT_ID",     ""),
    "trakt_username":      ("TRAKT_USERNAME",       ""),
    "trakt_access_token":  ("TRAKT_ACCESS_TOKEN",  ""),
    "reddit_auto_open":    ("REDDIT_AUTO_OPEN",     ""),
    "reddit_max_threads":  ("REDDIT_MAX_THREADS",  "3"),
    "bsky_identifier":     ("BSKY_IDENTIFIER",     ""),
    "bsky_app_password":   ("BSKY_APP_PASSWORD",   ""),
    "webhook_secret":      ("WEBHOOK_SECRET",      ""),
    "theme_mode":          ("THEME_MODE",          "dark"),
}

SECRET_KEYS = {"jf_api_key", "trakt_access_token", "bsky_app_password", "webhook_secret"}

def get_config() -> dict:
    """Consolidate configuration from UI overrides, environment variables, and defaults."""
    stored = cache.get("ui_config") or {}
    result = {}
    for key, (env_name, default) in ENV_MAP.items():
        env_val = os.environ.get(env_name, "")
        # Priority: UI Store > Environment > Default
        result[key] = stored.get(key) if stored.get(key) else (env_val or default)
    
    result["jf_url"] = result["jf_url"].rstrip("/")
    return result

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Handle application startup and shutdown tasks (background workers/cache)."""
    cfg = get_config()
    has_trakt_watch = bool(cfg["trakt_username"] or cfg["trakt_access_token"])
    has_jellyfin = bool(cfg["jf_api_key"])
    poller_task = None
    if has_trakt_watch and not has_jellyfin:
        poller_task = asyncio.create_task(trakt_watch_poller())
    yield
    if poller_task:
        poller_task.cancel()
    cache.close()

app = FastAPI(title="WatchBack API", lifespan=lifespan)

# --- Helpers ---

def _matches_episode(title: str, season: int, episode: int) -> bool:
    """Check if a title string contains a reference to the given season/episode."""
    t = title.upper()
    s, e = season, episode
    if any(re.search(rf"{c}(?!\d)", t) for c in [f"S{s:02d}E{e:02d}", f"S{s}E{e:02d}", f"S{s:02d}E{e}", f"S{s}E{e}"]):
        return True
    if any(re.search(pat, t) for pat in [rf"\b{s}X{e:02d}(?!\d)", rf"\b{s}X{e}(?!\d)", rf"\b{s}\.{e:02d}(?!\d)"]):
        return True
    if re.search(rf"\bSEASON\s+{s}\b.{{0,30}}\bEPISODE\s+{e}\b", t):
        return True
    return False

def trakt_headers(auth: bool = False) -> dict:
    """Build Trakt API headers, optionally including auth bearer token."""
    cfg = get_config()
    h = {"trakt-api-key": cfg["trakt_client_id"], "trakt-api-version": "2"}
    if auth and cfg["trakt_access_token"]:
        h["Authorization"] = f"Bearer {cfg['trakt_access_token']}"
    return h

def find_reddit_threads(series: str, season: int, episode: int, n: int = 3) -> list[dict]:
    """Search Reddit for episode discussion threads, ranked by subreddit relevance."""
    query = f'"{ series}" S{season:02d}E{episode:02d} discussion'
    try:
        resp = requests.get("https://www.reddit.com/search.json", params={"q": query, "sort": "relevance", "limit": 25}, headers={"User-Agent": "WatchBack/1.0"}, timeout=5)
        if resp.status_code != 200:
            return []
        posts = [p["data"] for p in resp.json()["data"]["children"]]
        episode_posts = [p for p in posts if _matches_episode(p.get("title", ""), season, episode)]

        def rank(p):
            sub = p.get("subreddit", "").lower()
            base = re.sub(r"[^a-z0-9]", "", series.lower())
            return (0 if sub in {base, base + "tv"} else 1, -p.get("score", 0))

        top = sorted(episode_posts, key=rank)[:n]
        return [{"url": f"https://www.reddit.com{p['permalink']}", "title": p.get("title", ""), "subreddit": p.get("subreddit", ""), "score": p.get("score", 0)} for p in top]
    except Exception:
        return []

def bsky_access_token() -> str | None:
    """Fetch or return cached Bluesky access token using configured credentials."""
    cached = cache.get("bsky_token")
    if cached:
        logger.debug("Bluesky token from cache")
        return cached
    cfg = get_config()
    if not cfg["bsky_identifier"] or not cfg["bsky_app_password"]:
        logger.debug("Bluesky credentials not configured")
        return None
    try:
        logger.debug("Fetching Bluesky access token")
        resp = requests.post(
            "https://bsky.social/xrpc/com.atproto.server.createSession",
            json={"identifier": cfg["bsky_identifier"], "password": cfg["bsky_app_password"]},
            timeout=5
        )
        if resp.status_code != 200:
            logger.error(f"Bluesky auth failed: {resp.status_code} {resp.text[:200]}")
            return None
        token = resp.json().get("accessJwt")
        if token:
            logger.debug("Bluesky token obtained and cached")
            cache.set("bsky_token", token, expire=5400)  # 90 min (tokens last ~2h)
        return token
    except Exception as e:
        logger.error(f"Bluesky auth exception: {e}")
        return None

async def trakt_watch_poller():
    """Background task that polls Trakt /watching and clears cache on change."""
    last_id = None
    while True:
        try:
            cfg = get_config()
            if not cfg["trakt_username"] and not cfg["trakt_access_token"]:
                await asyncio.sleep(60)
                continue

            username = cfg["trakt_username"] or "me"
            url = f"https://api.trakt.tv/users/{username}/watching"
            resp = requests.get(url, headers=trakt_headers(auth=True), timeout=10)
            
            if resp.status_code == 204: # Nothing playing
                current_id = None
            elif resp.status_code == 200:
                data = resp.json()
                if data.get("type") == "episode":
                    current_id = data["episode"]["ids"]["trakt"]
                elif data.get("type") == "movie":
                    current_id = data["movie"]["ids"]["trakt"]
                else:
                    current_id = None
            else:
                current_id = None

            if current_id != last_id:
                logger.info(f"Trakt session changed: {last_id} -> {current_id}")
                cache.delete("jf_session")
                cache.delete("trakt_session")
                cache.set("last_webhook_time", time.time())
                last_id = current_id

        except Exception as e:
            logger.error(f"Trakt poller error: {e}")
        
        await asyncio.sleep(30)

def _extract_bsky_images(embed: dict) -> list[str]:
    """Recursively extract image URLs from a Bluesky post's embed object."""
    imgs = []
    t = embed.get("$type")
    if t == "app.bsky.embed.images#view":
        for img in embed.get("images", []):
            if img.get("thumb"):
                imgs.append(img["thumb"])
    elif t == "app.bsky.embed.external#view":
        thumb = embed.get("external", {}).get("thumb")
        if thumb:
            imgs.append(thumb)
    elif t == "app.bsky.embed.recordWithMedia#view":
        imgs.extend(_extract_bsky_images(embed.get("media", {})))
    return imgs


def _is_low_content_post(norm_text: str, series: str, season: int, episode: int, images: list) -> bool:
    """Return True if a post is likely bot/low-content after stripping show identifiers."""
    s_long, e_long = str(season).zfill(2), str(episode).zfill(2)
    boring_patterns = [
        series.lower(),
        f"s{s_long}e{e_long}", f"s{season}e{episode}",
        f"{season}x{e_long}", f"{season}x{episode}",
        "playing", "watch along", "watching",
    ]
    test = norm_text
    for pat in boring_patterns:
        test = test.replace(pat, "")
    for char in "[]().,-_#":
        test = test.replace(char, "")
    return len(test.strip()) < 5 and len(norm_text) < 120 and not images


def find_bluesky_posts(series: str, season: int, episode: int, n: int = 10) -> list[dict]:
    """Search Bluesky for posts about the given episode, with dedup and bot filtering."""
    s_long = str(season).zfill(2)
    e_long = str(episode).zfill(2)
    query = f"{series} S{s_long}E{e_long}"
    token = bsky_access_token()
    headers = {"User-Agent": "WatchBack/1.0"}
    url = "https://api.bsky.app/xrpc/app.bsky.feed.searchPosts" if token else "https://public.api.bsky.app/xrpc/app.bsky.feed.searchPosts"
    if token:
        headers["Authorization"] = f"Bearer {token}"

    try:
        logger.debug(f"Searching Bluesky: {query}")
        resp = requests.get(url, params={"q": query, "sort": "latest", "limit": n}, headers=headers, timeout=5)
        if resp.status_code == 403:
            logger.error("Bluesky API: 403 Forbidden")
            return []
        if resp.status_code != 200:
            logger.error(f"Bluesky API error {resp.status_code}: {resp.text[:200]}")
            return []

        posts = resp.json().get("posts", [])
        logger.info(f"Bluesky search returned {len(posts)} post(s)")
        results = []
        seen = set()

        for p in posts:
            text = p.get("record", {}).get("text", "")
            if not text:
                continue

            norm_text = " ".join(text.strip().lower().split())
            images = _extract_bsky_images(p.get("embed", {}))

            # Fuzzy dedup: skip if both text + images match a previous post
            dedupe_key = (norm_text, frozenset(images))
            if dedupe_key in seen:
                continue
            seen.add(dedupe_key)

            if _is_low_content_post(norm_text, series, season, episode, images):
                continue

            results.append({
                "id": p["cid"],
                "comment": text,
                "user": {"username": p["author"]["handle"]},
                "created_at": p.get("record", {}).get("createdAt"),
                "url": f"https://bsky.app/profile/{p['author']['handle']}/post/{p['uri'].split('/')[-1]}",
                "images": images,
                "source": "bluesky",
            })
        return results
    except Exception as e:
        logger.error(f"Bluesky search exception: {e}")
        return []

# --- Endpoints ---

def _fetch_session(cfg: dict) -> dict | None:
    """Try Jellyfin first, then Trakt /watching. Returns session dict or None."""
    has_jf = bool(cfg["jf_api_key"])
    has_trakt_watch = bool(cfg["trakt_username"] or cfg["trakt_access_token"])
    logger.debug(f"_fetch_session: JF={has_jf}, Trakt={has_trakt_watch}")

    session_data = cache.get("jf_session")
    if session_data:
        logger.debug("Session hit: Jellyfin cache")
        return session_data

    if not session_data and has_jf:
        try:
            logger.debug("Querying Jellyfin /Sessions")
            headers = {"Authorization": f"MediaBrowser Token={cfg['jf_api_key']}"}
            res = requests.get(f"{cfg['jf_url']}/Sessions", headers=headers, timeout=5)
            if res.status_code != 200:
                error_msg = f"Jellyfin error: {res.status_code}"
                logger.error(error_msg)
                return {"_error": error_msg}
            active = next((s for s in res.json() if "NowPlayingItem" in s), None)
            if active:
                item = active["NowPlayingItem"]
                session_data = {
                    "series": item.get("SeriesName"), "name": item.get("Name"),
                    "season": item.get("ParentIndexNumber"), "episode": item.get("IndexNumber"),
                    "premiere": item.get("PremiereDate"),
                }
                logger.info(f"Session fetched from Jellyfin: {session_data['series']} S{session_data['season']:02d}E{session_data['episode']:02d}")
                cache.set("jf_session", session_data, expire=10)
        except Exception as e:
            error_msg = f"Failed to reach Jellyfin: {str(e)}"
            logger.error(error_msg)
            return {"_error": error_msg}

    if not session_data and has_trakt_watch:
        session_data = cache.get("trakt_session")
        if session_data:
            logger.debug("Session hit: Trakt cache")
            return session_data
        try:
            logger.debug("Querying Trakt /watching")
            username = cfg["trakt_username"] or "me"
            res = requests.get(f"https://api.trakt.tv/users/{username}/watching", headers=trakt_headers(auth=True), timeout=5)
            if res.status_code == 200:
                data = res.json()
                if data.get("type") == "episode":
                    ep, show = data["episode"], data["show"]
                    session_data = {
                        "series": show["title"], "name": ep["title"],
                        "season": ep["season"], "episode": ep["number"],
                        "premiere": ep.get("first_aired"),
                    }
                    logger.info(f"Session fetched from Trakt: {session_data['series']} S{session_data['season']:02d}E{session_data['episode']:02d}")
                    cache.set("trakt_session", session_data, expire=30)
            elif res.status_code == 204:
                logger.debug("Trakt /watching: 204 No Content")
        except Exception as e:
            logger.warning(f"Failed to query Trakt: {str(e)}")

    return session_data


def _fetch_reddit_data(session_data: dict, cfg: dict) -> tuple[list, str]:
    """Fetch Reddit threads and build the reddit_url. Returns (threads, url)."""
    google_url = f"https://www.google.com/search?q={urllib.parse.quote(session_data['series'] + ' S' + str(session_data['season']).zfill(2) + 'E' + str(session_data['episode']).zfill(2) + ' reddit')}"

    r_cache_key = f"reddit_{session_data['series']}_s{session_data['season']}e{session_data['episode']}"
    cached_r = cache.get(r_cache_key)
    if not isinstance(cached_r, list):
        cached_r = None

    if cached_r is not None:
        logger.debug(f"Reddit hit cache: {len(cached_r)} threads")
        threads = cached_r
    else:
        logger.info(f"Searching Reddit for {session_data['series']} S{session_data['season']:02d}E{session_data['episode']:02d}")
        threads = find_reddit_threads(session_data["series"], session_data["season"], session_data["episode"], n=int(cfg["reddit_max_threads"]))
        logger.info(f"Reddit search returned {len(threads)} thread(s)")
        cache.set(r_cache_key, threads, expire=86400)

    url = threads[0]["url"] if threads else google_url
    return threads, url


def _fetch_trakt_comments(session_data: dict, cfg: dict) -> list[dict]:
    """Fetch and cache Trakt comments for the current episode."""
    if not cfg["trakt_client_id"]:
        logger.debug("Trakt Client ID not configured, skipping comments")
        return []
    try:
        t_cache_key = f"trakt_{session_data['series']}_s{session_data['season']}e{session_data['episode']}"
        cached_t = cache.get(t_cache_key)

        if isinstance(cached_t, list):
            logger.debug(f"Trakt hit cache: {len(cached_t)} comments")
            return [c for c in cached_t if isinstance(c, dict)]

        logger.debug(f"Searching Trakt for {session_data['series']}")
        search_res = requests.get("https://api.trakt.tv/search/show", params={"query": session_data["series"]}, headers=trakt_headers(), timeout=5)
        if search_res.status_code == 401:
            logger.error("Trakt API: 401 Unauthorized - check Client ID")
            return [{"_error": "Trakt error: 401 Unauthorized"}]

        search_json = search_res.json()
        if not search_json:
            logger.warning(f"Trakt search returned no results for {session_data['series']}")
            return []

        slug = search_json[0]["show"]["ids"]["slug"]
        logger.debug(f"Trakt slug resolved: {slug}")
        comments_url = f"https://api.trakt.tv/shows/{slug}/seasons/{session_data['season']}/episodes/{session_data['episode']}/comments/newest"
        comments = requests.get(comments_url, headers=trakt_headers(), timeout=5).json()

        if isinstance(comments, list):
            valid = [c for c in comments if isinstance(c, dict)]
            logger.info(f"Trakt fetched {len(valid)} comment(s)")
            cache.set(t_cache_key, valid, expire=86400)
            return valid
        return []
    except Exception as e:
        logger.error(f"Trakt comments fetch failed: {str(e)}")
        return []


@app.get("/api/sync")
def sync_data():
    """Orchestrate session detection, comment aggregation, and time-machine filtering."""
    logger.debug("Sync request received")
    cfg = get_config()
    has_jf = bool(cfg["jf_api_key"])
    has_trakt_watch = bool(cfg["trakt_username"] or cfg["trakt_access_token"])

    if not has_jf and not has_trakt_watch:
        error_response = {"status": "setup_required", "message": "No session source configured."}
        logger.error(json.dumps(error_response, indent=4))
        return error_response

    session_data = _fetch_session(cfg)
    if isinstance(session_data, dict) and "_error" in session_data:
        error_response = {"status": "error", "message": session_data["_error"]}
        logger.error(json.dumps(error_response, indent=4))
        return error_response
    if not session_data:
        logger.warning("No active session found")
        return {"status": "idle"}

    # Gather data from all sources
    logger.info(f"Session found: {session_data['series']} S{session_data['season']:02d}E{session_data['episode']:02d}")
    reddit_threads, reddit_url = _fetch_reddit_data(session_data, cfg)

    all_comments = []
    bsky_results = find_bluesky_posts(session_data["series"], session_data["season"], session_data["episode"])
    logger.debug(json.dumps(bsky_results, indent=4))
    if isinstance(bsky_results, list):
        all_comments.extend([p for p in bsky_results if isinstance(p, dict)])

    trakt_comments = _fetch_trakt_comments(session_data, cfg)
    if trakt_comments and isinstance(trakt_comments[0], dict) and "_error" in trakt_comments[0]:
        return {"status": "error", "message": trakt_comments[0]["_error"]}
    all_comments.extend(trakt_comments)

    # Sort by date
    all_comments.sort(
        key=lambda x: x.get("created_at") if (isinstance(x, dict) and x.get("created_at")) else "",
        reverse=True,
    )

    # Time machine filter
    time_machine = []
    if session_data.get("premiere"):
        try:
            p_date = datetime.fromisoformat(session_data["premiere"].replace("Z", "+00:00"))
            time_machine = [
                c for c in all_comments
                if isinstance(c, dict) and c.get("created_at") and
                p_date <= datetime.fromisoformat(c["created_at"].replace("Z", "+00:00")) <= p_date + timedelta(days=14)
            ]
        except Exception:
            pass

    return {
        "status": "success",
        "title": f"{session_data['series']} - {session_data['name']}",
        "metadata": session_data,
        "all_comments": all_comments,
        "time_machine": time_machine,
        "reddit_url": reddit_url,
        "reddit_thread_found": bool(reddit_threads),
        "reddit_threads": reddit_threads,
    }

@app.get("/api/status")
def get_status():
    """Return current configuration status and data source indicators."""
    cfg = get_config()
    stored = cache.get("ui_config") or {}
    def source(key):
        return "env" if os.environ.get(ENV_MAP[key][0]) else ("stored" if stored.get(key) else "none")
    return {
        "jellyfin_configured": bool(cfg["jf_api_key"]),
        "trakt_configured": bool(cfg["trakt_client_id"]),
        "trakt_watch_configured": bool(cfg["trakt_username"] or cfg["trakt_access_token"]),
        "jf_url": cfg["jf_url"],
        "reddit_auto_open": bool(cfg["reddit_auto_open"]),
        "sources": {k: source(k) for k in ENV_MAP},
    }

def _mask(val: str, key: str) -> str:
    """Mask sensitive configuration values (secrets) for UI display."""
    if not val or key not in SECRET_KEYS:
        return val
    if len(val) <= 8:
        return "****"
    return f"{val[:4]}****{val[-4:]}"

@app.get("/api/config")
def get_config_endpoint():
    """Return all config keys with masked values, sources, and metadata."""
    cfg = get_config()
    stored = cache.get("ui_config") or {}
    result = {}
    for key, (env_name, default) in ENV_MAP.items():
        env_val = os.environ.get(env_name, "")
        stored_val = stored.get(key, "")
        
        source = "default"
        if stored_val:
            source = "stored"
        elif env_val:
            source = "env"
            
        result[key] = {
            "effective_value": _mask(cfg[key], key),
            "env_value": _mask(env_val, key),
            "stored_value": _mask(stored_val, key),
            "is_env_set": bool(env_val),
            "is_stored_set": bool(stored_val),
            "source": source,
            "is_secret": key in SECRET_KEYS
        }
    return result

@app.put("/api/config")
async def save_config(request: Request):
    """Save UI configuration overrides."""
    logger.debug(f"Config update request received with {len(await request.json())} fields")
    body = await request.json()
    stored = cache.get("ui_config")
    if stored is None:
        stored = {}

    for key in ENV_MAP:
        if key in body:
            val = str(body[key]).strip()
            if len(val) > 500:
                logger.warning(f"Rejected oversized value for {key}")
                continue  # Silently reject oversized values
            if val:
                stored[key] = val
                logger.debug(f"Updated config key: {key}")
            else:
                if key in stored:
                    del stored[key]
                    logger.debug(f"Cleared config key: {key}")

    cache.set("ui_config", stored)
    logger.info("Configuration saved successfully")
    return {"status": "ok"}

@app.post("/api/cache/clear")
def clear_cache():
    """Clear all cached data while preserving UI configuration."""
    try:
        logger.info("Cache clear requested")
        # 1. Capture the config so we don't log the user out of their settings
        ui_config = cache.get("ui_config")

        # 2. Completely evict everything else
        cache.clear()

        # 3. Restore the config
        if ui_config:
            cache.set("ui_config", ui_config)

        # 4. Set the webhook time to 'now' so the UI knows to refresh via SSE
        cache.set("last_webhook_time", time.time())

        logger.info("Cache cleared successfully")
        return {"status": "ok"}
    except Exception as e:
        logger.error(f"Cache clear failed: {str(e)}")
        return {"status": "error", "message": str(e)}

@app.post("/api/restart")
async def restart_server(request: Request):
    body = await request.json() if request.headers.get("content-type") == "application/json" else {}
    if not body.get("confirm"):
        logger.warning("Restart requested without confirmation")
        return {"status": "error", "message": "Send {\"confirm\": true} to restart."}

    logger.warning("Server restart initiated by user")

    async def _do_exit():
        await asyncio.sleep(0.3)
        os._exit(0)

    asyncio.create_task(_do_exit())
    return {"status": "restarting"}

@app.post("/api/webhook")
async def jellyfin_webhook(request: Request):
    """Receive Jellyfin webhook events. Optionally secured via WEBHOOK_SECRET."""
    try:
        cfg = get_config()
        secret = cfg.get("webhook_secret", "")
        if secret and request.headers.get("X-Webhook-Secret") != secret:
            logger.warning("Webhook rejected: invalid secret")
            return {"status": "unauthorized"}
        payload = await request.json()
        event_type = payload.get("NotificationType")
        if event_type in ["PlaybackStart", "PlaybackStop"]:
            logger.info(f"Jellyfin webhook: {event_type}")
            cache.delete("jf_session")
            cache.set("last_webhook_time", time.time())
        else:
            logger.debug(f"Jellyfin webhook ignored: {event_type}")
        return {"status": "received"}
    except Exception as e:
        logger.error(f"Webhook processing failed: {str(e)}")
        return {"status": "error"}

@app.get("/api/stream")
async def sse_stream():
    """Server-Sent Events endpoint for real-time UI refresh."""
    async def event_generator():
        last_time = cache.get("last_webhook_time", 0)
        try:
            while True:
                await asyncio.sleep(2)
                current_time = cache.get("last_webhook_time", 0)
                if current_time > last_time:
                    yield "data: refresh\n\n"
                    last_time = current_time
        except asyncio.CancelledError:
            pass
        except Exception:
            pass
    return StreamingResponse(event_generator(), media_type="text/event-stream")

DATA_DIR = os.environ.get("DATA_DIR", "./data")
STATIC_DIR = os.environ.get("STATIC_DIR", os.path.join(DATA_DIR, "static"))
os.makedirs(STATIC_DIR, exist_ok=True)
app.mount("/", StaticFiles(directory=STATIC_DIR, html=True), name="static")