import os
import re
import hmac
import hashlib
import urllib.parse
import requests
import asyncio
import time
import logging
import json
from datetime import datetime, timedelta, timezone
from contextlib import asynccontextmanager
from concurrent.futures import ThreadPoolExecutor

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
    "reddit_max_comments": ("REDDIT_MAX_COMMENTS", "250"),
    "bsky_identifier":     ("BSKY_IDENTIFIER",     ""),
    "bsky_app_password":   ("BSKY_APP_PASSWORD",   ""),
    "webhook_secret":      ("WEBHOOK_SECRET",      ""),
    "theme_mode":          ("THEME_MODE",          "dark"),
    "time_machine_days":   ("TIME_MACHINE_DAYS",   "14"),
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

def _get_show_subreddits(series: str) -> list[str]:
    """Return subreddit names to search for a show, combining overrides with heuristic derivation."""
    overrides = cache.get("subreddit_overrides") or {}
    override = overrides.get(series.lower())
    base = re.sub(r"[^a-z0-9]", "", series.lower())
    subs = []
    if override:
        subs.append(override.lower())
    if base not in subs:
        subs.append(base)
    return subs


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
            sub = re.sub(r"[^a-z0-9]", "", p.get("subreddit", "").lower())
            show_subs = set(_get_show_subreddits(series))
            return (0 if sub in show_subs else 1, -p.get("score", 0))

        top = sorted(episode_posts, key=rank)[:n]
        results = []
        for p in top:
            permalink = p.get("permalink", "")
            if not permalink.startswith("/r/"):
                continue
            results.append({
                "url": f"https://www.reddit.com{permalink}",
                "title": p.get("title", ""),
                "subreddit": p.get("subreddit", ""),
                "score": p.get("score", 0),
            })
        return results
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

def _extract_bsky_images(embed: dict) -> list[dict]:
    """Recursively extract image URLs and alt text from a Bluesky post's embed object."""
    imgs = []
    t = embed.get("$type")
    if t == "app.bsky.embed.images#view":
        for img in embed.get("images", []):
            if img.get("thumb"):
                imgs.append({"url": img["thumb"], "alt": img.get("alt", "")})
    elif t == "app.bsky.embed.external#view":
        thumb = embed.get("external", {}).get("thumb")
        if thumb:
            imgs.append({"url": thumb, "alt": ""})
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


def _ensure_comment_ids(comments: list[dict]) -> None:
    """Assign stable IDs to any comments/replies missing one, derived from content hash."""
    for c in comments:
        if not c.get("id"):
            key = f"{c.get('source', '')}:{c.get('comment', '')}:{c.get('created_at', '')}"
            c["id"] = f"wb_{hashlib.sha256(key.encode()).hexdigest()[:12]}"
        if isinstance(c.get("replies"), list):
            _ensure_comment_ids(c["replies"])


