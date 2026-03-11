"""Accessibility tests using axe-core via Playwright.

Requires Playwright browsers to be installed:
    uv run playwright install chromium

Tests are skipped automatically if playwright is not installed.
Every test runs twice — once in dark mode, once in light mode.
"""

import socket
import threading
import time

import pytest

playwright = pytest.importorskip("playwright", reason="playwright not installed")

import uvicorn
from axe_playwright_python.sync_playwright import Axe

from main import app
from tests.playwright_helpers import (
    CONFIG_EMPTY,
    CONFIG_FILLED,
    LOREM_LONG,
    LOREM_MEDIUM,
    LOREM_SHORT,
    STATUS_FULL,
    STATUS_UNCONFIGURED,
    SYNC_IDLE,
    SYNC_SETUP_REQUIRED,
    apply_theme,
    bluesky_post,
    get_available_themes,
    make_sync_success,
    reddit_comment,
    setup_api_routes,
    trakt_comment,
)

# ---------------------------------------------------------------------------
# Live server fixture
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def live_server_url():
    """Start the FastAPI app on a background thread for browser-based tests."""
    config = uvicorn.Config(app, host="127.0.0.1", port=18765, log_level="error")
    server = uvicorn.Server(config)
    thread = threading.Thread(target=server.run, daemon=True)
    thread.start()

    for _ in range(40):
        try:
            with socket.create_connection(("127.0.0.1", 18765), timeout=0.5):
                break
        except OSError:
            time.sleep(0.25)

    yield "http://127.0.0.1:18765"

    server.should_exit = True
    thread.join(timeout=5)


@pytest.fixture(params=get_available_themes())
def theme(request):
    return request.param


# ---------------------------------------------------------------------------
# Page helpers
# ---------------------------------------------------------------------------


def load_page(page, url, theme, *, sync=None, status=None, config=None):
    """Set up mock routes, navigate, wait for Alpine, then apply the theme."""
    setup_api_routes(page, sync=sync, status=status, config=config)
    page.goto(url)
    page.wait_for_load_state("networkidle", timeout=15_000)
    # Wait for Alpine auth flow to complete
    page.wait_for_timeout(500)
    apply_theme(page, theme)


def open_config_modal(page):
    """Click the settings gear to open the configuration panel."""
    page.click('button[title="Configuration"]')
    page.wait_for_timeout(300)  # allow Alpine x-show transition to complete


def assert_no_violations(page):
    """Run axe and assert zero violations.

    Also fails if axe found color-contrast nodes where it *could* compute the
    ratio but that ratio falls below the WCAG AA threshold. Nodes where the
    background is indeterminate (bgOverlap / CSS variable resolution failure)
    are skipped — those are axe limitations, not confirmed failures.
    """
    results = Axe().run(page)
    assert results.violations_count == 0, results.generate_report()

    # Surface computable contrast failures hidden in 'incomplete'
    contrast_failures = []
    for item in results.response.get("incomplete", []):
        if item["id"] != "color-contrast":
            continue
        for node in item["nodes"]:
            for check in node.get("any", []):
                data = check.get("data", {})
                ratio = data.get("contrastRatio", 0)
                if ratio == 0:
                    continue  # axe couldn't determine colors — not a confirmed failure
                expected_str = data.get("expectedContrastRatio", "4.5:1")
                expected = float(expected_str.replace(":1", ""))
                if ratio < expected:
                    contrast_failures.append(
                        f"  {node['html']}\n"
                        f"    ratio={ratio:.2f} (required {expected_str}), "
                        f"fg={data.get('fgColor')}, bg={data.get('bgColor')}"
                    )

    if contrast_failures:
        pytest.fail(
            "Color contrast failures (computed by axe but below WCAG AA threshold):\n"
            + "\n".join(contrast_failures)
        )


# ---------------------------------------------------------------------------
# Tests — each runs in dark and light mode via the `theme` fixture
# ---------------------------------------------------------------------------


