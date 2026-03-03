import os
import urllib.parse
from datetime import datetime, timedelta
import requests
import asyncio
import time
from fastapi import FastAPI, Request
from fastapi.responses import StreamingResponse
from fastapi.staticfiles import StaticFiles
from fastapi.middleware.cors import CORSMiddleware
from diskcache import Cache

app = FastAPI(title="WatchALongAgo API")

# --- Configuration & Caching Setup ---
CONFIG_DIR = os.environ.get("CONFIG_DIR", "/config")
os.makedirs(CONFIG_DIR, exist_ok=True)
cache = Cache(os.path.join(CONFIG_DIR, "cache"))

# Environment Variables (Defaults to empty string if not provided)
JF_URL = os.environ.get("JF_URL", "http://jellyfin:8096").rstrip("/")
JF_API_KEY = os.environ.get("JF_API_KEY", "")
TRAKT_CLIENT_ID = os.environ.get("TRAKT_CLIENT_ID", "")

# Allow CORS for development 
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])

# --- API Endpoints ---

@app.get("/api/status")
def get_status():
    """Lets the UI know if it needs to prompt the user for configuration."""
    return {
        "jellyfin_configured": bool(JF_API_KEY),
        "trakt_configured": bool(TRAKT_CLIENT_ID),
        "jf_url": JF_URL
    }

@app.get("/api/sync")
def sync_data():
    if not JF_API_KEY:
        return {"status": "setup_required", "message": "Jellyfin API Key missing."}

    # 1. Jellyfin Session Check (Cached for 10 seconds to prevent spamming)
    session_data = cache.get("jf_session")
    if not session_data:
        headers = {"Authorization": f"MediaBrowser Token={JF_API_KEY}"}
        try:
            sessions = requests.get(f"{JF_URL}/Sessions", headers=headers, timeout=5).json()
            active = next((s for s in sessions if "NowPlayingItem" in s), None)
            if active:
                item = active["NowPlayingItem"]
                session_data = {
                    "series": item.get("SeriesName"),
                    "name": item.get("Name"),
                    "season": item.get("ParentIndexNumber"),
                    "episode": item.get("IndexNumber"),
                    "premiere": item.get("PremiereDate")
                }
                cache.set("jf_session", session_data, expire=10)
        except Exception as e:
            return {"status": "error", "message": f"Failed to reach Jellyfin: {str(e)}"}

    if not session_data:
        return {"status": "idle", "message": "Nothing is currently playing."}

    if not TRAKT_CLIENT_ID:
        return {"status": "setup_required", "message": "Trakt Client ID missing."}

    # 2. Smart Trakt Cache Check
    cache_key = f"trakt_{session_data['series']}_s{session_data['season']}e{session_data['episode']}"
    cached_comments = cache.get(cache_key)

    if cached_comments:
        # Check if the thread is "Live" (newest comment < 24 hours old)
        if cached_comments.get("all_comments"):
            newest_ts = cached_comments["all_comments"][0].get("created_at")
            if newest_ts:
                newest_date = datetime.fromisoformat(newest_ts.replace('Z', '+00:00'))
                is_live = (datetime.now(newest_date.tzinfo) - newest_date).days < 1
                if not is_live:
                    return cached_comments

    # 3. Fetch from Trakt
    trakt_headers = {"trakt-api-key": TRAKT_CLIENT_ID, "trakt-api-version": "2"}
    
    try:
        # Step A: Find the Show ID first (using params for proper URL encoding)
        show_search_url = "https://api.trakt.tv/search/show"
        show_results = requests.get(show_search_url, params={"query": session_data['series']}, headers=trakt_headers, timeout=5).json()
        
        if not show_results:
            return {"status": "error", "message": f"Show '{session_data['series']}' not found on Trakt."}

        # Grab the 'slug' (e.g., 'breaking-bad') of the top result
        show_slug = show_results[0]['show']['ids']['slug']

        # Step B: Get the comments directly using Show Slug + Season + Episode
        comments_url = f"https://api.trakt.tv/shows/{show_slug}/seasons/{session_data['season']}/episodes/{session_data['episode']}/comments/newest"
        all_comments = requests.get(comments_url, headers=trakt_headers, timeout=5).json()

        # Catch if Trakt returns an error object instead of a list
        if isinstance(all_comments, dict) and "error" in all_comments:
            all_comments = []

        # Time Machine Filter (Comments within 14 days of premiere)
        p_date = datetime.fromisoformat(session_data['premiere'].replace('Z', '+00:00'))
        time_machine = [
            c for c in all_comments 
            if p_date <= datetime.fromisoformat(c['created_at'].replace('Z', '+00:00')) <= p_date + timedelta(days=14)
        ]

        # Keep it simple: Show Name + Episode Code + "reddit"
        google_query = f'{session_data["series"]} S{session_data["season"]:02}E{session_data["episode"]:02} reddit'
        safe_query = urllib.parse.quote(google_query)

        response_payload = {
            "status": "success",
            "title": f"{session_data['series']} - {session_data['name']}",
            "metadata": session_data,
            "time_machine": time_machine,
            "all_comments": all_comments,
            "reddit_url": f"https://www.google.com/search?q={safe_query}"
        }

        # Cache for 24 hours
        cache.set(cache_key, response_payload, expire=86400)
        return response_payload

    except Exception as e:
         return {"status": "error", "message": f"Trakt API error: {str(e)}"}

@app.post("/api/webhook")
async def jellyfin_webhook(request: Request):
    """Jellyfin Webhook Plugin hits this when playback state changes."""
    try:
        payload = await request.json()
        
        # We only care if an episode starts or stops
        if payload.get("NotificationType") in ["PlaybackStart", "PlaybackStop"]:
            # Clear the 10-second session cache so the UI gets fresh data instantly
            cache.delete("jf_session")
            # Update the global "last event" timestamp
            cache.set("last_webhook_time", time.time())
            
        return {"status": "received"}
    except Exception:
        return {"status": "error"}

@app.get("/api/stream")
async def sse_stream():
    """Pushes an event to the mobile web app when a webhook triggers."""
    async def event_generator():
        last_time = cache.get("last_webhook_time", 0)
        while True:
            await asyncio.sleep(2) # Check for changes every 2 seconds
            current_time = cache.get("last_webhook_time", 0)
            if current_time > last_time:
                yield "data: refresh\n\n"
                last_time = current_time
    
    return StreamingResponse(event_generator(), media_type="text/event-stream")

# Mount the static web app directory
os.makedirs("/app/static", exist_ok=True)
app.mount("/", StaticFiles(directory="/app/static", html=True), name="static")