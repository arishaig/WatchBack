"""Shared Playwright test helpers: mock data factories and route-injection utilities.

Import from this module in any Playwright-based test to get consistent mock
responses without duplicating boilerplate.
"""
import json
import re
from pathlib import Path
from typing import Any

# ---------------------------------------------------------------------------
# Theme discovery
# ---------------------------------------------------------------------------

def get_available_themes() -> list[str]:
    """Return the list of theme values defined in the UI theme dropdown.

    Parses the <select aria-label="Theme"> block in static/index.html so that
    adding a new theme to the dropdown automatically includes it in tests.
    """
    html_path = Path(__file__).parent.parent / "static" / "index.html"
    content = html_path.read_text()
    # Grab everything between the theme <select> and its closing tag
    block_match = re.search(
        r'aria-label="Theme".*?</select>', content, re.DOTALL
    )
    if block_match:
        themes = re.findall(r'<option\s+value="([^"]+)"', block_match.group())
        if themes:
            return themes
    return ["dark", "light"]  # fallback if HTML structure changes

# ---------------------------------------------------------------------------
# Lorem ipsum at three lengths
# ---------------------------------------------------------------------------

LOREM_SHORT = "Lorem ipsum dolor sit amet, consectetur adipiscing elit."

LOREM_MEDIUM = (
    "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor "
    "incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud "
    "exercitation ullamco laboris."
)

LOREM_LONG = (
    "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor "
    "incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud "
    "exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute "
    "irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla "
    "pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia "
    "deserunt mollit anim id est laborum."
)

# ---------------------------------------------------------------------------
# Comment / post factories
# ---------------------------------------------------------------------------

def trakt_comment(
    id: int,
    text: str = LOREM_SHORT,
    username: str = "alice",
    date: str = "2023-08-03T00:00:00Z",
) -> dict:
    return {
        "id": id,
        "comment": text,
        "created_at": date,
        "user": {"username": username},
        "source": "trakt",
    }


def reddit_comment(
    id: str,
    text: str = LOREM_MEDIUM,
    username: str = "redditor",
    date: str = "2023-08-03T00:00:00Z",
    replies: list | None = None,
    score: int = 42,
) -> dict:
    return {
        "id": id,
        "comment": text,
        "source": "reddit",
        "user": {"username": username},
        "created_at": date,
        "score": score,
        "url": f"https://reddit.com/r/testshow/comments/abc/_{id}/",
        "thread_title": "S01E01 Discussion Thread",
        "thread_url": "https://reddit.com/r/testshow/comments/abc/s01e01/",
        "replies": replies or [],
    }


def bluesky_post(
    id: str,
    text: str = LOREM_SHORT,
    username: str = "user.bsky.social",
    date: str = "2023-08-03T00:00:00Z",
    images: list[str] | None = None,
) -> dict:
    return {
        "id": id,
        "comment": text,
        "source": "bluesky",
        "user": {"username": username},
        "created_at": date,
        "url": f"https://bsky.app/profile/{username}/post/{id}",
        "images": images or [],
    }


# ---------------------------------------------------------------------------
# Canned /api/sync responses
# ---------------------------------------------------------------------------

SYNC_SETUP_REQUIRED: dict = {
    "status": "setup_required",
    "message": "Configure at least one source to get started.",
}

SYNC_IDLE: dict = {
    "status": "idle",
    "message": "Nothing is currently playing.",
    "reddit_url": "https://www.google.com/search?q=test+show+s01e01+reddit",
    "reddit_thread_found": False,
    "reddit_threads": [],
}


def make_sync_success(
    comments: list,
    time_machine: list | None = None,
    reddit_threads: list | None = None,
) -> dict:
    """Build a successful /api/sync response body."""
    threads = reddit_threads or []
    return {
        "status": "success",
        "title": "Test Show - Pilot",
        "metadata": {
            "series": "Test Show",
            "name": "Pilot",
            "season": 1,
            "episode": 1,
            "premiere": "2023-08-01T00:00:00Z",
        },
        "all_comments": comments,
        "time_machine": time_machine if time_machine is not None else comments[:2],
        "time_machine_days": 14,
        "reddit_url": (
            threads[0]["url"] if threads
            else "https://www.google.com/search?q=test+show+s01e01+reddit"
        ),
        "reddit_thread_found": bool(threads),
        "reddit_threads": threads,
    }


