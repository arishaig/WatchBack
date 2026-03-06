"""Tests for Trakt session fallthrough and background poller."""
import pytest
from unittest.mock import MagicMock, patch

from main import trakt_watch_poller


def test_sync_data_trakt_fallthrough(fresh_cache, client):
    """Jellyfin unconfigured or idle, Trakt configured and watching → session detected."""
    fresh_cache.set("ui_config", {
        "trakt_username": "testuser",
        "trakt_client_id": "testclient",
    })

    mock_response = MagicMock()
    mock_response.status_code = 200
    mock_response.json.return_value = {
        "type": "episode",
        "episode": {"title": "Test Episode", "season": 1, "number": 5, "ids": {"trakt": 123}, "first_aired": "2023-01-01T00:00:00Z"},
        "show": {"title": "Test Show"},
    }

    with patch("main.requests.get", return_value=mock_response):
        response = client.get("/api/sync")
        data = response.json()

    assert data["status"] == "success"
    assert data["title"] == "Test Show - Test Episode"
    assert data["metadata"]["series"] == "Test Show"
    assert data["metadata"]["season"] == 1
    assert data["metadata"]["episode"] == 5


@pytest.mark.asyncio
async def test_trakt_watch_poller_change(fresh_cache):
    """Background poller detects session changes and updates webhook time."""
    fresh_cache.set("ui_config", {"trakt_username": "user1"})
    fresh_cache.set("last_webhook_time", 100)

    resp1 = MagicMock()
    resp1.status_code = 200
    resp1.json.return_value = {"type": "episode", "episode": {"ids": {"trakt": 101}}}

    resp2 = MagicMock()
    resp2.status_code = 200
    resp2.json.return_value = {"type": "episode", "episode": {"ids": {"trakt": 102}}}

    with patch("main.requests.get", side_effect=[resp1, resp2]):
        with patch("asyncio.sleep", side_effect=[None, Exception("Stop Poller")]):
            try:
                await trakt_watch_poller()
            except Exception as e:
                if str(e) != "Stop Poller":
                    raise

    assert fresh_cache.get("last_webhook_time") > 100
