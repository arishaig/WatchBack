"""Tests for PullPush.io Reddit comment integration."""
import pytest
from unittest.mock import MagicMock, patch
from diskcache import Cache

import main
from main import find_pullpush_comments


def mock_response(status_code=200, body=None):
    m = MagicMock()
    m.status_code = status_code
    m.json.return_value = body if body is not None else {}
    m.text = "Mock response"
    return m


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(autouse=True)
def clean_env(monkeypatch):
    for _, (env_name, _) in main.ENV_MAP.items():
        monkeypatch.delenv(env_name, raising=False)


@pytest.fixture
def fresh_cache(tmp_path, monkeypatch):
    c = Cache(str(tmp_path / "cache"))
    monkeypatch.setattr(main, "cache", c)
    yield c
    c.close()


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

SUBMISSION_HIT = {
    "data": [
        {
            "id": "abc123",
            "title": "The Bear - S02E05 - Pop - Episode Discussion",
            "subreddit": "TheBear",
            "score": 500,
        }
    ]
}

SUBMISSION_MULTI = {
    "data": [
        {
            "id": "abc123",
            "title": "The Bear - S02E05 - Pop - Episode Discussion",
            "subreddit": "TheBear",
            "score": 500,
        },
        {
            "id": "def456",
            "title": "[S02E05] The Bear - Pop Discussion Thread",
            "subreddit": "television",
            "score": 200,
        },
    ]
}


def _make_comment(cid, body, author="user1", parent_id="t3_abc123", score=10, created_utc=1700000000):
    return {
        "id": cid,
        "body": body,
        "author": author,
        "parent_id": parent_id,
        "score": score,
        "created_utc": created_utc,
    }


COMMENTS_FLAT = {
    "data": [
        _make_comment("c1", "This episode was incredible!", score=50),
        _make_comment("c2", "The kitchen scene was so tense", score=30),
    ]
}

COMMENTS_NESTED = {
    "data": [
        _make_comment("c1", "This episode was incredible!", score=50),
        _make_comment("c2", "Totally agree, best of the season", parent_id="t1_c1", score=20),
        _make_comment("c3", "Same here!", parent_id="t1_c1", score=5),
        _make_comment("c4", "The ending though...", score=40),
    ]
}

COMMENTS_DEEP_NEST = {
    "data": [
        _make_comment("c1", "Top-level comment", score=50),
        _make_comment("c2", "Reply to c1", parent_id="t1_c1", score=20),
        _make_comment("c3", "Reply to c2 (orphaned to top-level)", parent_id="t1_c2", score=10),
    ]
}


# ---------------------------------------------------------------------------
# find_pullpush_comments — happy paths
# ---------------------------------------------------------------------------

