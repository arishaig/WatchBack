"""Comprehensive tests for WatchBack (main.py)."""

import asyncio
from unittest.mock import patch

from main import (
    ENV_MAP,
    _extract_bsky_images,
    _is_low_content_post,
    _matches_episode,
    _safe_int,
    find_bluesky_posts,
    find_pullpush_comments,
    get_config,
    trakt_headers,
)
from tests.helpers import mock_response

# Shared fixture data
JF_SESSION_ITEM = {
    "NowPlayingItem": {
        "SeriesName": "Test Show",
        "Name": "Pilot",
        "ParentIndexNumber": 1,
        "IndexNumber": 1,
        "PremiereDate": "2014-06-01T00:00:00Z",
    }
}

TRAKT_SEARCH = [{"show": {"ids": {"slug": "test-show"}}}]

TRAKT_COMMENTS = [
    # Within 14 days of premiere
    {
        "id": 1,
        "comment": "Great!",
        "created_at": "2014-06-03T00:00:00Z",
        "user": {"username": "alice"},
    },
    # Years later — outside time-machine window
    {
        "id": 2,
        "comment": "Rewatch!",
        "created_at": "2022-01-01T00:00:00Z",
        "user": {"username": "bob"},
    },
]

REDDIT_POSTS = {
    "data": {
        "children": [
            {
                "data": {
                    "title": "S01E01 Discussion Thread",
                    "permalink": "/r/testshow/comments/abc/s01e01_discussion/",
                    "subreddit": "testshow",
                    "score": 250,
                }
            },
            {
                "data": {
                    "title": "S01E01 Rewatch",
                    "permalink": "/r/television/comments/def/rewatch/",
                    "subreddit": "television",
                    "score": 80,
                }
            },
        ]
    }
}


# ---------------------------------------------------------------------------
# get_config()
# ---------------------------------------------------------------------------


class TestGetConfig:
    def test_defaults_when_nothing_set(self, fresh_cache):
        cfg = get_config()
        assert cfg["jf_url"] == "http://jellyfin:8096"
        assert cfg["jf_api_key"] == ""
        assert cfg["trakt_client_id"] == ""
        assert cfg["trakt_username"] == ""
        assert cfg["trakt_access_token"] == ""
        assert cfg["reddit_auto_open"] == ""
        assert cfg["reddit_max_threads"] == "3"

    def test_stored_overrides_env(self, fresh_cache, monkeypatch):
        fresh_cache.set("ui_config", {"jf_api_key": "stored-key"})
        monkeypatch.setenv("JF_API_KEY", "env-key")
        assert get_config()["jf_api_key"] == "stored-key"

    def test_stored_used_when_no_env(self, fresh_cache):
        fresh_cache.set("ui_config", {"jf_api_key": "stored-key"})
        assert get_config()["jf_api_key"] == "stored-key"

    def test_env_trailing_slash_stripped(self, fresh_cache, monkeypatch):
        monkeypatch.setenv("JF_URL", "http://jellyfin:8096/")
        assert get_config()["jf_url"] == "http://jellyfin:8096"

    def test_stored_trailing_slash_stripped(self, fresh_cache):
        fresh_cache.set("ui_config", {"jf_url": "http://192.168.1.1:8096/"})
        assert get_config()["jf_url"] == "http://192.168.1.1:8096"

    def test_multiple_stored_values(self, fresh_cache):
        fresh_cache.set(
            "ui_config",
            {
                "jf_api_key": "k",
                "trakt_client_id": "t",
                "trakt_username": "u",
            },
        )
        cfg = get_config()
        assert cfg["jf_api_key"] == "k"
        assert cfg["trakt_client_id"] == "t"
        assert cfg["trakt_username"] == "u"


# ---------------------------------------------------------------------------
# GET /api/status
# ---------------------------------------------------------------------------


class TestApiStatus:
    def test_fully_unconfigured(self, client):
        data = client.get("/api/status").json()
        assert data["jellyfin_configured"] is False
        assert data["trakt_configured"] is False
        assert data["trakt_watch_configured"] is False
        assert data["reddit_auto_open"] is False

    def test_jellyfin_configured(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "key"})
        assert client.get("/api/status").json()["jellyfin_configured"] is True

    def test_trakt_client_configured(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"trakt_client_id": "tid"})
        data = client.get("/api/status").json()
        assert data["trakt_configured"] is True
        assert data["trakt_watch_configured"] is False  # no username/token

    def test_trakt_watch_via_username(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"trakt_username": "user"})
        assert client.get("/api/status").json()["trakt_watch_configured"] is True

    def test_trakt_watch_via_token(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"trakt_access_token": "tok"})
        assert client.get("/api/status").json()["trakt_watch_configured"] is True

    def test_reddit_auto_open(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"reddit_auto_open": "1"})
        assert client.get("/api/status").json()["reddit_auto_open"] is True

    def test_sources_env(self, monkeypatch, client):
        monkeypatch.setenv("JF_API_KEY", "env-key")
        sources = client.get("/api/status").json()["sources"]
        assert sources["jf_api_key"] == "env"

    def test_sources_stored(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"trakt_username": "user"})
        sources = client.get("/api/status").json()["sources"]
        assert sources["trakt_username"] == "stored"
        assert sources["jf_api_key"] == "none"

    def test_sources_none_when_unset(self, client):
        sources = client.get("/api/status").json()["sources"]
        for key in ENV_MAP:
            assert sources[key] == "none"


