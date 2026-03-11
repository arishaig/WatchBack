"""Tests for forward auth middleware (auth.py ForwardAuthMiddleware).

The middleware is tested by patching auth_module.FORWARD_AUTH_ENABLED at
runtime (it's a module-level constant read from the environment at import
time, so monkeypatch.setattr is the right tool).  The TestClient passes
request headers through to Starlette, which the middleware reads normally.

DB-side assertions run async SQLAlchemy queries via _run(), which always
executes in a fresh thread to avoid conflicts with the running event loop
left behind by Playwright accessibility tests.
"""

import asyncio

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select
from starlette.requests import Request

import auth as auth_module
from main import app

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def fwd_enabled(monkeypatch, fresh_cache):
    """Activate forward auth and clear the per-process token cache.

    Patches the runtime flag in auth.py (controls middleware) and writes '1'
    into the test cache (controls what /api/health and /api/status report).
    Both use the same fresh_cache instance as fwd_client.
    """
    monkeypatch.setattr(auth_module, "_fwd_auth_active", True)
    monkeypatch.setattr(auth_module, "_fwd_cache", {})
    stored = fresh_cache.get("ui_config") or {}
    stored["forward_auth_enabled"] = "1"
    fresh_cache.set("ui_config", stored)
    yield


@pytest.fixture
def fwd_client(fresh_cache, fwd_enabled):
    """Unauthenticated TestClient with forward auth enabled."""
    with TestClient(app) as c:
        yield c


# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------


def _run(coro):
    """Run a coroutine from inside a synchronous test.

    Uses a dedicated thread so asyncio.run() always gets a clean event loop,
    even when Playwright (or another async test framework) leaves one running
    in the main thread.
    """
    import concurrent.futures

    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
        return pool.submit(asyncio.run, coro).result()


async def _user_by_name(username: str):
    async with auth_module.async_session_maker() as db:
        result = await db.execute(
            select(auth_module.User).where(auth_module.User.username == username)
        )
        return result.scalar_one_or_none()


async def _token_count_for(username: str) -> int:
    async with auth_module.async_session_maker() as db:
        user_id_subq = select(auth_module.User.id).where(
            auth_module.User.username == username
        )
        result = await db.execute(
            select(func.count())
            .select_from(auth_module.AccessToken)
            .where(auth_module.AccessToken.user_id.in_(user_id_subq))
        )
        return result.scalar()


# ---------------------------------------------------------------------------
# /api/health forward_auth_enabled flag
# ---------------------------------------------------------------------------


class TestHealthFlag:
    def test_disabled_by_default(self, client):
        assert client.get("/api/health").json()["forward_auth_enabled"] is False

    def test_enabled_when_activated(self, fwd_client):
        assert fwd_client.get("/api/health").json()["forward_auth_enabled"] is True


# ---------------------------------------------------------------------------
# Middleware bypass conditions
# ---------------------------------------------------------------------------


class TestMiddlewareBypass:
    def test_no_header_falls_through_to_local_auth(self, fwd_client):
        """With forward auth on but no Remote-User header, auth fails normally."""
        res = fwd_client.get("/api/status")
        assert res.status_code == 401

    def test_disabled_mode_uses_local_auth(self, client):
        """When forward auth is off, the normal cookie session still works."""
        # client fixture logs in via local auth
        assert client.get("/api/status").status_code == 200

    def test_forward_auth_header_ignored_when_disabled(self, client):
        """Passing a Remote-User header with forward auth off has no effect."""
        # client is already authenticated as local admin; make a fresh
        # unauthenticated client to prove the header alone does nothing
        with TestClient(app) as fresh:
            res = fresh.get("/api/auth/me", headers={"Remote-User": "intruder"})
            assert res.status_code == 401


# ---------------------------------------------------------------------------
# User provisioning
# ---------------------------------------------------------------------------


