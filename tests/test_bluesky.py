"""Tests for Bluesky integration: auth, search, image extraction, dedup, and bot filtering."""
from unittest.mock import patch

from main import (
    find_bluesky_posts, bsky_access_token,
    _extract_bsky_images, _is_low_content_post,
)
from tests.helpers import mock_response


# ---------------------------------------------------------------------------
# bsky_access_token
# ---------------------------------------------------------------------------

class TestBskyAccessToken:
    def test_cached(self, fresh_cache):
        fresh_cache.set("bsky_token", "cached_token")
        assert bsky_access_token() == "cached_token"

    def test_fetch(self, fresh_cache):
        fresh_cache.set("ui_config", {"bsky_identifier": "user", "bsky_app_password": "pass"})
        with patch("main.http.post") as mock_post:
            mock_post.return_value = mock_response(200, {"accessJwt": "new_token"})
            assert bsky_access_token() == "new_token"
            assert fresh_cache.get("bsky_token") == "new_token"

    def test_returns_none_without_credentials(self, fresh_cache):
        assert bsky_access_token() is None


# ---------------------------------------------------------------------------
# _extract_bsky_images
# ---------------------------------------------------------------------------

class TestExtractBskyImages:
    def test_images_view(self):
        embed = {
            "$type": "app.bsky.embed.images#view",
            "images": [{"thumb": "http://img1.jpg", "alt": "first"}, {"thumb": "http://img2.jpg"}],
        }
        result = _extract_bsky_images(embed)
        assert result == [{"url": "http://img1.jpg", "alt": "first"}, {"url": "http://img2.jpg", "alt": ""}]

    def test_external_view(self):
        embed = {
            "$type": "app.bsky.embed.external#view",
            "external": {"thumb": "http://ext.jpg"},
        }
        assert _extract_bsky_images(embed) == [{"url": "http://ext.jpg", "alt": ""}]

    def test_record_with_media(self):
        embed = {
            "$type": "app.bsky.embed.recordWithMedia#view",
            "media": {
                "$type": "app.bsky.embed.images#view",
                "images": [{"thumb": "http://nested.jpg"}],
            },
        }
        assert _extract_bsky_images(embed) == [{"url": "http://nested.jpg", "alt": ""}]

    def test_empty_embed(self):
        assert _extract_bsky_images({}) == []

    def test_images_without_thumb(self):
        embed = {
            "$type": "app.bsky.embed.images#view",
            "images": [{"fullsize": "http://full.jpg"}],  # No thumb
        }
        assert _extract_bsky_images(embed) == []


# ---------------------------------------------------------------------------
# _is_low_content_post
# ---------------------------------------------------------------------------

class TestIsLowContentPost:
    def test_bot_post_only_show_name(self):
        """A post that only says the show name + episode code is low content."""
        assert _is_low_content_post("test show s01e05", "Test Show", 1, 5, []) is True

    def test_bot_post_watching(self):
        assert _is_low_content_post("watching test show s01e05", "Test Show", 1, 5, []) is True

    def test_substantive_post(self):
        assert _is_low_content_post(
            "test show s01e05 this episode blew my mind with the twist ending",
            "Test Show", 1, 5, []
        ) is False

    def test_low_text_with_images_kept(self):
        """Low text but with images → not low content (images provide value)."""
        assert _is_low_content_post("test show s01e05", "Test Show", 1, 5, ["http://img.jpg"]) is False

    def test_long_post_always_kept(self):
        """Posts > 120 chars are never filtered even if boring words dominate."""
        long_text = "test show s01e05 " + "x" * 120
        assert _is_low_content_post(long_text, "Test Show", 1, 5, []) is False


# ---------------------------------------------------------------------------
# find_bluesky_posts (integration)
# ---------------------------------------------------------------------------

class TestFindBlueskyPosts:
    def _make_post(self, cid, text, handle="user.bsky.social", images=None):
        post = {
            "cid": cid,
            "author": {"handle": handle},
            "record": {"text": text, "createdAt": "2024-03-04T12:00:00Z"},
            "uri": f"at://did:plc:123/app.bsky.feed.post/{cid}",
        }
        if images:
            post["embed"] = {
                "$type": "app.bsky.embed.images#view",
                "images": [{"thumb": url} for url in images],
            }
        return post

    def test_basic_search(self, fresh_cache):
        posts = {"posts": [self._make_post("1", "Great episode discussion here!")]}
        with patch("main.http.get", return_value=mock_response(200, posts)):
            with patch("main.bsky_access_token", return_value="token"):
                results = find_bluesky_posts("Test Show", 1, 1)
        assert len(results) == 1
        assert results[0]["comment"] == "Great episode discussion here!"
        assert results[0]["source"] == "bluesky"

    def test_deduplication(self, fresh_cache):
        """Identical posts are deduplicated."""
        posts = {"posts": [
            self._make_post("1", "Same text here"),
            self._make_post("2", "Same text here"),
        ]}
        with patch("main.http.get", return_value=mock_response(200, posts)):
            with patch("main.bsky_access_token", return_value=None):
                results = find_bluesky_posts("Test Show", 1, 1)
        assert len(results) == 1

    def test_dedup_different_images_kept(self, fresh_cache):
        """Same text but different images = two distinct posts."""
        posts = {"posts": [
            self._make_post("1", "Check this out", images=["http://a.jpg"]),
            self._make_post("2", "Check this out", images=["http://b.jpg"]),
        ]}
        with patch("main.http.get", return_value=mock_response(200, posts)):
            with patch("main.bsky_access_token", return_value=None):
                results = find_bluesky_posts("Test Show", 1, 1)
        assert len(results) == 2

    def test_bot_post_filtered(self, fresh_cache):
        """Low-content bot posts are removed."""
        posts = {"posts": [
            self._make_post("1", "Test Show S01E01"),  # Bot — just the show name
            self._make_post("2", "This show is incredible, what a season premiere!"),
        ]}
        with patch("main.http.get", return_value=mock_response(200, posts)):
            with patch("main.bsky_access_token", return_value=None):
                results = find_bluesky_posts("Test Show", 1, 1)
        assert len(results) == 1
        assert "incredible" in results[0]["comment"]

    def test_empty_text_skipped(self, fresh_cache):
        posts = {"posts": [self._make_post("1", "")]}
        with patch("main.http.get", return_value=mock_response(200, posts)):
            with patch("main.bsky_access_token", return_value=None):
                results = find_bluesky_posts("Test Show", 1, 1)
        assert results == []

    def test_no_auth_header_when_no_token(self, fresh_cache):
        with patch("main.http.get", return_value=mock_response(200, {"posts": []})) as mock_get:
            with patch("main.bsky_access_token", return_value=None):
                find_bluesky_posts("Test Show", 1, 1)
                _, kwargs = mock_get.call_args
                assert "Authorization" not in kwargs["headers"]
                assert kwargs["headers"]["User-Agent"] == "WatchBack/1.0"

    def test_403_returns_empty(self, fresh_cache):
        with patch("main.http.get", return_value=mock_response(403)):
            with patch("main.bsky_access_token", return_value=None):
                assert find_bluesky_posts("Test Show", 1, 1) == []