@pytest.mark.a11y
class TestAccessibility:
    def test_setup_required(self, page, live_server_url, theme):
        """No configuration: setup-required screen."""
        load_page(
            page,
            live_server_url,
            theme,
            sync=SYNC_SETUP_REQUIRED,
            status=STATUS_UNCONFIGURED,
            config=CONFIG_EMPTY,
        )
        assert_no_violations(page)

    def test_idle(self, page, live_server_url, theme):
        """Configured but nothing currently playing."""
        load_page(
            page,
            live_server_url,
            theme,
            sync=SYNC_IDLE,
            status=STATUS_FULL,
            config=CONFIG_FILLED,
        )
        assert_no_violations(page)

    def test_success_trakt_only(self, page, live_server_url, theme):
        """Success state with Trakt comments at varying text lengths."""
        comments = [
            trakt_comment(1, LOREM_SHORT, "alice"),
            trakt_comment(2, LOREM_MEDIUM, "bob"),
            trakt_comment(3, LOREM_LONG, "carol"),
        ]
        load_page(
            page,
            live_server_url,
            theme,
            sync=make_sync_success(comments),
            status=STATUS_FULL,
            config=CONFIG_FILLED,
        )
        assert_no_violations(page)

    def test_success_reddit_with_nesting(self, page, live_server_url, theme):
        """Success state with Reddit comments, nested replies, and a thread list."""
        threads = [
            {
                "url": "https://reddit.com/r/testshow/comments/abc/",
                "title": "S01E01 Discussion Thread",
                "subreddit": "testshow",
                "score": 312,
            },
            {
                "url": "https://reddit.com/r/television/comments/def/",
                "title": "Weekly Discussion",
                "subreddit": "television",
                "score": 85,
            },
        ]
        comments = [
            reddit_comment(
                "r1",
                LOREM_MEDIUM,
                "user1",
                score=99,
                replies=[
                    reddit_comment(
                        "r1_1",
                        LOREM_SHORT,
                        "user2",
                        replies=[
                            reddit_comment("r1_1_1", LOREM_SHORT, "user3"),
                        ],
                    ),
                ],
            ),
            reddit_comment("r2", LOREM_LONG, "user4", score=7),
        ]
        load_page(
            page,
            live_server_url,
            theme,
            sync=make_sync_success(comments, reddit_threads=threads),
            status=STATUS_FULL,
            config=CONFIG_FILLED,
        )
        assert_no_violations(page)

    def test_success_bluesky_with_images(self, page, live_server_url, theme):
        """Success state with Bluesky posts: no images, one image, two images."""
        img = f"{live_server_url}/watchback.png"
        comments = [
            bluesky_post("b1", LOREM_SHORT, "alice.bsky.social"),
            bluesky_post("b2", LOREM_MEDIUM, "bob.bsky.social", images=[img]),
            bluesky_post("b3", LOREM_LONG, "carol.bsky.social", images=[img, img]),
        ]
        load_page(
            page,
            live_server_url,
            theme,
            sync=make_sync_success(comments),
            status=STATUS_FULL,
            config=CONFIG_FILLED,
        )
        assert_no_violations(page)

    def test_success_all_sources(self, page, live_server_url, theme):
        """Success state with all three sources interleaved, replies, and images."""
        img = f"{live_server_url}/watchback.png"
        threads = [
            {
                "url": "https://reddit.com/r/testshow/comments/abc/",
                "title": "S01E01 Discussion Thread",
                "subreddit": "testshow",
                "score": 250,
            },
        ]
        comments = [
            trakt_comment(1, LOREM_SHORT, "trakt_alice"),
            reddit_comment(
                "r1",
                LOREM_MEDIUM,
                "reddit_bob",
                score=58,
                replies=[
                    reddit_comment("r1_1", LOREM_SHORT, "reddit_carol"),
                ],
            ),
            bluesky_post("b1", LOREM_SHORT, "bsky.user.social", images=[img]),
            trakt_comment(2, LOREM_LONG, "trakt_dave"),
            reddit_comment("r2", LOREM_SHORT, "reddit_eve", score=12),
            bluesky_post("b2", LOREM_MEDIUM, "another.bsky.social"),
        ]
        load_page(
            page,
            live_server_url,
            theme,
            sync=make_sync_success(comments, reddit_threads=threads),
            status=STATUS_FULL,
            config=CONFIG_FILLED,
        )
        assert_no_violations(page)

    def test_config_modal_empty(self, page, live_server_url, theme):
        """Configuration panel open with no values filled in."""
        load_page(
            page,
            live_server_url,
            theme,
            sync=SYNC_SETUP_REQUIRED,
            status=STATUS_UNCONFIGURED,
            config=CONFIG_EMPTY,
        )
        open_config_modal(page)
        assert_no_violations(page)

    def test_config_modal_filled(self, page, live_server_url, theme):
        """Configuration panel open with all key fields populated."""
        load_page(
            page,
            live_server_url,
            theme,
            sync=SYNC_IDLE,
            status=STATUS_FULL,
            config=CONFIG_FILLED,
        )
        open_config_modal(page)
        assert_no_violations(page)