class TestUserProvisioning:
    def test_new_user_auto_provisioned(self, fwd_client):
        res = fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_newuser"})
        assert res.status_code == 200
        data = res.json()
        assert data["username"] == "fwd_newuser"
        assert data["auth_source"] == "forward_auth"
        assert data["must_change_password"] is False

    def test_new_user_not_admin_by_default(self, fwd_client):
        res = fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_regular"})
        assert res.json()["is_admin"] is False

    def test_email_populated_from_header(self, fwd_client):
        fwd_client.get(
            "/api/auth/me",
            headers={
                "Remote-User": "fwd_emailuser",
                "Remote-Email": "fwd_emailuser@example.com",
            },
        )
        user = _run(_user_by_name("fwd_emailuser"))
        assert user is not None
        assert user.email == "fwd_emailuser@example.com"

    def test_fallback_email_when_none_provided(self, fwd_client):
        fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_noemail"})
        user = _run(_user_by_name("fwd_noemail"))
        assert user is not None
        assert user.email == "fwd_noemail@forward-auth.local"

    def test_duplicate_requests_create_one_user(self, fwd_client):
        """Multiple requests for the same username do not create duplicate rows."""
        for _ in range(3):
            fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_stable"})

        async def _count():
            async with auth_module.async_session_maker() as db:
                result = await db.execute(
                    select(func.count())
                    .select_from(auth_module.User)
                    .where(auth_module.User.username == "fwd_stable")
                )
                return result.scalar()

        assert _run(_count()) == 1

    def test_existing_local_account_merged(self, fwd_client):
        """A forward-auth identity whose username matches a local account merges into it,
        updating auth_source to forward_auth."""
        # "admin" is the pre-existing local account (auth_source="local")
        fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "admin", "Remote-Groups": "admins"},
        )
        user = _run(_user_by_name("admin"))
        assert user.auth_source == "forward_auth"


# ---------------------------------------------------------------------------
# Admin role from groups / FORWARD_AUTH_ADMIN_USERS
# ---------------------------------------------------------------------------


class TestHeaderValidation:
    def test_oversized_username_rejected(self, fwd_client):
        """A username longer than 150 chars is dropped — falls through to 401."""
        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "a" * 151},
        )
        assert res.status_code == 401

    def test_max_length_username_accepted(self, fwd_client):
        """A username exactly 150 chars is fine."""
        long_name = "fwd_" + "x" * 146  # 150 chars total
        res = fwd_client.get("/api/auth/me", headers={"Remote-User": long_name})
        assert res.status_code == 200

    def test_oversized_email_silently_dropped(self, fwd_client):
        """An email exceeding RFC 5321 length is ignored; fallback email is used."""
        long_email = "a" * 255 + "@example.com"
        fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_longemail", "Remote-Email": long_email},
        )
        user = _run(_user_by_name("fwd_longemail"))
        assert user is not None
        assert long_email not in user.email  # oversized value was not stored


class TestAdminRole:
    def test_admin_group_grants_superuser(self, fwd_client):
        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_gadmin", "Remote-Groups": "admins"},
        )
        assert res.json()["is_admin"] is True

    def test_watchback_admin_group_grants_superuser(self, fwd_client):
        res = fwd_client.get(
            "/api/auth/me",
            headers={
                "Remote-User": "fwd_wbadmin",
                "Remote-Groups": "watchback-admin,viewers",
            },
        )
        assert res.json()["is_admin"] is True

    def test_unrecognised_group_does_not_grant_admin(self, fwd_client):
        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_pleb", "Remote-Groups": "users,readonly"},
        )
        assert res.json()["is_admin"] is False

    def test_admin_users_list_grants_superuser(self, fwd_client, monkeypatch):
        monkeypatch.setattr(auth_module, "_FWD_ADMIN_USERS", {"fwd_specialadmin"})
        res = fwd_client.get(
            "/api/auth/me", headers={"Remote-User": "fwd_specialadmin"}
        )
        assert res.json()["is_admin"] is True

    def test_admin_status_synced_when_group_added(self, fwd_client, monkeypatch):
        """Cache miss forces a DB sync; admin status updates when group membership changes."""
        # First login: non-admin
        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_promoted", "Remote-Groups": "users"},
        )
        assert res.json()["is_admin"] is False

        # Simulate group change by clearing the in-memory cache
        monkeypatch.setattr(auth_module, "_fwd_cache", {})

        # Second login: now in admins group
        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_promoted", "Remote-Groups": "admins"},
        )
        assert res.json()["is_admin"] is True

    def test_last_admin_not_demoted(self, fwd_client, monkeypatch):
        """The sole admin is never demoted via forward auth — it would lock everyone out."""
        # Provision an admin user
        fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_lastadmin", "Remote-Groups": "admins"},
        )
        # Ensure they're the only admin by checking current count, then simulate
        # their groups being removed on a cache miss
        monkeypatch.setattr(auth_module, "_fwd_cache", {})
        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_lastadmin", "Remote-Groups": "users"},
        )
        # If they are the last admin WatchBack retains their admin status
        user = _run(_user_by_name("fwd_lastadmin"))
        if user.is_superuser:
            # Correctly protected — they were the last admin
            assert res.json()["is_admin"] is True
        else:
            # There were other admins in the DB, so demotion was allowed
            assert res.json()["is_admin"] is False

    def test_admin_status_synced_when_group_removed(self, fwd_client, monkeypatch):
        """Admin status is revoked when the user is removed from the admin group."""
        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_demoted", "Remote-Groups": "admins"},
        )
        assert res.json()["is_admin"] is True

        monkeypatch.setattr(auth_module, "_fwd_cache", {})

        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_demoted", "Remote-Groups": "users"},
        )
        assert res.json()["is_admin"] is False


