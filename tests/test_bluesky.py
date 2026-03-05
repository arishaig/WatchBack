import pytest
from unittest.mock import MagicMock, patch
from main import find_bluesky_posts, bsky_access_token

def mock_response(status_code=200, body=None):
    m = MagicMock()
    m.status_code = status_code
    m.json.return_value = body if body is not None else {}
    m.text = "Mock response"
    return m

@pytest.fixture
def mock_cache():
    with patch("main.cache") as m:
        yield m

def test_bsky_access_token_cached(mock_cache):
    mock_cache.get.return_value = "cached_token"
    assert bsky_access_token() == "cached_token"
    mock_cache.get.assert_called_with("bsky_token")

def test_bsky_access_token_fetch(mock_cache):
    mock_cache.get.return_value = None
    with patch("main.get_config") as mock_cfg:
        mock_cfg.return_value = {
            "bsky_identifier": "user",
            "bsky_app_password": "pass"
        }
        with patch("requests.post") as mock_post:
            mock_post.return_value = mock_response(200, {"accessJwt": "new_token"})
            assert bsky_access_token() == "new_token"
            mock_cache.set.assert_called_with("bsky_token", "new_token", expire=5400)

def test_find_bluesky_posts_success():
    mock_posts = {
        "posts": [
            {
                "cid": "123",
                "author": {"handle": "alice.bsky.social"},
                "record": {"text": "Watching this show!", "createdAt": "2024-03-04T12:00:00Z"},
                "uri": "at://did:plc:123/app.bsky.feed.post/456"
            }
        ]
    }
    with patch("requests.get") as mock_get:
        mock_get.return_value = mock_response(200, mock_posts)
        with patch("main.bsky_access_token", return_value="token"):
            results = find_bluesky_posts("Test Show", 1, 1)
            assert len(results) == 1
            assert results[0]["comment"] == "Watching this show!"
            assert results[0]["user"]["username"] == "alice.bsky.social"
            assert "bsky.app" in results[0]["url"]

def test_find_bluesky_posts_no_token():
    # Should still work with just User-Agent if public API allows
    with patch("requests.get") as mock_get:
        mock_get.return_value = mock_response(200, {"posts": []})
        with patch("main.bsky_access_token", return_value=None):
            results = find_bluesky_posts("Test Show", 1, 1)
            assert results == []
            # Verify headers used in the call
            args, kwargs = mock_get.call_args
            assert "Authorization" not in kwargs["headers"]
            assert "User-Agent" in kwargs["headers"]