def find_pullpush_comments(series: str, season: int, episode: int, max_threads: int = 3, max_comments: int = 250) -> list[dict]:
    """Fetch Reddit comments via PullPush.io for the given episode, grouped by thread."""
    s_long, e_long = str(season).zfill(2), str(episode).zfill(2)

    try:
        # Build search queries: title searches with show name (padded + unpadded episode format),
        # plus subreddit-targeted searches for threads that omit the show name from the title
        show_subs = _get_show_subreddits(series)
        searches = [
            {"title": f"{series} S{season}E{episode}"},
            {"title": f"{series} S{s_long}E{e_long}"},
        ]
        for sub_name in show_subs:
            searches.append({"title": f"S{season}E{episode}", "subreddit": sub_name})
            searches.append({"title": f"S{s_long}E{e_long}", "subreddit": sub_name})
        seen_ids = {}
        for params in searches:
            search_params = {**params, "size": 25, "sort_type": "score", "sort": "desc"}
            logger.debug(f"PullPush: searching submissions {params}")
            try:
                sub_resp = requests.get(
                    "https://api.pullpush.io/reddit/search/submission/",
                    params=search_params,
                    headers={"User-Agent": "WatchBack/1.0"},
                    timeout=5,
                )
                if sub_resp.status_code != 200:
                    logger.warning(f"PullPush submission search failed: {sub_resp.status_code}")
                    continue
                for sub in sub_resp.json().get("data", []):
                    sid = sub.get("id", "")
                    if sid and sid not in seen_ids:
                        seen_ids[sid] = sub
            except Exception as e:
                logger.warning(f"PullPush submission search exception: {e}")
                continue

        # Keep submissions that reference this episode; for non-subreddit results also
        # verify the series name appears in the title to avoid false positives
        series_lower = series.lower()
        show_sub_set = set(show_subs)
        submissions = []
        for sub in seen_ids.values():
            title = sub.get("title", "")
            if not _matches_episode(title, season, episode):
                continue
            sub_name = re.sub(r"[^a-z0-9]", "", sub.get("subreddit", "").lower())
            if sub_name in show_sub_set or series_lower in title.lower():
                submissions.append(sub)
        submissions.sort(key=lambda s: s.get("score", 0), reverse=True)

        if not submissions:
            logger.debug("PullPush: no matching submissions found")
            return []
        logger.info(f"PullPush: found {len(submissions)} matching submission(s)")

        all_results = []
        # Belt-and-suspenders: score-sort above should give best threads first, but cap locally
        for sub in submissions[:max_threads]:
            sub_id = sub.get("id", "")
            thread_title = sub.get("title", "")
            thread_sub = sub.get("subreddit", "")
            safe_sub = urllib.parse.quote(thread_sub, safe="")
            safe_sub_id = urllib.parse.quote(sub_id, safe="")
            thread_url = f"https://www.reddit.com/r/{safe_sub}/comments/{safe_sub_id}/"

            logger.debug(f"PullPush: fetching comments for thread {sub_id}")
            try:
                com_resp = requests.get(
                    "https://api.pullpush.io/reddit/search/comment/",
                    params={"link_id": sub_id, "size": max_comments, "sort_type": "score", "sort": "desc"},
                    headers={"User-Agent": "WatchBack/1.0"},
                    timeout=5,
                )
                if com_resp.status_code != 200:
                    logger.warning(f"PullPush comment fetch failed for {sub_id}: {com_resp.status_code}")
                    continue
                raw_comments = com_resp.json().get("data", [])
            except Exception as e:
                logger.warning(f"PullPush comment fetch exception for {sub_id}: {e}")
                continue

            if not raw_comments:
                continue

            # Drop deleted/removed comments before building the reply tree
            raw_comments = [c for c in raw_comments if c.get("body") not in ("[deleted]", "[removed]")]

            # Keyed by comment ID so replies can be attached to parents in a single pass
            by_id = {}
            for c in raw_comments:
                cid = c.get("id", "")
                safe_cid = urllib.parse.quote(cid, safe="")
                # PullPush returns created_utc as int for recent posts but string for older ones
                try:
                    created_ts = float(c.get("created_utc", 0))
                except (TypeError, ValueError):
                    created_ts = 0.0
                by_id[cid] = {
                    "id": cid,
                    "comment": c.get("body", ""),
                    "user": {"username": c.get("author", "[deleted]")},
                    "created_at": datetime.fromtimestamp(created_ts, tz=timezone.utc).isoformat().replace("+00:00", "Z"),
                    "score": c.get("score", 0),
                    "url": f"https://www.reddit.com/r/{safe_sub}/comments/{safe_sub_id}/_/{safe_cid}/",
                    "source": "reddit",
                    "thread_title": thread_title,
                    "thread_url": thread_url,
                    "parent_id": c.get("parent_id", ""),
                    "replies": [],
                }

            top_level = []
            for cid, comment in by_id.items():
                raw_parent = comment.pop("parent_id")
                # t3_ prefix = parent is the submission (top-level), t1_ = parent is another comment
                if raw_parent.startswith("t1_"):
                    parent_cid = raw_parent[3:]
                    if parent_cid in by_id:
                        by_id[parent_cid]["replies"].append(comment)
                    # else: parent not fetched — drop orphaned reply rather than surface it out of context
                else:
                    top_level.append(comment)

            top_level.sort(key=lambda c: c.get("score", 0), reverse=True)
            for c in top_level:
                c["replies"].sort(key=lambda r: r.get("score", 0), reverse=True)

            all_results.extend(top_level)

        logger.info(f"PullPush: returning {len(all_results)} top-level comment(s)")
        return all_results

    except Exception as e:
        logger.error(f"PullPush search exception: {e}")
        return []


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
            dedupe_key = (norm_text, frozenset(img["url"] for img in images))
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
                "url": "https://bsky.app/profile/{}/post/{}".format(
                    urllib.parse.quote(p["author"]["handle"], safe=""),
                    urllib.parse.quote(p["uri"].split("/")[-1], safe=""),
                ),
                "score": p.get("likeCount", 0),
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

        slug = urllib.parse.quote(search_json[0]["show"]["ids"]["slug"], safe="")
        logger.debug(f"Trakt slug resolved: {slug}")
        comments_url = f"https://api.trakt.tv/shows/{slug}/seasons/{session_data['season']}/episodes/{session_data['episode']}/comments/newest"
        comments = requests.get(comments_url, headers=trakt_headers(), timeout=5).json()

        if isinstance(comments, list):
            valid = [c for c in comments if isinstance(c, dict)]
            for c in valid:
                if "likes" in c and "score" not in c:
                    c["score"] = c["likes"]
            logger.info(f"Trakt fetched {len(valid)} comment(s)")
            cache.set(t_cache_key, valid, expire=86400)
            return valid
        return []
    except Exception as e:
        logger.error(f"Trakt comments fetch failed: {str(e)}")
        return []