# ---------------------------------------------------------------------------
# GET /api/config
# ---------------------------------------------------------------------------


class TestApiConfigGet:
    def test_all_keys_present(self, client):
        data = client.get("/api/config").json()
        for key in ENV_MAP:
            assert key in data
            assert set(data[key].keys()) == {
                "effective_value",
                "env_value",
                "stored_value",
                "is_env_set",
                "is_stored_set",
                "source",
                "is_secret",
            }

    def test_secrets_value_always_empty(self, fresh_cache, client):
        fresh_cache.set(
            "ui_config",
            {"jf_api_key": "s1", "trakt_client_id": "s2", "trakt_access_token": "s3"},
        )
        data = client.get("/api/config").json()
        assert data["jf_api_key"]["effective_value"] == "****"
        assert (
            data["trakt_client_id"]["effective_value"] == "s2"
        )  # Client ID is public, not secret
        assert data["trakt_access_token"]["effective_value"] == "****"

    def test_secrets_is_set_true(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "key"})
        assert client.get("/api/config").json()["jf_api_key"]["is_stored_set"] is True

    def test_non_secret_value_exposed(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"trakt_username": "myuser"})
        assert (
            client.get("/api/config").json()["trakt_username"]["effective_value"]
            == "myuser"
        )

    def test_source_env(self, monkeypatch, client):
        monkeypatch.setenv("TRAKT_CLIENT_ID", "env-id")
        data = client.get("/api/config").json()
        assert data["trakt_client_id"]["source"] == "env"
        assert data["trakt_client_id"]["is_env_set"] is True

    def test_source_stored(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"trakt_username": "u"})
        assert client.get("/api/config").json()["trakt_username"]["source"] == "stored"

    def test_source_default(self, client):
        assert (
            client.get("/api/config").json()["trakt_client_id"]["source"] == "default"
        )

    def test_default_jf_url_visible(self, client):
        assert (
            client.get("/api/config").json()["jf_url"]["effective_value"]
            == "http://jellyfin:8096"
        )

    def test_default_max_threads(self, client):
        assert (
            client.get("/api/config").json()["reddit_max_threads"]["effective_value"]
            == "3"
        )


# ---------------------------------------------------------------------------
# PUT /api/config
# ---------------------------------------------------------------------------


class TestApiConfigPut:
    def test_saves_values(self, fresh_cache, client):
        client.put(
            "/api/config",
            json={"jf_url": "http://10.0.0.1:8096", "trakt_username": "user"},
        )
        stored = fresh_cache.get("ui_config")
        assert stored["jf_url"] == "http://10.0.0.1:8096"
        assert stored["trakt_username"] == "user"

    def test_empty_value_removes_key(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"trakt_username": "old"})
        client.put("/api/config", json={"trakt_username": ""})
        assert "trakt_username" not in fresh_cache.get("ui_config")

    def test_whitespace_stripped(self, fresh_cache, client):
        client.put("/api/config", json={"trakt_username": "  user  "})
        assert fresh_cache.get("ui_config")["trakt_username"] == "user"

    def test_unknown_keys_ignored(self, fresh_cache, client):
        client.put("/api/config", json={"hacker_key": "evil"})
        assert "hacker_key" not in (fresh_cache.get("ui_config") or {})

    def test_omitted_keys_preserved(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "existing"})
        client.put("/api/config", json={"trakt_username": "new"})
        stored = fresh_cache.get("ui_config")
        assert stored["jf_api_key"] == "existing"
        assert stored["trakt_username"] == "new"

    def test_returns_ok(self, client):
        assert client.put("/api/config", json={}).json() == {"status": "ok"}


# ---------------------------------------------------------------------------
# POST /api/cache/clear
# ---------------------------------------------------------------------------


class TestCacheClear:
    def test_clears_session_cache(self, fresh_cache, client):
        fresh_cache.set("jf_session", {"series": "Show"})
        client.post("/api/cache/clear")
        assert fresh_cache.get("jf_session") is None

    def test_clears_trakt_cache(self, fresh_cache, client):
        fresh_cache.set("trakt_Show_s1e1", {"all_comments": []})
        client.post("/api/cache/clear")
        assert fresh_cache.get("trakt_Show_s1e1") is None

    def test_clears_reddit_cache(self, fresh_cache, client):
        fresh_cache.set("reddit_Show_s1e1", [{"url": "https://reddit.com/..."}])
        client.post("/api/cache/clear")
        assert fresh_cache.get("reddit_Show_s1e1") is None

    def test_preserves_ui_config(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "keep-me"})
        fresh_cache.set("jf_session", {"series": "Show"})
        client.post("/api/cache/clear")
        assert fresh_cache.get("ui_config") == {"jf_api_key": "keep-me"}

    def test_preserves_subreddit_overrides(self, fresh_cache, client):
        fresh_cache.set("subreddit_overrides", {"severance": "SeveranceAppleTVPlus"})
        fresh_cache.set("jf_session", {"series": "Show"})
        client.post("/api/cache/clear")
        assert fresh_cache.get("subreddit_overrides") == {
            "severance": "SeveranceAppleTVPlus"
        }

    def test_returns_ok(self, client):
        assert client.post("/api/cache/clear").json() == {"status": "ok"}


# ---------------------------------------------------------------------------
# Subreddit overrides
# ---------------------------------------------------------------------------