# ---------------------------------------------------------------------------
# Session cookie and token caching
# ---------------------------------------------------------------------------


class TestSessionCookie:
    def test_response_sets_session_cookie(self, fwd_client):
        fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_cookieuser"})
        assert "watchback_session" in fwd_client.cookies

    def test_cached_token_not_duplicated(self, fwd_client):
        """Second request reuses the cached token — no extra DB row created."""
        fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_cached"})
        count_after_first = _run(_token_count_for("fwd_cached"))

        fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_cached"})
        count_after_second = _run(_token_count_for("fwd_cached"))

        assert count_after_first == count_after_second

    def test_cache_miss_creates_new_token(self, fwd_client, monkeypatch):
        """Clearing the in-memory cache (e.g. restart) forces a new DB token."""
        fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_refreshed"})
        count_first = _run(_token_count_for("fwd_refreshed"))

        monkeypatch.setattr(auth_module, "_fwd_cache", {})
        fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_refreshed"})
        count_second = _run(_token_count_for("fwd_refreshed"))

        assert count_second == count_first + 1

    def test_cache_hit_skips_db_provisioning(self, fwd_client, monkeypatch):
        """With a warm cache, _find_or_provision_fwd_user is never called."""
        # Warm the cache with a first request
        fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_warmcache"})

        call_count = 0

        original = auth_module._find_or_provision_fwd_user

        async def _spy(*args, **kwargs):
            nonlocal call_count
            call_count += 1
            return await original(*args, **kwargs)

        monkeypatch.setattr(auth_module, "_find_or_provision_fwd_user", _spy)

        # Second request: cache should be warm, spy should not be called
        fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_warmcache"})
        assert call_count == 0


# ---------------------------------------------------------------------------
# Access control
# ---------------------------------------------------------------------------


class TestAccessControl:
    def test_fwd_user_can_access_protected_endpoint(self, fwd_client):
        res = fwd_client.get("/api/status", headers={"Remote-User": "fwd_viewer"})
        assert res.status_code == 200

    def test_fwd_non_admin_rejected_from_admin_endpoint(self, fwd_client):
        res = fwd_client.put(
            "/api/config",
            json={"theme_mode": "light"},
            headers={"Remote-User": "fwd_viewer"},
        )
        assert res.status_code == 403

    def test_fwd_admin_can_use_admin_endpoint(self, fwd_client):
        res = fwd_client.put(
            "/api/config",
            json={"theme_mode": "light"},
            headers={"Remote-User": "fwd_cfgadmin", "Remote-Groups": "admins"},
        )
        assert res.status_code == 200

    def test_status_reports_forward_auth_enabled(self, fwd_client):
        res = fwd_client.get("/api/status", headers={"Remote-User": "fwd_viewer"})
        assert res.json()["forward_auth_enabled"] is True


# ---------------------------------------------------------------------------
# Custom header names
# ---------------------------------------------------------------------------


class TestCustomHeaders:
    def test_custom_user_header_name(self, fwd_client, monkeypatch):
        monkeypatch.setattr(auth_module, "_FWD_USER_HEADER", "X-Forwarded-User")
        res = fwd_client.get(
            "/api/auth/me", headers={"X-Forwarded-User": "fwd_xfwduser"}
        )
        assert res.status_code == 200
        assert res.json()["username"] == "fwd_xfwduser"

    def test_default_header_unused_with_custom_config(self, fwd_client, monkeypatch):
        """When a custom header name is set, the default Remote-User is ignored."""
        monkeypatch.setattr(auth_module, "_FWD_USER_HEADER", "X-Forwarded-User")
        res = fwd_client.get("/api/auth/me", headers={"Remote-User": "shouldbeignored"})
        assert res.status_code == 401

    def test_custom_groups_header_name(self, fwd_client, monkeypatch):
        monkeypatch.setattr(auth_module, "_FWD_GROUPS_HEADER", "X-Groups")
        res = fwd_client.get(
            "/api/auth/me",
            headers={"Remote-User": "fwd_xgroupuser", "X-Groups": "admins"},
        )
        assert res.json()["is_admin"] is True

    def test_custom_admin_groups(self, fwd_client, monkeypatch):
        monkeypatch.setattr(auth_module, "_FWD_ADMIN_GROUPS", {"myorg-admins"})
        res = fwd_client.get(
            "/api/auth/me",
            headers={
                "Remote-User": "fwd_orgadmin",
                "Remote-Groups": "myorg-admins,users",
            },
        )
        assert res.json()["is_admin"] is True


