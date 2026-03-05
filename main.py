import os
import re
import urllib.parse
import requests
import asyncio
import time
from datetime import datetime, timedelta
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import StreamingResponse
from fastapi.staticfiles import StaticFiles
from fastapi.middleware.cors import CORSMiddleware
from diskcache import Cache

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
}

SECRET_KEYS = {"jf_api_key", "trakt_client_id", "trakt_access_token", "bsky_app_password"}

def get_config() -> dict:
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
    cfg = get_config()
    has_trakt_watch = bool(cfg["trakt_username"] or cfg["trakt_access_token"])
    has_jellyfin = bool(cfg["jf_api_key"])
    poller_task = None
    if has_trakt_watch and not has_jellyfin:
        poller_task = asyncio.create_task(trakt_watch_poller())
    yield
    if poller_task: poller_task.cancel()
    cache.close()

app = FastAPI(title="WatchBack API", lifespan=lifespan)
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])

# --- Helpers ---

def _matches_episode(title: str, season: int, episode: int) -> bool:
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
    cfg = get_config()
    h = {"trakt-api-key": cfg["trakt_client_id"], "trakt-api-version": "2"}
    if auth and cfg["trakt_access_token"]: h["Authorization"] = f"Bearer {cfg['trakt_access_token']}"
    return h

def find_reddit_threads(series: str, season: int, episode: int, n: int = 3) -> list[dict]:
    query = f'"{series}" S{season:02d}E{episode:02d} discussion'
    try:
        resp = requests.get("https://www.reddit.com/search.json", params={"q": query, "sort": "relevance", "limit": 25}, headers={"User-Agent": "WatchBack/1.0"}, timeout=5)
        if resp.status_code != 200: return []
        posts = [p["data"] for p in resp.json()["data"]["children"]]
        episode_posts = [p for p in posts if _matches_episode(p.get("title", ""), season, episode)]
        def rank(p):
            sub = p.get("subreddit", "").lower()
            base = re.sub(r"[^a-z0-9]", "", series.lower())
            return (0 if sub in {base, base+"tv"} else 1, -p.get("score", 0))
        top = sorted(episode_posts, key=rank)[:n]
        return [{"url": f"https://www.reddit.com{p['permalink']}", "title": p.get("title", ""), "subreddit": p.get("subreddit", ""), "score": p.get("score", 0)} for p in top]
    except Exception: return []

def bsky_access_token() -> str | None:
    cached = cache.get("bsky_token")
    if cached:
        return cached
    cfg = get_config()
    if not cfg["bsky_identifier"] or not cfg["bsky_app_password"]:
        return None
    try:
        resp = requests.post(
            "https://bsky.social/xrpc/com.atproto.server.createSession",
            json={"identifier": cfg["bsky_identifier"], "password": cfg["bsky_app_password"]},
            timeout=5
        )
        if resp.status_code != 200:
            print(f"[bsky] auth failed: {resp.status_code} {resp.text[:200]}", flush=True)
            return None
        token = resp.json().get("accessJwt")
        if token:
            cache.set("bsky_token", token, expire=5400)  # 90 min (tokens last ~2h)
        return token
    except Exception as e:
        print(f"[bsky] auth exception: {e}", flush=True)
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
                print(f"[trakt-poller] Session changed: {last_id} -> {current_id}", flush=True)
                cache.delete("jf_session")
                cache.delete("trakt_session")
                cache.set("last_webhook_time", time.time())
                last_id = current_id

        except Exception as e:
            print(f"[trakt-poller] Error: {e}", flush=True)
        
        await asyncio.sleep(30)