class TestSubredditOverrides:
    def test_get_empty(self, client):
        assert client.get("/api/subreddit-overrides").json() == {}

    def test_set_and_get(self, fresh_cache, client):
        client.put(
            "/api/subreddit-overrides",
            json={"series": "Severance", "subreddit": "SeveranceAppleTVPlus"},
        )
        data = client.get("/api/subreddit-overrides").json()
        assert data["severance"] == "SeveranceAppleTVPlus"

    def test_case_insensitive_key(self, fresh_cache, client):
        client.put(
            "/api/subreddit-overrides",
            json={"series": "The Bear", "subreddit": "TheBear"},
        )
        data = client.get("/api/subreddit-overrides").json()
        assert "the bear" in data

    def test_remove_override(self, fresh_cache, client):
        client.put(
            "/api/subreddit-overrides",
            json={"series": "Severance", "subreddit": "SeveranceAppleTVPlus"},
        )
        client.put(
            "/api/subreddit-overrides", json={"series": "Severance", "subreddit": ""}
        )
        assert client.get("/api/subreddit-overrides").json() == {}

    def test_missing_series_returns_error(self, client):
        r = client.put("/api/subreddit-overrides", json={"subreddit": "test"})
        assert r.json()["status"] == "error"

    def test_override_used_in_search(self, fresh_cache):
        from main import _get_show_subreddits

        fresh_cache.set("subreddit_overrides", {"severance": "SeveranceAppleTVPlus"})
        subs = _get_show_subreddits("Severance")
        assert subs[0] == "severanceappletvplus"
        assert "severance" in subs  # derived name still present as fallback


# ---------------------------------------------------------------------------
# _matches_episode()
# ---------------------------------------------------------------------------


class TestMatchesEpisode:
    # --- Should match ---

    def test_s01e10_standard(self):
        assert _matches_episode("S01E10 Discussion", 1, 10)

    def test_s1e10_unpadded_season(self):
        assert _matches_episode("S1E10 Discussion", 1, 10)

    def test_s01e01_padded_single_digit(self):
        assert _matches_episode("S01E01 Discussion", 1, 1)

    def test_s1e1_unpadded_both(self):
        assert _matches_episode("S1E1 Discussion", 1, 1)

    def test_case_insensitive(self):
        assert _matches_episode("s01e10 discussion", 1, 10)

    def test_embedded_in_brackets(self):
        assert _matches_episode("[TV] Show S01E10 HD Discussion", 1, 10)

    def test_1x10_format(self):
        assert _matches_episode("Show 1x10 discussion", 1, 10)

    def test_1X10_uppercase(self):
        assert _matches_episode("Show 1X10 Discussion", 1, 10)

    def test_1x09_single_digit_episode(self):
        assert _matches_episode("Show 1x09 discussion", 1, 9)

    def test_dot_1_10(self):
        assert _matches_episode("Episode Discussion - 1.10 - Season Finale", 1, 10)

    def test_dot_1_09(self):
        assert _matches_episode("Episode Discussion - 1.09 - Title Here", 1, 9)

    def test_season_episode_prose(self):
        assert _matches_episode("Season 1 Episode 10 Discussion", 1, 10)

    def test_season_episode_prose_with_comma(self):
        assert _matches_episode("Season 1, Episode 10 rewatch", 1, 10)

    def test_season_episode_prose_with_title(self):
        assert _matches_episode(
            "Season 1 Episode 10 '1984' re-watch discussion (series finale)", 1, 10
        )

    def test_season_episode_prose_lowercase(self):
        assert _matches_episode("season 1 episode 10 thoughts", 1, 10)

    # --- Should NOT match ---

    def test_no_match_torrent_s01_1080p(self):
        """Core regression: '1.10' inside 'S01.1080p' must not match."""
        assert not _matches_episode(
            "[TV] Halt.and.Catch.Fire.S01.1080p.BluRay.x264-SHORTBREHD | 27.1GB", 1, 10
        )

    def test_no_match_resolution_string(self):
        assert not _matches_episode("show.s01.1080p.webrip.x264", 1, 10)

    def test_no_match_wrong_season(self):
        assert not _matches_episode("S02E10 Discussion", 1, 10)

    def test_no_match_wrong_episode(self):
        assert not _matches_episode("S01E11 Discussion", 1, 10)

    def test_no_match_s1e1_inside_s1e10(self):
        """S1E1 should not match a title about S1E10."""
        assert not _matches_episode("S01E10 Discussion", 1, 1)

    def test_no_match_s01e10_inside_s01e100(self):
        """Episode 10 should not match S01E100."""
        assert not _matches_episode("S01E100 Discussion", 1, 10)

    def test_no_match_episode_100_for_ep_10(self):
        """'Season 1 Episode 100' should not match episode 10."""
        assert not _matches_episode("Season 1 Episode 100 Discussion", 1, 10)

    def test_no_match_season_only_no_episode(self):
        assert not _matches_episode("Season 1 finale discussion", 1, 10)

    def test_no_match_season_2_prose(self):
        assert not _matches_episode("Season 2 Episode 10 Discussion", 1, 10)


# ---------------------------------------------------------------------------
# GET /api/sync
# ---------------------------------------------------------------------------


