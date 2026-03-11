"""Tests for PullPush.io Reddit comment integration."""

from unittest.mock import patch

from main import find_pullpush_comments
from tests.helpers import mock_response

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


def _make_comment(
    cid, body, author="user1", parent_id="t3_abc123", score=10, created_utc=1700000000
):
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
        _make_comment(
            "c2", "Totally agree, best of the season", parent_id="t1_c1", score=20
        ),
        _make_comment("c3", "Same here!", parent_id="t1_c1", score=5),
        _make_comment("c4", "The ending though...", score=40),
    ]
}

COMMENTS_DEEP_NEST = {
    "data": [
        _make_comment("c1", "Top-level comment", score=50),
        _make_comment("c2", "Reply to c1", parent_id="t1_c1", score=20),
        _make_comment(
            "c3", "Reply to c2 (orphaned to top-level)", parent_id="t1_c2", score=10
        ),
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

        with patch("main.http.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        assert len(results) == 2
        assert results[0]["comment"] == "This episode was incredible!"
        assert results[0]["source"] == "reddit"
        assert (
            results[0]["thread_title"] == "The Bear - S02E05 - Pop - Episode Discussion"
        )
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

        with patch("main.http.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        # Two top-level comments: c1 (score 50) and c4 (score 40)
        assert len(results) == 2
        top = results[0]
        assert top["comment"] == "This episode was incredible!"
        assert len(top["replies"]) == 2
        # Replies sorted by score descending
        assert top["replies"][0]["comment"] == "Totally agree, best of the season"
        assert top["replies"][1]["comment"] == "Same here!"

    def test_deep_nesting_preserved(self, fresh_cache):
        """Replies nest under their parent even when multiple levels deep."""

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, COMMENTS_DEEP_NEST)

        with patch("main.http.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        top = next(r for r in results if r["id"] == "c1")
        assert len(top["replies"]) == 1
        assert top["replies"][0]["id"] == "c2"
        # c3's parent (t1_c2) is in by_id, so c3 nests under c2
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
            return mock_response(
                200,
                {"data": [_make_comment("d1", "Great discussion here too", score=15)]},
            )

        with patch("main.http.get", side_effect=side_effect):
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

        with patch("main.http.get", side_effect=side_effect):
            find_pullpush_comments("The Bear", 2, 5, max_threads=1)

        comment_calls = [c for c in calls if "comment" in c]
        assert len(comment_calls) == 1

    def test_created_at_format(self, fresh_cache):
        """created_at is formatted as ISO 8601 with Z suffix."""

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(
                200, {"data": [_make_comment("c1", "Test", created_utc=1700000000)]}
            )

        with patch("main.http.get", side_effect=side_effect):
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

        with patch("main.http.get", side_effect=side_effect):
            find_pullpush_comments("The Bear", 2, 5)


# ---------------------------------------------------------------------------
# find_pullpush_comments — failure states
# ---------------------------------------------------------------------------


class TestFindPullpushFailures:
    def test_submission_search_http_error(self, fresh_cache):
        """Non-200 from submission search returns empty list."""
        with patch("main.http.get", return_value=mock_response(500)):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_submission_search_timeout(self, fresh_cache):
        """Network timeout on submission search returns empty list."""
        with patch("main.http.get", side_effect=Exception("Connection timed out")):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_no_submissions_found(self, fresh_cache):
        """Empty submission results return empty list."""
        with patch("main.http.get", return_value=mock_response(200, {"data": []})):
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
            return mock_response(
                200, {"data": [_make_comment("d1", "From second thread", score=10)]}
            )

        with patch("main.http.get", side_effect=side_effect):
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

        with patch("main.http.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_malformed_submission_response(self, fresh_cache):
        """Missing 'data' key in submission response returns empty list."""
        with patch(
            "main.http.get", return_value=mock_response(200, {"weird": "response"})
        ):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_malformed_comment_response(self, fresh_cache):
        """Missing 'data' key in comment response skips thread."""

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, {"weird": "response"})

        with patch("main.http.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []

    def test_deleted_author_preserved(self, fresh_cache):
        """Comments from [deleted] authors are still returned."""

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(
                200,
                {
                    "data": [
                        _make_comment(
                            "c1", "I was wrong about this", author="[deleted]"
                        )
                    ]
                },
            )

        with patch("main.http.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        assert len(results) == 1
        assert results[0]["user"]["username"] == "[deleted]"

    def test_deleted_body_filtered_out(self, fresh_cache):
        """Comments with [deleted] or [removed] body text are excluded."""

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(
                200,
                {
                    "data": [
                        _make_comment("c1", "[deleted]", score=50),
                        _make_comment("c2", "[removed]", score=30),
                        _make_comment("c3", "Actual comment", score=10),
                    ]
                },
            )

        with patch("main.http.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)

        assert len(results) == 1
        assert results[0]["comment"] == "Actual comment"

    def test_empty_comments_for_thread(self, fresh_cache):
        """Thread with no comments is skipped without error."""

        def side_effect(url, **kwargs):
            if "submission" in url:
                return mock_response(200, SUBMISSION_HIT)
            return mock_response(200, {"data": []})

        with patch("main.http.get", side_effect=side_effect):
            results = find_pullpush_comments("The Bear", 2, 5)
        assert results == []