# ---------------------------------------------------------------------------
# Canned /api/config and /api/status responses
# ---------------------------------------------------------------------------

# All keys from ENV_MAP (must stay in sync with main.py)
_CONFIG_KEYS = [
    "jf_url", "jf_api_key", "trakt_client_id", "trakt_username", "trakt_access_token",
    "reddit_auto_open", "reddit_max_threads", "reddit_max_comments", "bsky_identifier", "bsky_app_password",
    "webhook_secret", "theme_mode", "time_machine_days",
]
_SECRET_KEYS = {"jf_api_key", "trakt_access_token", "bsky_app_password", "webhook_secret"}
_DEFAULTS = {
    "jf_url": "http://jellyfin:8096",
    "reddit_max_threads": "3",
    "reddit_max_comments": "250",
    "theme_mode": "dark",
    "time_machine_days": "14",
}


def _cfg_entry(value: str, key: str) -> dict:
    secret = key in _SECRET_KEYS
    effective = "****" if (secret and value) else (value or _DEFAULTS.get(key, ""))
    return {
        "effective_value": effective,
        "env_value": "",
        "stored_value": value,
        "is_env_set": False,
        "is_stored_set": bool(value),
        "source": "stored" if value else "default",
        "is_secret": secret,
    }


CONFIG_EMPTY: dict = {k: _cfg_entry("", k) for k in _CONFIG_KEYS}

CONFIG_FILLED: dict = {
    **CONFIG_EMPTY,
    "jf_url":           _cfg_entry("http://192.168.1.100:8096", "jf_url"),
    "jf_api_key":       _cfg_entry("abc123key", "jf_api_key"),
    "trakt_client_id":  _cfg_entry("traktclientid123", "trakt_client_id"),
    "trakt_username":   _cfg_entry("myuser", "trakt_username"),
    "reddit_auto_open": _cfg_entry("1", "reddit_auto_open"),
}

STATUS_UNCONFIGURED: dict = {
    "jellyfin_configured": False,
    "trakt_configured": False,
    "trakt_watch_configured": False,
    "reddit_auto_open": False,
    "jf_url": "http://jellyfin:8096",
    "sources": {k: "none" for k in _CONFIG_KEYS},
}

STATUS_FULL: dict = {
    "jellyfin_configured": True,
    "trakt_configured": True,
    "trakt_watch_configured": True,
    "reddit_auto_open": True,
    "jf_url": "http://192.168.1.100:8096",
    "sources": {k: "stored" for k in _CONFIG_KEYS},
}


# ---------------------------------------------------------------------------
# Theme helpers
# ---------------------------------------------------------------------------

def apply_theme(page, theme: str) -> None:
    """Directly set the page theme by writing the data-theme attribute.

    This is more reliable than relying on Alpine's config flow: it writes
    the same attribute that the CSS ``html[data-theme]`` selectors respond to,
    so CSS variables resolve to the correct theme values before axe runs.
    """
    page.evaluate(f'document.documentElement.setAttribute("data-theme", "{theme}")')


# ---------------------------------------------------------------------------
# Route injection
# ---------------------------------------------------------------------------

def _fulfill_json(route, body: Any) -> None:
    route.fulfill(status=200, content_type="application/json", body=json.dumps(body))


def setup_api_routes(page, *, sync=None, status=None, config=None) -> None:
    """Intercept WatchBack API calls with mock responses.

    Always blocks /api/stream (SSE) so ``wait_for_load_state("networkidle")``
    can settle. Pass a dict for any endpoint you want to mock; omit to let it
    hit the real server.

    Args:
        page:   Playwright Page object.
        sync:   Mock body for GET /api/sync, or None to pass through.
        status: Mock body for GET /api/status, or None to pass through.
        config: Mock body for GET /api/config, or None to pass through.
    """
    page.route("**/api/stream", lambda route: route.abort())

    if sync is not None:
        page.route("**/api/sync", lambda route: _fulfill_json(route, sync))
    if status is not None:
        page.route("**/api/status", lambda route: _fulfill_json(route, status))
    if config is not None:
        page.route("**/api/config", lambda route: _fulfill_json(route, config))