def find_bluesky_posts(series: str, season: int, episode: int, n: int = 10) -> list[dict]:
    s_long = str(season).zfill(2)
    e_long = str(episode).zfill(2)
    query = f"{series} S{s_long}E{e_long}"
    token = bsky_access_token()
    headers = {
        "User-Agent": "WatchBack/1.0"
    }
    # Use api.bsky.app for authenticated, public.api.bsky.app for unauthenticated
    url = "https://api.bsky.app/xrpc/app.bsky.feed.searchPosts" if token else "https://public.api.bsky.app/xrpc/app.bsky.feed.searchPosts"
    if token:
        headers["Authorization"] = f"Bearer {token}"

    try:
        resp = requests.get(
            url,
            params={"q": query, "sort": "latest", "limit": n},
            headers=headers,
            timeout=5
        )
        if resp.status_code == 403:
            print(f"[bsky] 403 Forbidden: API may be blocking the request. User-Agent: {headers.get('User-Agent')}", flush=True)
            return []
        if resp.status_code != 200:
            print(f"[bsky] error body: {resp.text[:200]}", flush=True)
            return []
        posts = resp.json().get("posts", [])
        print(f"[bsky] got {len(posts)} posts", flush=True)
        results = []
        seen_normalized = set()
        
        # Build set of "boring" words to help filter bot posts
        boring = {series.lower(), f"s{s_long}e{e_long}".lower(), f"s{season}e{episode}".lower(), "halt and catch fire", "playing"}

        for p in posts:
            text = p.get("record", {}).get("text", "")
            if not text:
                continue

            clean_text = text.strip()
            norm_text = " ".join(clean_text.lower().split())

            # 1. Media Detection
            images = []
            embed = p.get("embed", {})
            e_type = embed.get("$type")
            
            def extract_images(emb):
                imgs = []
                t = emb.get("$type")
                if t == "app.bsky.embed.images#view":
                    for img in emb.get("images", []):
                        if img.get("thumb"): imgs.append(img.get("thumb"))
                elif t == "app.bsky.embed.external#view":
                    if emb.get("external", {}).get("thumb"):
                        imgs.append(emb["external"]["thumb"])
                elif t == "app.bsky.embed.recordWithMedia#view":
                    # Recurse into the media part
                    imgs.extend(extract_images(emb.get("media", {})))
                return imgs

            images = extract_images(embed)

            # 2. Fuzzy Deduplication
            # Skip only if BOTH the normalized text and the exact set of images matches.
            dedupe_key = (norm_text, frozenset(images))
            if dedupe_key in seen_normalized:
                continue
            seen_normalized.add(dedupe_key)

            # 3. Bot/Low-Content Filtering:
            # We want to remove ALL mentions of the show/episode to see what's left.
            test_content = norm_text
            # More aggressive "boring" patterns
            boring_patterns = [
                series.lower(),
                f"s{s_long}e{e_long}".lower(),
                f"s{season}e{episode}".lower(),
                f"{season}x{e_long}".lower(),
                f"{season}x{episode}".lower(),
                "playing",
                "watch along",
                "watching"
            ]
            for pat in boring_patterns:
                test_content = test_content.replace(pat, "")
            
            # Strip punctuation and common "joiners" for a cleaner count
            for char in "[]().,-_#":
                test_content = test_content.replace(char, "")
            
            filtered_text = test_content.strip()
            is_low_text = len(filtered_text) < 5 and len(norm_text) < 120
            
            if is_low_text and not images:
                continue

            results.append({
                "id": p["cid"],
                "comment": text,
                "user": {"username": p["author"]["handle"]},
                "created_at": p.get("record", {}).get("createdAt"),
                "url": f"https://bsky.app/profile/{p['author']['handle']}/post/{p['uri'].split('/')[-1]}",
                "images": images,
                "source": "bluesky"
            })
        return results
    except Exception as e:
        print(f"[bsky] exception: {e}", flush=True)
        return []

# --- Endpoints ---