class TestApiSync:
    def test_setup_required_when_unconfigured(self, client):
        r = client.get("/api/sync")
        assert r.json()["status"] == "setup_required"

    def test_idle_when_jellyfin_nothing_playing(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "key"})
        with patch("main.http.get", return_value=mock_response(200, [])):
            r = client.get("/api/sync")
        assert r.json()["status"] == "idle"

    def test_jellyfin_unreachable_returns_error(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "key"})
        with patch("main.http.get", side_effect=Exception("refused")):
            r = client.get("/api/sync")
        data = r.json()
        assert data["status"] == "error"
        assert "Jellyfin" in data["message"]

    def test_tier1_jellyfin_only_no_comments(self, fresh_cache, client):
        """Tier 1: Jellyfin session, no Trakt → empty comment arrays."""
        fresh_cache.set("ui_config", {"jf_api_key": "key"})
        with patch("main.http.get", return_value=mock_response(200, [JF_SESSION_ITEM])):
            r = client.get("/api/sync")
        data = r.json()
        assert data["status"] == "success"
        assert data["time_machine"] == []
        assert data["all_comments"] == []
        assert data["metadata"]["series"] == "Test Show"

    def test_google_reddit_url_always_present(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "key"})
        with patch("main.http.get", return_value=mock_response(200, [JF_SESSION_ITEM])):
            data = client.get("/api/sync").json()
        assert "google.com/search" in data["reddit_url"]
        assert data["reddit_thread_found"] is False
        assert data["reddit_threads"] == []

    def test_trakt_comments_fetched(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "key", "trakt_client_id": "tid"})

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "search/show" in url:
                return mock_response(200, TRAKT_SEARCH)
            if "/comments/" in url:
                return mock_response(200, TRAKT_COMMENTS)
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect):
            data = client.get("/api/sync").json()
        assert data["status"] == "success"
        assert len(data["all_comments"]) == 2

    def test_time_machine_filter(self, fresh_cache, client):
        """Only comments within 14 days of premiere appear in time_machine."""
        fresh_cache.set("ui_config", {"jf_api_key": "key", "trakt_client_id": "tid"})

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "search/show" in url:
                return mock_response(200, TRAKT_SEARCH)
            if "/comments/" in url:
                return mock_response(200, TRAKT_COMMENTS)
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect):
            data = client.get("/api/sync").json()
        # id=1 is 2 days after premiere; id=2 is 8 years later
        assert len(data["time_machine"]) == 1
        assert data["time_machine"][0]["id"] == 1
        assert len(data["all_comments"]) == 2

    def test_trakt_401_returns_clear_error(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "key", "trakt_client_id": "tid"})

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "search/show" in url:
                return mock_response(401, text="Unauthorized")
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect):
            data = client.get("/api/sync").json()
        assert data["status"] == "error"
        assert "401" in data["message"]

    def test_empty_trakt_comments_response(self, fresh_cache, client):
        """204 / empty body from Trakt comments → empty list, not a crash."""
        fresh_cache.set("ui_config", {"jf_api_key": "key", "trakt_client_id": "tid"})

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "search/show" in url:
                return mock_response(200, TRAKT_SEARCH)
            if "/comments/" in url:
                return mock_response(204, text="")
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect):
            data = client.get("/api/sync").json()
        assert data["status"] == "success"
        assert data["all_comments"] == []

    def test_trakt_error_object_in_comments(self, fresh_cache, client):
        """Trakt returning {error: ...} instead of a list → treated as empty."""
        fresh_cache.set("ui_config", {"jf_api_key": "key", "trakt_client_id": "tid"})

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "search/show" in url:
                return mock_response(200, TRAKT_SEARCH)
            if "/comments/" in url:
                return mock_response(200, {"error": "not found"})
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect):
            data = client.get("/api/sync").json()
        assert data["all_comments"] == []

    def test_reddit_threads_returned(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"jf_api_key": "key", "reddit_auto_open": "1"})

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "reddit.com/search" in url:
                return mock_response(200, REDDIT_POSTS)
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect):
            data = client.get("/api/sync").json()
        assert data["reddit_thread_found"] is True
        assert len(data["reddit_threads"]) >= 1
        assert "reddit.com" in data["reddit_url"]
        thread = data["reddit_threads"][0]
        assert {"url", "title", "subreddit", "score"} == set(thread.keys())

    def test_reddit_show_sub_ranked_first(self, fresh_cache, client):
        """Show-specific subreddit (tier 0) beats r/television (tier 1)."""
        fresh_cache.set("ui_config", {"jf_api_key": "key", "reddit_auto_open": "1"})

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "reddit.com/search" in url:
                return mock_response(200, REDDIT_POSTS)
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect):
            data = client.get("/api/sync").json()
        # "testshow" sub (tier 0) should beat "television" (tier 1) even with lower score
        assert data["reddit_threads"][0]["subreddit"] == "testshow"

    def test_trakt_comments_cached(self, fresh_cache, client):
        """After first sync, Trakt API not called again for same episode."""
        fresh_cache.set("ui_config", {"jf_api_key": "key", "trakt_client_id": "tid"})

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "search/show" in url:
                return mock_response(200, TRAKT_SEARCH)
            if "/comments/" in url:
                return mock_response(200, TRAKT_COMMENTS)
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect) as mock_get:
            client.get("/api/sync")
            first_count = mock_get.call_count
            # Expire only the short-lived session cache
            fresh_cache.delete("jf_session")
            client.get("/api/sync")
            second_count = mock_get.call_count

        # Second call re-fetches session but not Trakt comments
        assert second_count < first_count * 2
        # Specifically: search/show and /comments/ not called again
        trakt_comment_calls = [
            c for c in mock_get.call_args_list if "/comments/" in str(c)
        ]
        assert len(trakt_comment_calls) == 1

    def test_old_string_reddit_cache_invalidated(self, fresh_cache, client):
        """Old string-format reddit cache entries are ignored and re-fetched."""
        fresh_cache.set("ui_config", {"jf_api_key": "key", "reddit_auto_open": "1"})
        # Simulate old cache format (string URL, not list)
        fresh_cache.set("reddit_Test Show_s1e1", "https://reddit.com/old")

        def side_effect(url, **kwargs):
            if "/Sessions" in url:
                return mock_response(200, [JF_SESSION_ITEM])
            if "reddit.com/search" in url:
                return mock_response(200, REDDIT_POSTS)
            return mock_response(200, [])

        with patch("main.http.get", side_effect=side_effect):
            data = client.get("/api/sync").json()
        # Should have re-fetched and returned structured data
        assert isinstance(data["reddit_threads"], list)