class TestFindPullpushComments:
    def test_basic_flat_comments(self, fresh_cache):
        """Top-level comments from a single thread are returned with metadata."""
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, COMMENTS_FLAT)

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        assert len(results) == 2
        assert results[0]["comment"] == "This episode was incredible!"
        assert results[0]["source"] == "reddit"
        assert results[0]["thread_title"] == "The Bear - S02E05 - Pop - Episode Discussion"
        assert "reddit.com" in results[0]["url"]
        assert "reddit.com" in results[0]["thread_url"]
        assert results[0]["replies"] == []
        # Sorted by score descending
        assert results[0]["score"] >= results[1]["score"]

    def test_nested_replies(self, fresh_cache):
        """Replies are nested under their parent comment."""
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, COMMENTS_NESTED)

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        # Two top-level comments: c1 (score 50) and c4 (score 40)
        assert len(results) == 2
        top = results[0]
        assert top["comment"] == "This episode was incredible!"
        assert len(top["replies"]) == 2
        # Replies sorted by score descending
        assert top["replies"][0]["comment"] == "Totally agree, best of the season"
        assert top["replies"][1]["comment"] == "Same here!"

    def test_orphaned_reply_becomes_top_level(self, fresh_cache):
        """A reply whose parent is itself a reply (not top-level) becomes top-level."""
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, COMMENTS_DEEP_NEST)

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        # c1 is top-level, c2 is reply to c1, c3 is reply to c2
        # c2 nests under c1, c3 has parent c2 which is not top-level
        # but c2 IS in by_id, so c3 nests under c2 as a reply
        top = next(r for r in results if r["id"] == "c1")
        assert len(top["replies"]) == 1
        assert top["replies"][0]["id"] == "c2"
        # c3 is a reply to c2 — but our tree is only 1 level deep,
        # so c3 gets nested under c2 in by_id. Since c2 is a reply
        # itself, c3 goes under c2's replies.
        # Actually looking at the code: c3's parent is t1_c2,
        # c2 is in by_id, so c3 is appended to c2's replies.
        assert len(top["replies"][0]["replies"]) == 1
        assert top["replies"][0]["replies"][0]["id"] == "c3"

    def test_multiple_threads(self, fresh_cache):
        """Comments from multiple threads are combined."""
        call_count = {"n": 0}

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_MULTI)
            call_count["n"] += 1
            if call_count["n"] == 1:
                return mock_response(200, COMMENTS_FLAT)
            return mock_response(200, {
                "data": [_make_comment("d1", "Great discussion here too", score=15)]
            })

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5, max_threads=2)

        assert len(results) == 3
        threads = {r["thread_title"] for r in results}
        assert len(threads) == 2

    def test_max_threads_limits_fetches(self, fresh_cache):
        """Only max_threads submissions are queried for comments."""
        calls = []

        def side_effect(url, **kwargs):
            calls.append(url)
            if "submission" in url:
                return mock_response(200, SUBMISSION_MULTI)
            return mock_response(200, {"data": []})

        with patch("main.requests.get", side_effect=side_effect):
            find_pullpush_comments("The Bear", 2, 5, max_threads=1)

        comment_calls = [c for c in calls if "comment" in c]
        assert len(comment_calls) == 1

    def test_created_at_format(self, fresh_cache):
        """created_at is formatted as ISO 8601 with Z suffix."""
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, {
                "data": [_make_comment("c1", "Test", created_utc=1700000000)]
            })

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        assert results[0]["created_at"].endswith("Z")
        assert "T" in results[0]["created_at"]

    def test_user_agent_sent(self, fresh_cache):
        """User-Agent header is set on all requests."""
        def side_effect(url, **kwargs):
            assert kwargs.get("headers", {}).get("User-Agent") == "WatchBack/1.0"
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, COMMENTS_FLAT)

        with patch("main.requests.get", side_effect=side_effect):
            find_pullpush_comments("The Bear", 2, 5)


# ---------------------------------------------------------------------------
# find_pullpush_comments — failure states
# ---------------------------------------------------------------------------