@app.get("/api/sync")
def sync_data():
    cfg = get_config()
    has_jf = bool(cfg["jf_api_key"])
    has_trakt_watch = bool(cfg["trakt_username"] or cfg["trakt_access_token"])
    
    if not has_jf and not has_trakt_watch:
        return {"status": "setup_required", "message": "No session source configured."}

    session_data = cache.get("jf_session")
    
    if not session_data and has_jf:
        try:
            headers = {"Authorization": f"MediaBrowser Token={cfg['jf_api_key']}"}
            res = requests.get(f"{cfg['jf_url']}/Sessions", headers=headers, timeout=5)
            if res.status_code != 200: return {"status": "error", "message": f"Jellyfin error: {res.status_code}"}
            active = next((s for s in res.json() if "NowPlayingItem" in s), None)
            if active:
                item = active["NowPlayingItem"]
                session_data = {"series": item.get("SeriesName"), "name": item.get("Name"), "season": item.get("ParentIndexNumber"), "episode": item.get("IndexNumber"), "premiere": item.get("PremiereDate")}
                cache.set("jf_session", session_data, expire=10)
        except Exception as e:
            return {"status": "error", "message": f"Failed to reach Jellyfin: {str(e)}"}

    if not session_data and has_trakt_watch:
        session_data = cache.get("trakt_session")
        if not session_data:
            try:
                username = cfg["trakt_username"] or "me"
                res = requests.get(f"https://api.trakt.tv/users/{username}/watching", headers=trakt_headers(auth=True), timeout=5)
                if res.status_code == 200:
                    data = res.json()
                    if data.get("type") == "episode":
                        ep = data["episode"]
                        show = data["show"]
                        session_data = {
                            "series": show["title"],
                            "name": ep["title"],
                            "season": ep["season"],
                            "episode": ep["number"],
                            "premiere": ep.get("first_aired")
                        }
                        cache.set("trakt_session", session_data, expire=30)
            except Exception:
                pass

    if not session_data: return {"status": "idle"}

    # Reddit Logic
    reddit_threads = []
    google_url = f"https://www.google.com/search?q={urllib.parse.quote(session_data['series'] + ' S' + str(session_data['season']).zfill(2) + 'E' + str(session_data['episode']).zfill(2) + ' reddit')}"
    
    r_cache_key = f"reddit_{session_data['series']}_s{session_data['season']}e{session_data['episode']}"
    cached_r = cache.get(r_cache_key)
    if not isinstance(cached_r, list): cached_r = None
    
    if cached_r is not None:
        reddit_threads = cached_r
    else:
        reddit_threads = find_reddit_threads(session_data["series"], session_data["season"], session_data["episode"], n=int(cfg["reddit_max_threads"]))
        cache.set(r_cache_key, reddit_threads, expire=86400)
    
    reddit_url = reddit_threads[0]["url"] if reddit_threads else google_url
    
    # 1. Gather BlueSky Posts (Validation included)
    all_comments = []
    bsky_results = find_bluesky_posts(session_data["series"], session_data["season"], session_data["episode"])
    if isinstance(bsky_results, list):
        all_comments.extend([p for p in bsky_results if isinstance(p, dict)])
    
    # 2. Gather Trakt Comments
    if cfg["trakt_client_id"]:
        try:
            t_cache_key = f"trakt_{session_data['series']}_s{session_data['season']}e{session_data['episode']}"
            cached_t = cache.get(t_cache_key)
            
            if isinstance(cached_t, list):
                all_comments.extend([c for c in cached_t if isinstance(c, dict)])
            else:
                search_res = requests.get("https://api.trakt.tv/search/show", params={"query": session_data["series"]}, headers=trakt_headers(), timeout=5)
                if search_res.status_code == 401: return {"status": "error", "message": "Trakt error: 401 Unauthorized"}
                
                search_json = search_res.json()
                if search_json:
                    slug = search_json[0]["show"]["ids"]["slug"]
                    comments_url = f"https://api.trakt.tv/shows/{slug}/seasons/{session_data['season']}/episodes/{session_data['episode']}/comments/newest"
                    comments = requests.get(comments_url, headers=trakt_headers(), timeout=5).json()
                    
                    if isinstance(comments, list):
                        valid_comments = [c for c in comments if isinstance(c, dict)]
                        all_comments.extend(valid_comments)
                        cache.set(t_cache_key, valid_comments, expire=86400)
        except Exception: 
            pass

    # CRASH-PROOF SORT: Ensure we only attempt .get() on dicts
    all_comments.sort(
        key=lambda x: x.get("created_at") if (isinstance(x, dict) and x.get("created_at")) else "", 
        reverse=True
    )
    
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
        "reddit_threads": reddit_threads
    }

@app.get("/api/status")
def get_status():
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
    if not val or key not in SECRET_KEYS:
        return val
    if len(val) <= 8:
        return "****"
    return f"{val[:4]}****{val[-4:]}"

@app.get("/api/config")
def get_config_endpoint():
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
    body = await request.json()
    # Pull current config or initialize empty dict
    stored = cache.get("ui_config")
    if stored is None:
        stored = {}
        
    for key in ENV_MAP:
        if key in body:
            val = str(body[key]).strip()
            if val:
                stored[key] = val
            else:
                # This fixes "test_empty_value_removes_key"
                if key in stored:
                    del stored[key]
    
    # Save back to cache
    cache.set("ui_config", stored)
    return {"status": "ok"}

@app.post("/api/cache/clear")
def clear_cache():
    try:
        # 1. Capture the config so we don't log the user out of their settings
        ui_config = cache.get("ui_config")
        
        # 2. Completely evict everything else
        cache.clear()
        
        # 3. Restore the config
        if ui_config:
            cache.set("ui_config", ui_config)
            
        # 4. Set the webhook time to 'now' so the UI knows to refresh via SSE
        cache.set("last_webhook_time", time.time())
        
        return {"status": "ok"}
    except Exception as e:
        return {"status": "error", "message": str(e)}

@app.post("/api/restart")
async def restart_server():
    async def _do_exit():
        await asyncio.sleep(0.3)
        os._exit(0)
    asyncio.create_task(_do_exit())
    return {"status": "restarting"}

@app.post("/api/webhook")
async def jellyfin_webhook(request: Request):
    try:
        payload = await request.json()
        if payload.get("NotificationType") in ["PlaybackStart", "PlaybackStop"]:
            cache.delete("jf_session")
            cache.set("last_webhook_time", time.time())
        return {"status": "received"}
    except Exception: return {"status": "error"}

@app.get("/api/stream")
async def sse_stream():
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

STATIC_DIR = os.environ.get("STATIC_DIR", os.path.join(os.path.dirname(__file__), "static"))
os.makedirs(STATIC_DIR, exist_ok=True)
app.mount("/", StaticFiles(directory=STATIC_DIR, html=True), name="static")