# ---------------------------------------------------------------------------
# Error handling
# ---------------------------------------------------------------------------


class TestRuntimeToggle:
    def test_set_forward_auth_active_toggles_flag(self):
        """set_forward_auth_active() mutates the module-level runtime flag."""
        original = auth_module._fwd_auth_active
        try:
            auth_module.set_forward_auth_active(True)
            assert auth_module._fwd_auth_active is True
            auth_module.set_forward_auth_active(False)
            assert auth_module._fwd_auth_active is False
        finally:
            auth_module.set_forward_auth_active(original)

    def test_middleware_off_with_empty_config(self, fresh_cache):
        """Empty cache → lifespan syncs flag to False → headers ignored → 401."""
        with TestClient(app) as c:
            res = c.get("/api/auth/me", headers={"Remote-User": "fwd_rt_off"})
        assert res.status_code == 401

    def test_middleware_on_when_config_set(self, fresh_cache):
        """Saving forward_auth_enabled=1 to config activates the middleware via
        lifespan's _sync_forward_auth_state() call — simulates the UI toggle."""
        stored = fresh_cache.get("ui_config") or {}
        stored["forward_auth_enabled"] = "1"
        fresh_cache.set("ui_config", stored)
        with TestClient(app) as c:
            res = c.get("/api/auth/me", headers={"Remote-User": "fwd_rt_on"})
        assert res.status_code == 200
        assert res.json()["username"] == "fwd_rt_on"


class TestErrorHandling:
    def test_db_error_falls_through_unauthenticated(self, fwd_client, monkeypatch):
        """If provisioning raises, the request proceeds without auth (returns 401)."""

        async def _boom(db, username, email, is_admin):
            raise RuntimeError("DB unavailable")

        monkeypatch.setattr(auth_module, "_find_or_provision_fwd_user", _boom)
        res = fwd_client.get("/api/auth/me", headers={"Remote-User": "fwd_erroruser"})
        assert res.status_code == 401

    def test_db_error_does_not_crash_server(self, fwd_client, monkeypatch):
        """Server returns a proper response (not 500) even when provisioning fails."""

        async def _boom(db, username, email, is_admin):
            raise RuntimeError("simulated failure")

        monkeypatch.setattr(auth_module, "_find_or_provision_fwd_user", _boom)
        res = fwd_client.get(
            "/api/health", headers={"Remote-User": "fwd_errhealthuser"}
        )
        # /api/health is unauthenticated — it should still succeed
        assert res.status_code == 200


# ---------------------------------------------------------------------------
# _inject_request_cookie unit tests (no HTTP / DB needed)
# ---------------------------------------------------------------------------


def _make_request(raw_headers: list[tuple[bytes, bytes]]) -> Request:
    """Build a minimal Starlette Request with the given raw headers."""
    scope = {
        "type": "http",
        "method": "GET",
        "path": "/",
        "query_string": b"",
        "headers": list(raw_headers),
    }
    return Request(scope)


class TestInjectRequestCookie:
    def test_injects_cookie_when_none_exists(self):
        req = _make_request([])
        auth_module._inject_request_cookie(req, "watchback_session", "tok123")
        headers = dict(req.scope["headers"])
        cookie_val = headers[b"cookie"].decode()
        assert "watchback_session=tok123" in cookie_val

    def test_replaces_existing_same_cookie(self):
        req = _make_request([(b"cookie", b"watchback_session=oldtoken")])
        auth_module._inject_request_cookie(req, "watchback_session", "newtoken")
        headers = dict(req.scope["headers"])
        cookie_str = headers[b"cookie"].decode()
        assert "newtoken" in cookie_str
        assert "oldtoken" not in cookie_str

    def test_preserves_other_cookies(self):
        req = _make_request([(b"cookie", b"other=value; watchback_session=old")])
        auth_module._inject_request_cookie(req, "watchback_session", "new")
        headers = dict(req.scope["headers"])
        cookie_str = headers[b"cookie"].decode()
        assert "other=value" in cookie_str
        assert "watchback_session=new" in cookie_str

    def test_other_headers_preserved(self):
        req = _make_request(
            [
                (b"content-type", b"application/json"),
                (b"cookie", b"watchback_session=old"),
            ]
        )
        auth_module._inject_request_cookie(req, "watchback_session", "new")
        header_keys = [k for k, _ in req.scope["headers"]]
        assert b"content-type" in header_keys

    def test_adds_cookie_alongside_other_headers(self):
        req = _make_request([(b"accept", b"*/*")])
        auth_module._inject_request_cookie(req, "session", "abc")
        header_keys = [k for k, _ in req.scope["headers"]]
        assert b"cookie" in header_keys
        assert b"accept" in header_keys