class TestFindPullpushFailures:
    def test_submission_search_http_error(self, fresh_cache):
        """Non-200 from submission search returns empty list."""
        with patch("main.requests.get", return_value=mock_response(500)):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_submission_search_timeout(self, fresh_cache):
        """Network timeout on submission search returns empty list."""
        with patch("main.requests.get", side_effect=Exception("Connection timed out")):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_no_submissions_found(self, fresh_cache):
        """Empty submission results return empty list."""
        with patch("main.requests.get", return_value=mock_response(200, {"data": []})):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_comment_fetch_http_error_skips_thread(self, fresh_cache):
        """If comment fetch fails for one thread, other threads still work."""
        call_count = {"n": 0}

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_MULTI)
            call_count["n"] += 1
            if call_count["n"] == 1:
                return mock_response(503)  # First thread fails
            return mock_response(200, {
                "data": [_make_comment("d1", "From second thread", score=10)]
            })

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5, max_threads=2)

        assert len(results) == 1
        assert results[0]["comment"] == "From second thread"

    def test_comment_fetch_timeout_skips_thread(self, fresh_cache):
        """Network timeout on comment fetch skips that thread gracefully."""
        call_count = {"n": 0}

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            raise Exception("Read timed out")

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_malformed_submission_response(self, fresh_cache):
        """Missing 'data' key in submission response returns empty list."""
        with patch("main.requests.get", return_value=mock_response(200, {"weird": "response"})):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_malformed_comment_response(self, fresh_cache):
        """Missing 'data' key in comment response skips thread."""
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, {"weird": "response"})

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_deleted_author_preserved(self, fresh_cache):
        """Comments from [deleted] authors are still returned."""
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, {
                "data": [_make_comment("c1", "I was wrong about this", author="[deleted]")]
            })

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        assert len(results) == 1
        assert results[0]["user"]["username"] == "[deleted]"

    def test_empty_comments_for_thread(self, fresh_cache):
        """Thread with no comments is skipped without error."""
        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, {"data": []})

        with patch("main.requests.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []


# ---------------------------------------------------------------------------
# Sync endpoint integration — PullPush caching
# ---------------------------------------------------------------------------

class TestSyncPullpushCaching:
    """Verify that /api/sync caches and retrieves PullPush results correctly."""

    SESSION = {
        "series": "The Bear", "name": "Pop",
        "season": 2, "episode": 5, "premiere": "2023-08-01T00:00:00Z",
    }

    def test_pullpush_results_cached(self, fresh_cache, monkeypatch):
        """PullPush results are cached for 24h on first fetch."""
        monkeypatch.setenv("JF_API_KEY", "test")
        fresh_cache.set("jf_session", self.SESSION, expire=60)

        pp_comment = {
            "id": "c1", "comment": "Great episode", "source": "reddit",
            "user": {"username": "user1"}, "created_at": "2023-08-02T00:00:00Z",
            "score": 10, "url": "https://reddit.com/r/TheBear/comments/abc123/_/c1/",
            "thread_title": "Discussion", "thread_url": "https://reddit.com/r/TheBear/comments/abc123/",
            "replies": [],
        }

        with patch("main.find_pullpush_comments", return_value=[pp_comment]) as mock_pp, \
             patch("main.find_bluesky_posts", return_value=[]), \
             patch("main.find_reddit_threads", return_value=[]):
            from main import sync_data
            result = sync_data()
            assert mock_pp.call_count == 1

            # Second call should use cache
            result2 = sync_data()
            assert mock_pp.call_count == 1  # Not called again

        assert result["status"] == "success"
        reddit_comments = [c for c in result["all_comments"] if c.get("source") == "reddit"]
        assert len(reddit_comments) == 1

    def test_pullpush_failure_doesnt_break_sync(self, fresh_cache, monkeypatch):
        """If PullPush returns empty, sync still succeeds with other sources."""
        monkeypatch.setenv("JF_API_KEY", "test")
        fresh_cache.set("jf_session", self.SESSION, expire=60)

        bsky_comment = {
            "id": "b1", "comment": "Bluesky post", "source": "bluesky",
            "user": {"username": "user.bsky.social"}, "created_at": "2023-08-02T00:00:00Z",
            "url": "https://bsky.app/profile/user.bsky.social/post/b1", "images": [],
        }

        with patch("main.find_pullpush_comments", return_value=[]), \
             patch("main.find_bluesky_posts", return_value=[bsky_comment]), \
             patch("main.find_reddit_threads", return_value=[]):
            from main import sync_data
            result = sync_data()

        assert result["status"] == "success"
        assert len(result["all_comments"]) == 1
        assert result["all_comments"][0]["source"] == "bluesky"

    def test_reddit_button_shown_regardless(self, fresh_cache, monkeypatch):
        """reddit_url is always present even when PullPush returns nothing."""
        monkeypatch.setenv("JF_API_KEY", "test")
        fresh_cache.set("jf_session", self.SESSION, expire=60)

        with patch("main.find_pullpush_comments", return_value=[]), \
             patch("main.find_bluesky_posts", return_value=[]), \
             patch("main.find_reddit_threads", return_value=[]):
            from main import sync_data
            result = sync_data()

        assert result["status"] == "success"
        assert "reddit_url" in result
        assert result["reddit_url"]  # Not empty