# ---------------------------------------------------------------------------
# Sync endpoint integration -- PullPush caching
# ---------------------------------------------------------------------------


class TestSyncPullpushCaching:
    """Verify that /api/sync caches and retrieves PullPush results correctly."""

    SESSION = {
        "series": "The Bear",
        "name": "Pop",
        "season": 2,
        "episode": 5,
        "premiere": "2023-08-01T00:00:00Z",
    }

    def test_pullpush_results_cached(self, fresh_cache, monkeypatch):
        """PullPush results are cached for 24h on first fetch."""
        monkeypatch.setenv("JF_API_KEY", "test")
        fresh_cache.set("jf_session", self.SESSION, expire=60)

        pp_comment = {
            "id": "c1",
            "comment": "Great episode",
            "source": "reddit",
            "user": {"username": "user1"},
            "created_at": "2023-08-02T00:00:00Z",
            "score": 10,
            "url": "https://reddit.com/r/TheBear/comments/abc123/_/c1/",
            "thread_title": "Discussion",
            "thread_url": "https://reddit.com/r/TheBear/comments/abc123/",
            "replies": [],
        }

        with (
            patch("main.find_pullpush_comments", return_value=[pp_comment]) as mock_pp,
            patch("main.find_bluesky_posts", return_value=[]),
            patch("main.find_reddit_threads", return_value=[]),
        ):
            from main import sync_data

            result = sync_data()
            assert mock_pp.call_count == 1

            # Second call should use cache
            result2 = sync_data()
            assert mock_pp.call_count == 1  # Not called again

        assert result["status"] == "success"
        reddit_comments = [
            c for c in result["all_comments"] if c.get("source") == "reddit"
        ]
        assert len(reddit_comments) == 1

    def test_pullpush_failure_doesnt_break_sync(self, fresh_cache, monkeypatch):
        """If PullPush returns empty, sync still succeeds with other sources."""
        monkeypatch.setenv("JF_API_KEY", "test")
        fresh_cache.set("jf_session", self.SESSION, expire=60)

        bsky_comment = {
            "id": "b1",
            "comment": "Bluesky post",
            "source": "bluesky",
            "user": {"username": "user.bsky.social"},
            "created_at": "2023-08-02T00:00:00Z",
            "url": "https://bsky.app/profile/user.bsky.social/post/b1",
            "images": [],
        }

        with (
            patch("main.find_pullpush_comments", return_value=[]),
            patch("main.find_bluesky_posts", return_value=[bsky_comment]),
            patch("main.find_reddit_threads", return_value=[]),
        ):
            from main import sync_data

            result = sync_data()

        assert result["status"] == "success"
        assert len(result["all_comments"]) == 1
        assert result["all_comments"][0]["source"] == "bluesky"

    def test_reddit_button_shown_regardless(self, fresh_cache, monkeypatch):
        """reddit_url is always present even when PullPush returns nothing."""
        monkeypatch.setenv("JF_API_KEY", "test")
        fresh_cache.set("jf_session", self.SESSION, expire=60)

        with (
            patch("main.find_pullpush_comments", return_value=[]),
            patch("main.find_bluesky_posts", return_value=[]),
            patch("main.find_reddit_threads", return_value=[]),
        ):
            from main import sync_data

            result = sync_data()

        assert result["status"] == "success"
        assert "reddit_url" in result
        assert result["reddit_url"]  # Not empty


# ---------------------------------------------------------------------------
# POST /api/webhook
# ---------------------------------------------------------------------------