def _fetch_pullpush_data(session_data: dict, cfg: dict) -> list[dict]:
    """Fetch and cache PullPush Reddit comments for the current episode."""
    pp_cache_key = f"pullpush_{session_data['series']}_s{session_data['season']}e{session_data['episode']}"
    cached_pp = cache.get(pp_cache_key)
    if isinstance(cached_pp, list):
        logger.debug(f"PullPush: cache hit ({len(cached_pp)} comments)")
        return cached_pp
    pp_results = find_pullpush_comments(
        session_data["series"], session_data["season"], session_data["episode"],
        max_threads=int(cfg["reddit_max_threads"]),
        max_comments=int(cfg.get("reddit_max_comments") or 250),
    )
    if pp_results:
        cache.set(pp_cache_key, pp_results, expire=86400)
    return pp_results


def _fetch_bluesky_data(session_data: dict) -> list[dict]:
    """Fetch Bluesky posts for the current episode."""
    bsky_results = find_bluesky_posts(
        session_data["series"], session_data["season"], session_data["episode"],
    )
    if isinstance(bsky_results, list):
        return [p for p in bsky_results if isinstance(p, dict)]
    return []


@app.get("/api/sync")
def sync_data():
    """Orchestrate session detection, comment aggregation, and time-machine filtering."""
    logger.debug("Sync request received")
    cfg = get_config()
    has_jf = bool(cfg["jf_api_key"])
    has_trakt_watch = bool(cfg["trakt_username"] or cfg["trakt_access_token"])

    if not has_jf and not has_trakt_watch:
        logger.error("No session source configured")
        return {"status": "setup_required", "message": "No session source configured."}

    session_data = _fetch_session(cfg)
    if isinstance(session_data, dict) and "_error" in session_data:
        logger.error(f"Session error: {session_data['_error']}")
        return {"status": "error", "message": session_data["_error"]}
    if not session_data:
        logger.warning("No active session found")
        return {"status": "idle"}

    logger.info(f"Session found: {session_data['series']} S{session_data['season']:02d}E{session_data['episode']:02d}")

    # Fetch all data sources in parallel since they're independent
    with ThreadPoolExecutor(max_workers=4) as executor:
        reddit_future = executor.submit(_fetch_reddit_data, session_data, cfg)
        pp_future = executor.submit(_fetch_pullpush_data, session_data, cfg)
        bsky_future = executor.submit(_fetch_bluesky_data, session_data)
        trakt_future = executor.submit(_fetch_trakt_comments, session_data, cfg)

        reddit_threads, reddit_url = reddit_future.result()
        pp_results = pp_future.result()
        bsky_results = bsky_future.result()
        trakt_comments = trakt_future.result()

    if trakt_comments and isinstance(trakt_comments[0], dict) and "_error" in trakt_comments[0]:
        return {"status": "error", "message": trakt_comments[0]["_error"]}

    all_comments = pp_results + bsky_results + trakt_comments
    _ensure_comment_ids(all_comments)

    all_comments.sort(
        key=lambda x: x.get("created_at") if (isinstance(x, dict) and x.get("created_at")) else "",
        reverse=True,
    )

    tm_days = int(cfg.get("time_machine_days", 14))
    time_machine = []
    if session_data.get("premiere"):
        try:
            p_date = datetime.fromisoformat(session_data["premiere"].replace("Z", "+00:00"))
            time_machine = [
                c for c in all_comments
                if isinstance(c, dict) and c.get("created_at") and
                p_date <= datetime.fromisoformat(c["created_at"].replace("Z", "+00:00")) <= p_date + timedelta(days=tm_days)
            ]
        except Exception:
            pass

    return {
        "status": "success",
        "title": f"{session_data['series']} - {session_data['name']}",
        "metadata": session_data,
        "all_comments": all_comments,
        "time_machine": time_machine,
        "time_machine_days": tm_days,
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

@app.get("/api/test/{service}")
def test_service(service: str):
    """Test connectivity to an integration. Returns {ok, message}."""
    cfg = get_config()

    if service == "jellyfin":
        if not cfg["jf_api_key"]:
            return {"ok": False, "message": "No API key configured"}
        try:
            headers = {"Authorization": f"MediaBrowser Token={cfg['jf_api_key']}"}
            res = requests.get(f"{cfg['jf_url']}/System/Info/Public", headers=headers, timeout=5)
            if res.status_code == 200:
                info = res.json()
                return {"ok": True, "message": f"Connected to {info.get('ServerName', 'Jellyfin')} v{info.get('Version', '?')}"}
            return {"ok": False, "message": f"HTTP {res.status_code}"}
        except Exception as e:
            return {"ok": False, "message": str(e)}

    elif service == "trakt":
        if not cfg["trakt_client_id"]:
            return {"ok": False, "message": "No Client ID configured"}
        try:
            res = requests.get("https://api.trakt.tv/shows/trending?limit=1", headers=trakt_headers(), timeout=5)
            if res.status_code == 200:
                return {"ok": True, "message": "API key valid"}
            return {"ok": False, "message": f"HTTP {res.status_code}"}
        except Exception as e:
            return {"ok": False, "message": str(e)}

    elif service == "trakt-watch":
        if not (cfg["trakt_username"] or cfg["trakt_access_token"]):
            return {"ok": False, "message": "No username or access token configured"}
        try:
            username = cfg["trakt_username"] or "me"
            res = requests.get(f"https://api.trakt.tv/users/{username}/watching", headers=trakt_headers(auth=True), timeout=5)
            if res.status_code in (200, 204):
                watching = "currently watching" if res.status_code == 200 else "idle"
                return {"ok": True, "message": f"Profile reachable ({watching})"}
            if res.status_code == 401:
                return {"ok": False, "message": "Unauthorized — check access token"}
            return {"ok": False, "message": f"HTTP {res.status_code}"}
        except Exception as e:
            return {"ok": False, "message": str(e)}

    elif service == "bluesky":
        if not cfg["bsky_identifier"] or not cfg["bsky_app_password"]:
            return {"ok": False, "message": "Handle and app password both required"}
        try:
            res = requests.post(
                "https://bsky.social/xrpc/com.atproto.server.createSession",
                json={"identifier": cfg["bsky_identifier"], "password": cfg["bsky_app_password"]},
                timeout=5,
            )
            if res.status_code == 200:
                handle = res.json().get("handle", cfg["bsky_identifier"])
                return {"ok": True, "message": f"Authenticated as @{handle}"}
            return {"ok": False, "message": f"Auth failed (HTTP {res.status_code})"}
        except Exception as e:
            return {"ok": False, "message": str(e)}

    elif service == "reddit":
        try:
            res = requests.get(
                "https://www.reddit.com/search.json?q=test&limit=1",
                headers={"User-Agent": "WatchBack/1.0"},
                timeout=5,
            )
            if res.status_code == 200:
                return {"ok": True, "message": "Reddit API reachable"}
            return {"ok": False, "message": f"HTTP {res.status_code}"}
        except Exception as e:
            return {"ok": False, "message": str(e)}

    return {"ok": False, "message": f"Unknown service: {service}"}

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

@app.get("/api/subreddit-overrides")
def get_subreddit_overrides():
    """Return all series-to-subreddit overrides."""
    return cache.get("subreddit_overrides") or {}

@app.put("/api/subreddit-overrides")
async def set_subreddit_override(request: Request):
    """Set a subreddit override for a series. Body: {"series": "...", "subreddit": "..."}"""
    body = await request.json()
    series = str(body.get("series", "")).strip()
    subreddit = str(body.get("subreddit", "")).strip()
    if not series:
        return {"status": "error", "message": "series is required"}
    overrides = cache.get("subreddit_overrides") or {}
    if subreddit:
        overrides[series.lower()] = subreddit
        logger.info(f"Subreddit override set: {series!r} -> r/{subreddit}")
    else:
        overrides.pop(series.lower(), None)
        logger.info(f"Subreddit override removed: {series!r}")
    cache.set("subreddit_overrides", overrides)
    return {"status": "ok"}

@app.post("/api/cache/clear")
def clear_cache():
    """Clear all cached data while preserving UI configuration and subreddit overrides."""
    try:
        logger.info("Cache clear requested")
        # Capture persistent settings so we don't lose them
        ui_config = cache.get("ui_config")
        sub_overrides = cache.get("subreddit_overrides")

        cache.clear()

        if ui_config:
            cache.set("ui_config", ui_config)
        if sub_overrides:
            cache.set("subreddit_overrides", sub_overrides)

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
        if secret and not hmac.compare_digest(request.headers.get("X-Webhook-Secret", ""), secret):
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