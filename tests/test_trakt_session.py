import pytest
from unittest.mock import MagicMock, patch
from fastapi.testclient import TestClient
from main import app, cache, trakt_watch_poller
import time
import asyncio

@pytest.fixture
def client():
    return TestClient(app)

@pytest.fixture
def fresh_cache():
    cache.clear()
    yield cache
    cache.clear()

def test_sync_data_trakt_fallthrough(fresh_cache, client):
    # Jellyfin unconfigured or idle, Trakt configured and watching
    fresh_cache.set("ui_config", {
        "trakt_username": "testuser",
        "trakt_client_id": "testclient"
    })
    
    # Mock Jellyfin response (empty/idle)
    with patch("requests.get") as mock_get:
        # 1st call: Jellyfin Sessions (if JF_API_KEY was set, but here it's not)
        # 2nd call: Trakt Watching
        mock_response = MagicMock()
        mock_response.status_code = 200
        mock_response.json.return_value = {
            "type": "episode",
            "episode": {"title": "Test Episode", "season": 1, "number": 5, "ids": {"trakt": 123}, "first_aired": "2023-01-01T00:00:00Z"},
            "show": {"title": "Test Show"}
        }
        mock_get.return_value = mock_response
        
        # We also need to mock the following calls in sync_data: 
        # Reddit search, Bluesky search, Trakt search/comments
        # To simplify, let's just mock all requests.get for this test
        
        response = client.get("/api/sync")
        data = response.json()
        
        assert data["status"] == "success"
        assert data["title"] == "Test Show - Test Episode"
        assert data["metadata"]["series"] == "Test Show"
        assert data["metadata"]["season"] == 1
        assert data["metadata"]["episode"] == 5

@pytest.mark.asyncio
async def test_trakt_watch_poller_change(fresh_cache):
    fresh_cache.set("ui_config", {"trakt_username": "user1"})
    fresh_cache.set("last_webhook_time", 100)
    
    with patch("requests.get") as mock_get:
        # First poll: Session A
        resp1 = MagicMock()
        resp1.status_code = 200
        resp1.json.return_value = {"type": "episode", "episode": {"ids": {"trakt": 101}}}
        
        # Second poll: Session B
        resp2 = MagicMock()
        resp2.status_code = 200
        resp2.json.return_value = {"type": "episode", "episode": {"ids": {"trakt": 102}}}
        
        mock_get.side_effect = [resp1, resp2]
        
        # We need to run the poller but break after a few cycles
        # Since it has an infinite loop, we can wrap it or mock sleep
        with patch("asyncio.sleep", side_effect=[None, Exception("Stop Poller")]):
            try:
                await trakt_watch_poller()
            except Exception as e:
                if str(e) != "Stop Poller": raise
        
        # Check that last_webhook_time was updated
        assert fresh_cache.get("last_webhook_time") > 100