class TestWebhook:
    def test_playback_start_clears_session(self, fresh_cache, client):
        fresh_cache.set("jf_session", {"series": "Show"})
        client.post("/api/webhook", json={"NotificationType": "PlaybackStart"})
        assert fresh_cache.get("jf_session") is None

    def test_playback_stop_clears_session(self, fresh_cache, client):
        fresh_cache.set("jf_session", {"series": "Show"})
        client.post("/api/webhook", json={"NotificationType": "PlaybackStop"})
        assert fresh_cache.get("jf_session") is None

    def test_playback_start_stamps_webhook_time(self, fresh_cache, client):
        client.post("/api/webhook", json={"NotificationType": "PlaybackStart"})
        assert fresh_cache.get("last_webhook_time") is not None

    def test_unrecognised_event_leaves_session(self, fresh_cache, client):
        fresh_cache.set("jf_session", {"series": "Show"})
        client.post("/api/webhook", json={"NotificationType": "SomeOtherEvent"})
        assert fresh_cache.get("jf_session") is not None

    def test_invalid_json_handled(self, client):
        r = client.post(
            "/api/webhook",
            content=b"not-json",
            headers={"Content-Type": "application/json"},
        )
        assert r.json()["status"] == "error"

    def test_returns_received(self, client):
        r = client.post("/api/webhook", json={"NotificationType": "PlaybackStart"})
        assert r.json()["status"] == "received"

    def test_rejects_bad_secret(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"webhook_secret": "s3cret"})
        r = client.post(
            "/api/webhook",
            json={"NotificationType": "PlaybackStart"},
            headers={"X-Webhook-Secret": "wrong"},
        )
        assert r.json()["status"] == "unauthorized"

    def test_accepts_correct_secret(self, fresh_cache, client):
        fresh_cache.set("ui_config", {"webhook_secret": "s3cret"})
        r = client.post(
            "/api/webhook",
            json={"NotificationType": "PlaybackStart"},
            headers={"X-Webhook-Secret": "s3cret"},
        )
        assert r.json()["status"] == "received"

    def test_no_secret_configured_accepts_all(self, client):
        r = client.post("/api/webhook", json={"NotificationType": "PlaybackStart"})
        assert r.json()["status"] == "received"


# ---------------------------------------------------------------------------
# POST /api/restart
# ---------------------------------------------------------------------------


class TestRestart:
    def test_returns_restarting(self, client):
        _real_create_task = asyncio.create_task

        def _intercept(coro, **kw):
            """Sink the _do_exit coroutine but let other create_task calls through."""
            if hasattr(coro, "__qualname__") and "_do_exit" in coro.__qualname__:
                coro.close()
                return _real_create_task(asyncio.sleep(0))  # return a real Task
            return _real_create_task(coro, **kw)

        with patch("main.asyncio.create_task", side_effect=_intercept):
            r = client.post("/api/restart", json={"confirm": True})
        assert r.json()["status"] == "restarting"

    def test_rejects_without_confirm(self, client):
        r = client.post("/api/restart", json={})
        assert r.json()["status"] == "error"


# ---------------------------------------------------------------------------
# _safe_int()
# ---------------------------------------------------------------------------


class TestSafeInt:
    def test_valid_string(self):
        assert _safe_int("14", 7) == 14

    def test_non_numeric_string(self):
        assert _safe_int("abc", 7) == 7

    def test_empty_string(self):
        assert _safe_int("", 7) == 7

    def test_none(self):
        assert _safe_int(None, 7) == 7

    def test_int_passthrough(self):
        assert _safe_int(42, 7) == 42


# ---------------------------------------------------------------------------
# trakt_headers()
# ---------------------------------------------------------------------------


class TestTraktHeaders:
    def test_basic_headers(self):
        cfg = {"trakt_client_id": "my-client-id", "trakt_access_token": ""}
        h = trakt_headers(cfg)
        assert h["trakt-api-key"] == "my-client-id"
        assert h["trakt-api-version"] == "2"
        assert "Authorization" not in h

    def test_auth_included_when_token_present(self):
        cfg = {"trakt_client_id": "cid", "trakt_access_token": "my-token"}
        h = trakt_headers(cfg, auth=True)
        assert h["Authorization"] == "Bearer my-token"

    def test_no_auth_when_token_missing(self):
        cfg = {"trakt_client_id": "cid", "trakt_access_token": ""}
        h = trakt_headers(cfg, auth=True)
        assert "Authorization" not in h


# ---------------------------------------------------------------------------
# _extract_bsky_images()
# ---------------------------------------------------------------------------


class TestExtractBskyImages:
    def test_images_view(self):
        embed = {
            "$type": "app.bsky.embed.images#view",
            "images": [
                {"thumb": "https://cdn.bsky.app/img/1.jpg", "alt": "screenshot"},
                {"thumb": "https://cdn.bsky.app/img/2.jpg"},
            ],
        }
        imgs = _extract_bsky_images(embed)
        assert len(imgs) == 2
        assert imgs[0] == {"url": "https://cdn.bsky.app/img/1.jpg", "alt": "screenshot"}
        assert imgs[1]["alt"] == ""

    def test_external_view(self):
        embed = {
            "$type": "app.bsky.embed.external#view",
            "external": {"thumb": "https://cdn.bsky.app/ext.jpg"},
        }
        imgs = _extract_bsky_images(embed)
        assert len(imgs) == 1
        assert imgs[0] == {"url": "https://cdn.bsky.app/ext.jpg", "alt": ""}

    def test_record_with_media_view(self):
        embed = {
            "$type": "app.bsky.embed.recordWithMedia#view",
            "media": {
                "$type": "app.bsky.embed.images#view",
                "images": [
                    {"thumb": "https://cdn.bsky.app/nested.jpg", "alt": "nested"}
                ],
            },
        }
        imgs = _extract_bsky_images(embed)
        assert len(imgs) == 1
        assert imgs[0]["url"] == "https://cdn.bsky.app/nested.jpg"

    def test_empty_embed(self):
        assert _extract_bsky_images({}) == []

    def test_missing_thumb(self):
        embed = {
            "$type": "app.bsky.embed.images#view",
            "images": [{"alt": "no thumb here"}],
        }
        assert _extract_bsky_images(embed) == []


# ---------------------------------------------------------------------------
# _is_low_content_post()
# ---------------------------------------------------------------------------


class TestIsLowContentPost:
    def test_bot_like_post(self):
        text = "watching test show s01e01"
        assert _is_low_content_post(text, "Test Show", 1, 1, []) is True

    def test_substantial_commentary(self):
        text = "this episode of test show s01e01 completely blew my mind, the writing was incredible and the twist at the end was perfect"
        assert _is_low_content_post(text, "Test Show", 1, 1, []) is False

    def test_short_text_with_images(self):
        text = "test show s01e01"
        images = [{"url": "https://example.com/img.jpg", "alt": ""}]
        assert _is_low_content_post(text, "Test Show", 1, 1, images) is False

    def test_just_watching_show_name(self):
        text = "watching test show s01e01 #tv"
        assert _is_low_content_post(text, "Test Show", 1, 1, []) is True

    def test_episode_code_variants(self):
        text = "test show 1x01"
        assert _is_low_content_post(text, "Test Show", 1, 1, []) is True


# ---------------------------------------------------------------------------
# find_bluesky_posts()
# ---------------------------------------------------------------------------


class TestFindBlueskyPosts:
    SAMPLE_BSKY_RESPONSE = {
        "posts": [
            {
                "cid": "cid1",
                "uri": "at://did:plc:abc/app.bsky.feed.post/post1",
                "author": {"handle": "alice.bsky.social"},
                "record": {
                    "text": "Just watched Test Show S01E01 and it was amazing! Great pilot episode.",
                    "createdAt": "2024-01-15T20:00:00Z",
                },
                "embed": {},
                "likeCount": 5,
            },
            {
                "cid": "cid2",
                "uri": "at://did:plc:def/app.bsky.feed.post/post2",
                "author": {"handle": "bob.bsky.social"},
                "record": {
                    "text": "Test Show S01E01 has an incredible opening scene, loved the cinematography",
                    "createdAt": "2024-01-15T21:00:00Z",
                },
                "embed": {},
                "likeCount": 3,
            },
        ],
    }

    @patch("main.bsky_access_token", return_value=None)
    @patch("main.http.get")
    def test_returns_posts(self, mock_get, _mock_token):
        mock_get.return_value = mock_response(200, self.SAMPLE_BSKY_RESPONSE)
        results = find_bluesky_posts("Test Show", 1, 1)
        assert len(results) == 2
        assert results[0]["id"] == "cid1"
        assert results[0]["source"] == "bluesky"
        assert results[0]["user"]["username"] == "alice.bsky.social"
        assert "bsky.app/profile/alice.bsky.social/post/post1" in results[0]["url"]

    @patch("main.bsky_access_token", return_value=None)
    @patch("main.http.get")
    def test_403_returns_empty(self, mock_get, _mock_token):
        mock_get.return_value = mock_response(403, text="Forbidden")
        assert find_bluesky_posts("Test Show", 1, 1) == []

    @patch("main.bsky_access_token", return_value=None)
    @patch("main.http.get")
    def test_network_error_returns_empty(self, mock_get, _mock_token):
        mock_get.side_effect = Exception("Connection refused")
        assert find_bluesky_posts("Test Show", 1, 1) == []

    @patch("main.bsky_access_token", return_value=None)
    @patch("main.http.get")
    def test_dedup_identical_posts(self, mock_get, _mock_token):
        duped_response = {
            "posts": [
                {
                    "cid": "cid1",
                    "uri": "at://did:plc:abc/app.bsky.feed.post/post1",
                    "author": {"handle": "alice.bsky.social"},
                    "record": {
                        "text": "Just watched Test Show S01E01 and it was amazing! Great pilot episode.",
                        "createdAt": "2024-01-15T20:00:00Z",
                    },
                    "embed": {},
                    "likeCount": 5,
                },
                {
                    "cid": "cid_dup",
                    "uri": "at://did:plc:xyz/app.bsky.feed.post/post_dup",
                    "author": {"handle": "repost.bsky.social"},
                    "record": {
                        "text": "Just watched Test Show S01E01 and it was amazing! Great pilot episode.",
                        "createdAt": "2024-01-15T20:05:00Z",
                    },
                    "embed": {},
                    "likeCount": 1,
                },
            ],
        }
        mock_get.return_value = mock_response(200, duped_response)
        results = find_bluesky_posts("Test Show", 1, 1)
        assert len(results) == 1

    @patch("main.bsky_access_token", return_value=None)
    @patch("main.http.get")
    def test_bot_posts_filtered(self, mock_get, _mock_token):
        bot_response = {
            "posts": [
                {
                    "cid": "cid_bot",
                    "uri": "at://did:plc:bot/app.bsky.feed.post/botpost",
                    "author": {"handle": "bot.bsky.social"},
                    "record": {
                        "text": "watching Test Show S01E01",
                        "createdAt": "2024-01-15T20:00:00Z",
                    },
                    "embed": {},
                    "likeCount": 0,
                },
            ],
        }
        mock_get.return_value = mock_response(200, bot_response)
        results = find_bluesky_posts("Test Show", 1, 1)
        assert len(results) == 0

    @patch("main.bsky_access_token", return_value=None)
    @patch("main.http.get")
    def test_uses_public_api_without_token(self, mock_get, _mock_token):
        mock_get.return_value = mock_response(200, {"posts": []})
        find_bluesky_posts("Test Show", 1, 1)
        call_url = mock_get.call_args[0][0]
        assert "public.api.bsky.app" in call_url


# ---------------------------------------------------------------------------
# find_pullpush_comments()
# ---------------------------------------------------------------------------


class TestFindPullpushComments:
    SUBMISSION_RESPONSE = {
        "data": [
            {
                "id": "abc123",
                "title": "Test Show S01E01 - Discussion Thread",
                "subreddit": "TestShow",
                "score": 500,
            },
        ],
    }

    COMMENT_RESPONSE = {
        "data": [
            {
                "id": "c1",
                "body": "Great episode!",
                "author": "user1",
                "created_utc": 1700000000,
                "score": 42,
                "parent_id": "t3_abc123",
            },
            {
                "id": "c2",
                "body": "I agree, the writing was superb",
                "author": "user2",
                "created_utc": 1700001000,
                "score": 15,
                "parent_id": "t1_c1",
            },
            {
                "id": "c3",
                "body": "Another top-level thought about the episode",
                "author": "user3",
                "created_utc": 1700002000,
                "score": 30,
                "parent_id": "t3_abc123",
            },
        ],
    }

    @patch("main.http.get")
    def test_returns_comments_with_reply_tree(self, mock_get):
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, self.SUBMISSION_RESPONSE)
            if "comment" in url:
                return mock_response(200, self.COMMENT_RESPONSE)
            return mock_response(200, {"data": []})

        mock_get.side_effect = side_effect
        results = find_pullpush_comments("Test Show", 1, 1)
        assert len(results) >= 1
        # All results should be top-level (parent_id = t3_)
        for r in results:
            assert r["source"] == "reddit"
            assert "parent_id" not in r  # popped during tree building

    @patch("main.http.get")
    def test_reply_attached_to_parent(self, mock_get):
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, self.SUBMISSION_RESPONSE)
            if "comment" in url:
                return mock_response(200, self.COMMENT_RESPONSE)
            return mock_response(200, {"data": []})

        mock_get.side_effect = side_effect
        results = find_pullpush_comments("Test Show", 1, 1)
        # c1 is top-level with c2 as a reply
        c1 = next((c for c in results if c["id"] == "c1"), None)
        assert c1 is not None
        assert len(c1["replies"]) == 1
        assert c1["replies"][0]["id"] == "c2"

    @patch("main.http.get")
    def test_deleted_comments_filtered(self, mock_get):
        comment_data = {
            "data": [
                {
                    "id": "c_del",
                    "body": "[deleted]",
                    "author": "[deleted]",
                    "created_utc": 1700000000,
                    "score": 0,
                    "parent_id": "t3_abc123",
                },
                {
                    "id": "c_rem",
                    "body": "[removed]",
                    "author": "[deleted]",
                    "created_utc": 1700000100,
                    "score": 0,
                    "parent_id": "t3_abc123",
                },
                {
                    "id": "c_ok",
                    "body": "This is a real comment about the episode",
                    "author": "realuser",
                    "created_utc": 1700000200,
                    "score": 10,
                    "parent_id": "t3_abc123",
                },
            ],
        }

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, self.SUBMISSION_RESPONSE)
            if "comment" in url:
                return mock_response(200, comment_data)
            return mock_response(200, {"data": []})

        mock_get.side_effect = side_effect
        results = find_pullpush_comments("Test Show", 1, 1)
        ids = [c["id"] for c in results]
        assert "c_del" not in ids
        assert "c_rem" not in ids
        assert "c_ok" in ids

    @patch("main.http.get")
    def test_episode_title_filtering(self, mock_get):
        """Submissions that don't match the episode code are filtered out."""
        wrong_ep_submissions = {
            "data": [
                {
                    "id": "wrong1",
                    "title": "Test Show S01E05 - Wrong Episode",
                    "subreddit": "TestShow",
                    "score": 100,
                },
            ],
        }

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, wrong_ep_submissions)
            return mock_response(200, {"data": []})

        mock_get.side_effect = side_effect
        results = find_pullpush_comments("Test Show", 1, 1)
        assert results == []

    @patch("main.http.get")
    def test_empty_results(self, mock_get):
        mock_get.return_value = mock_response(200, {"data": []})
        results = find_pullpush_comments("Test Show", 1, 1)
        assert results == []

    @patch("main.http.get")
    def test_network_error_returns_empty(self, mock_get):
        mock_get.side_effect = Exception("Connection refused")
        results = find_pullpush_comments("Test Show", 1, 1)
        assert results == []

    @patch("main.http.get")
    def test_thread_url_built_correctly(self, mock_get):
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, self.SUBMISSION_RESPONSE)
            if "comment" in url:
                return mock_response(200, self.COMMENT_RESPONSE)
            return mock_response(200, {"data": []})

        mock_get.side_effect = side_effect
        results = find_pullpush_comments("Test Show", 1, 1)
        assert len(results) >= 1
        assert "reddit.com/r/TestShow/comments/abc123/" in results[0]["thread_url"]

    @patch("main.http.get")
    def test_submission_dedup_across_searches(self, mock_get):
        """Same submission ID from multiple search queries is only processed once."""
        call_count = {"comment": 0}

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, self.SUBMISSION_RESPONSE)
            if "comment" in url:
                call_count["comment"] += 1
                return mock_response(200, self.COMMENT_RESPONSE)
            return mock_response(200, {"data": []})

        mock_get.side_effect = side_effect
        find_pullpush_comments("Test Show", 1, 1)
        # Comment fetch should happen only once per unique submission
        assert call_count["comment"] == 1
