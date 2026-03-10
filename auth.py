"""
WatchBack Authentication Module

Provides user auth, session management, and admin functionality
using fastapi-users with SQLAlchemy + SQLite backend.
"""

import os
import time as _time
import uuid
import secrets
import logging
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, Request, status
from pydantic import BaseModel
from sqlalchemy import Boolean, DateTime, String, select, func
from sqlalchemy.ext.asyncio import (
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column
from starlette.middleware.base import BaseHTTPMiddleware

from fastapi_users import BaseUserManager, FastAPIUsers, UUIDIDMixin
from fastapi_users.authentication import AuthenticationBackend, CookieTransport
from fastapi_users.authentication.strategy.db import AccessTokenDatabase, DatabaseStrategy
from fastapi_users.password import PasswordHelper
from fastapi_users_db_sqlalchemy import SQLAlchemyBaseUserTableUUID, SQLAlchemyUserDatabase
from fastapi_users_db_sqlalchemy.access_token import (
    SQLAlchemyBaseAccessTokenTableUUID,
    SQLAlchemyAccessTokenDatabase,
)

logger = logging.getLogger(__name__)

# ─── Configuration ────────────────────────────────────────────────────────────

CONFIG_DIR = os.environ.get("CONFIG_DIR", "/config")
os.makedirs(CONFIG_DIR, exist_ok=True)


def _get_secret(name: str, env_var: str) -> str:
    """Resolve a secret: env var > persisted file > auto-generate."""
    val = os.environ.get(env_var, "")
    if val:
        return val
    path = os.path.join(CONFIG_DIR, f".{name}")
    if os.path.exists(path):
        with open(path) as f:
            return f.read().strip()
    val = secrets.token_urlsafe(32)
    with open(path, "w") as f:
        f.write(val)
    try:
        os.chmod(path, 0o600)
    except OSError:
        pass
    return val


SECRET_KEY = _get_secret("secret_key", "WATCHBACK_SECRET_KEY")
DATABASE_URL = f"sqlite+aiosqlite:///{os.path.join(CONFIG_DIR, 'watchback.db')}"

# ─── Forward Auth Configuration ───────────────────────────────────────────────

FORWARD_AUTH_ENABLED = os.environ.get("FORWARD_AUTH_ENABLED", "").lower() in ("1", "true")
_FWD_USER_HEADER = os.environ.get("FORWARD_AUTH_USER_HEADER", "Remote-User")
_FWD_EMAIL_HEADER = os.environ.get("FORWARD_AUTH_EMAIL_HEADER", "Remote-Email")
_FWD_GROUPS_HEADER = os.environ.get("FORWARD_AUTH_GROUPS_HEADER", "Remote-Groups")
_FWD_ADMIN_GROUPS = {
    g.strip().lower()
    for g in os.environ.get("FORWARD_AUTH_ADMIN_GROUPS", "admins,admin,watchback-admin").split(",")
    if g.strip()
}
_FWD_ADMIN_USERS = {
    u.strip()
    for u in os.environ.get("FORWARD_AUTH_ADMIN_USERS", "").split(",")
    if u.strip()
}

_COOKIE_NAME = "watchback_session"
_COOKIE_MAX_AGE = 86400 * 30
_COOKIE_SECURE = os.environ.get("WATCHBACK_SECURE_COOKIES", "").lower() in ("1", "true")

# In-memory cache: username -> (token_str, is_admin, expiry_monotonic)
_fwd_cache: dict[str, tuple[str, bool, float]] = {}
_FWD_CACHE_TTL = 86400 * 20  # 20 days (well within 30-day DB token lifetime)

# Runtime enable flag — initialised from env var, updated at runtime by main.py
# so that the UI setting takes effect without restart.
_fwd_auth_active: bool = FORWARD_AUTH_ENABLED


def set_forward_auth_active(value: bool) -> None:
    """Called by main.py to sync the runtime forward-auth state from config."""
    global _fwd_auth_active
    _fwd_auth_active = bool(value)

# ─── Database ─────────────────────────────────────────────────────────────────

engine = create_async_engine(DATABASE_URL)
async_session_maker = async_sessionmaker(engine, expire_on_commit=False)


class Base(DeclarativeBase):
    pass


# ─── Models ───────────────────────────────────────────────────────────────────


class User(SQLAlchemyBaseUserTableUUID, Base):
    __tablename__ = "user"

    username: Mapped[str] = mapped_column(
        String(150), unique=True, index=True, nullable=False
    )
    must_change_password: Mapped[bool] = mapped_column(
        Boolean, default=False, server_default="0"
    )
    auth_source: Mapped[str] = mapped_column(
        String(20), default="local", server_default="local"
    )
    last_login_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=lambda: datetime.now(timezone.utc),
    )


class AccessToken(SQLAlchemyBaseAccessTokenTableUUID, Base):
    __tablename__ = "accesstoken"


# ─── Dependencies ─────────────────────────────────────────────────────────────


async def get_async_session():
    async with async_session_maker() as session:
        yield session


async def get_user_db(session: AsyncSession = Depends(get_async_session)):
    yield SQLAlchemyUserDatabase(session, User)


async def get_access_token_db(session: AsyncSession = Depends(get_async_session)):
    yield SQLAlchemyAccessTokenDatabase(session, AccessToken)


# ─── User Manager ─────────────────────────────────────────────────────────────


class UserManager(UUIDIDMixin, BaseUserManager[User, uuid.UUID]):
    reset_password_token_secret = SECRET_KEY
    verification_token_secret = SECRET_KEY

    async def authenticate(self, credentials):
        """Authenticate by username or email (overrides email-only default)."""
        stmt = select(User).where(
            (User.username == credentials.username)
            | (User.email == credentials.username)
        )
        result = await self.user_db.session.execute(stmt)
        user = result.scalar_one_or_none()

        if user is None or not user.is_active:
            self.password_helper.hash(credentials.password)
            return None

        verified, updated_hash = self.password_helper.verify_and_update(
            credentials.password, user.hashed_password
        )
        if not verified:
            return None
        if updated_hash:
            await self.user_db.update(user, {"hashed_password": updated_hash})
        return user

    async def on_after_login(self, user: User, request=None, response=None):
        await self.user_db.update(
            user, {"last_login_at": datetime.now(timezone.utc)}
        )
        logger.info("User logged in: %s", user.username)


async def get_user_manager(user_db=Depends(get_user_db)):
    yield UserManager(user_db)


# ─── Auth Backend ─────────────────────────────────────────────────────────────

cookie_transport = CookieTransport(
    cookie_name="watchback_session",
    cookie_max_age=86400 * 30,  # 30 days
    cookie_secure=os.environ.get("WATCHBACK_SECURE_COOKIES", "").lower()
    in ("1", "true"),
    cookie_httponly=True,
    cookie_samesite="lax",
)


def get_database_strategy(
    access_token_db: AccessTokenDatabase[AccessToken] = Depends(get_access_token_db),
) -> DatabaseStrategy:
    return DatabaseStrategy(access_token_db, lifetime_seconds=86400 * 30)


auth_backend = AuthenticationBackend(
    name="cookie",
    transport=cookie_transport,
    get_strategy=get_database_strategy,
)

# ─── FastAPIUsers Instance ────────────────────────────────────────────────────

fastapi_users_app = FastAPIUsers[User, uuid.UUID](get_user_manager, [auth_backend])

# Dependency: get current user (allows must_change_password users through)
current_user = fastapi_users_app.current_user(active=True)


async def require_auth(user: User = Depends(current_user)) -> User:
    """Require auth AND enforce must_change_password."""
    if user.must_change_password:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="password_change_required",
        )
    return user


async def require_admin(user: User = Depends(require_auth)) -> User:
    """Require admin (is_superuser) role."""
    if not user.is_superuser:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN, detail="admin_required"
        )
    return user


# ─── Schemas ──────────────────────────────────────────────────────────────────


class SetupRequest(BaseModel):
    username: str
    email: str
    password: str


class ChangePasswordRequest(BaseModel):
    current_password: str
    new_password: str


class CreateUserRequest(BaseModel):
    username: str
    email: str
    is_admin: bool = False


class UpdateUserRequest(BaseModel):
    username: str | None = None
    email: str | None = None
    is_admin: bool | None = None
    is_active: bool | None = None


# ─── Auth Routes ──────────────────────────────────────────────────────────────

auth_router = APIRouter(prefix="/api/auth", tags=["auth"])

# Include fastapi-users login/logout routes (POST /login, POST /logout)
auth_router.include_router(fastapi_users_app.get_auth_router(auth_backend))


@auth_router.get("/me")
async def get_me(user: User = Depends(current_user)):
    """Current user info. Allowed even when must_change_password is set."""
    return {
        "id": str(user.id),
        "username": user.username,
        "email": user.email,
        "is_admin": user.is_superuser,
        "must_change_password": user.must_change_password,
        "auth_source": user.auth_source,
    }


@auth_router.post("/setup")
async def first_run_setup(data: SetupRequest, user: User = Depends(current_user)):
    """First-run setup: replace temp admin with real credentials."""
    if not user.must_change_password:
        raise HTTPException(400, "Setup already completed")

    if len(data.password) < 8:
        raise HTTPException(400, "Password must be at least 8 characters")

    ph = PasswordHelper()
    async with async_session_maker() as session:
        # Check username uniqueness
        for field, val in [("username", data.username), ("email", data.email)]:
            col = getattr(User, field)
            result = await session.execute(
                select(User).where(col == val, User.id != user.id)
            )
            if result.scalar_one_or_none():
                raise HTTPException(400, f"{field.title()} already in use")

        result = await session.execute(select(User).where(User.id == user.id))
        db_user = result.scalar_one()
        db_user.username = data.username
        db_user.email = data.email
        db_user.hashed_password = ph.hash(data.password)
        db_user.must_change_password = False
        await session.commit()

    logger.info("First-run setup completed for user: %s", data.username)
    return {"status": "ok"}


@auth_router.post("/change-password")
async def change_password(
    data: ChangePasswordRequest, user: User = Depends(current_user)
):
    """Change password. Requires current password."""
    if len(data.new_password) < 8:
        raise HTTPException(400, "Password must be at least 8 characters")

    ph = PasswordHelper()
    verified, _ = ph.verify_and_update(data.current_password, user.hashed_password)
    if not verified:
        raise HTTPException(400, "Current password is incorrect")

    async with async_session_maker() as session:
        result = await session.execute(select(User).where(User.id == user.id))
        db_user = result.scalar_one()
        db_user.hashed_password = ph.hash(data.new_password)
        db_user.must_change_password = False
        await session.commit()

    return {"status": "ok"}


# ─── Admin Routes ─────────────────────────────────────────────────────────────

admin_router = APIRouter(prefix="/api/admin", tags=["admin"])


@admin_router.get("/users")
async def list_users(_: User = Depends(require_admin)):
    """List all users (admin only)."""
    async with async_session_maker() as session:
        result = await session.execute(select(User).order_by(User.created_at))
        users = result.scalars().all()
    return [
        {
            "id": str(u.id),
            "username": u.username,
            "email": u.email,
            "is_admin": u.is_superuser,
            "is_active": u.is_active,
            "auth_source": u.auth_source,
            "must_change_password": u.must_change_password,
            "last_login_at": u.last_login_at.isoformat() if u.last_login_at else None,
            "created_at": u.created_at.isoformat() if u.created_at else None,
        }
        for u in users
    ]


@admin_router.post("/users")
async def create_user(data: CreateUserRequest, _: User = Depends(require_admin)):
    """Create a new user. Returns temporary password (shown once)."""
    ph = PasswordHelper()
    temp_password = secrets.token_urlsafe(16)

    async with async_session_maker() as session:
        result = await session.execute(
            select(User).where(
                (User.username == data.username) | (User.email == data.email)
            )
        )
        if result.scalar_one_or_none():
            raise HTTPException(400, "Username or email already in use")

        new_user = User(
            id=uuid.uuid4(),
            email=data.email,
            username=data.username,
            hashed_password=ph.hash(temp_password),
            is_active=True,
            is_superuser=data.is_admin,
            is_verified=True,
            must_change_password=True,
            auth_source="local",
        )
        session.add(new_user)
        await session.commit()

    logger.info("User created by admin: %s", data.username)
    return {
        "status": "ok",
        "user_id": str(new_user.id),
        "username": data.username,
        "temporary_password": temp_password,
    }


@admin_router.post("/users/{user_id}/reset-password")
async def admin_reset_password(user_id: str, _: User = Depends(require_admin)):
    """Reset password. Temp password goes to container logs only."""
    ph = PasswordHelper()
    try:
        target_id = uuid.UUID(user_id)
    except ValueError:
        raise HTTPException(400, "Invalid user ID")

    temp_password = secrets.token_urlsafe(16)

    async with async_session_maker() as session:
        result = await session.execute(select(User).where(User.id == target_id))
        target = result.scalar_one_or_none()
        if not target:
            raise HTTPException(404, "User not found")

        target.hashed_password = ph.hash(temp_password)
        target.must_change_password = True
        await session.commit()
        target_username = target.username

    logger.warning("=" * 60)
    logger.warning("  PASSWORD RESET for user '%s'", target_username)
    logger.warning("  Temporary password: %s", temp_password)
    logger.warning("=" * 60)

    return {"status": "ok", "message": "Check container logs for temporary password."}


@admin_router.put("/users/{user_id}")
async def update_user(
    user_id: str, data: UpdateUserRequest, admin: User = Depends(require_admin)
):
    """Update user details (admin only)."""
    try:
        target_id = uuid.UUID(user_id)
    except ValueError:
        raise HTTPException(400, "Invalid user ID")

    async with async_session_maker() as session:
        result = await session.execute(select(User).where(User.id == target_id))
        target = result.scalar_one_or_none()
        if not target:
            raise HTTPException(404, "User not found")

        # Prevent demoting the last admin
        if data.is_admin is False and target.is_superuser:
            admin_count = await session.execute(
                select(func.count())
                .select_from(User)
                .where(User.is_superuser == True)  # noqa: E712
            )
            if admin_count.scalar() <= 1:
                raise HTTPException(400, "Cannot demote the last admin")

        if data.username is not None:
            existing = await session.execute(
                select(User).where(
                    User.username == data.username, User.id != target_id
                )
            )
            if existing.scalar_one_or_none():
                raise HTTPException(400, "Username already in use")
            target.username = data.username

        if data.email is not None:
            existing = await session.execute(
                select(User).where(User.email == data.email, User.id != target_id)
            )
            if existing.scalar_one_or_none():
                raise HTTPException(400, "Email already in use")
            target.email = data.email

        if data.is_admin is not None:
            target.is_superuser = data.is_admin

        if data.is_active is not None:
            target.is_active = data.is_active

        await session.commit()

    return {"status": "ok"}


@admin_router.delete("/users/{user_id}")
async def delete_user(user_id: str, admin: User = Depends(require_admin)):
    """Delete a user. Cannot delete self or last admin."""
    try:
        target_id = uuid.UUID(user_id)
    except ValueError:
        raise HTTPException(400, "Invalid user ID")

    if target_id == admin.id:
        raise HTTPException(400, "Cannot delete yourself")

    async with async_session_maker() as session:
        result = await session.execute(select(User).where(User.id == target_id))
        target = result.scalar_one_or_none()
        if not target:
            raise HTTPException(404, "User not found")

        if target.is_superuser:
            admin_count = await session.execute(
                select(func.count())
                .select_from(User)
                .where(User.is_superuser == True)  # noqa: E712
            )
            if admin_count.scalar() <= 1:
                raise HTTPException(400, "Cannot delete the last admin")

        await session.delete(target)
        await session.commit()

    return {"status": "ok"}


# ─── Forward Auth Middleware ──────────────────────────────────────────────────


async def _find_or_provision_fwd_user(
    db: AsyncSession, username: str, email: str, is_admin: bool
) -> "User":
    """Find an existing user by username/email or create one for forward-auth.

    Existing accounts (including local ones) are merged: auth_source is updated
    to "forward_auth" and admin status is synced from group headers.

    Last-admin protection: if the user is currently the only admin and the proxy
    no longer grants them admin, the demotion is skipped to avoid locking out all
    administrators.
    """
    result = await db.execute(select(User).where(User.username == username))
    user = result.scalar_one_or_none()
    if user is None and email:
        result = await db.execute(select(User).where(User.email == email))
        user = result.scalar_one_or_none()

    now = datetime.now(timezone.utc)
    if user is None:
        ph = PasswordHelper()
        effective_email = email or f"{username}@forward-auth.local"
        user = User(
            id=uuid.uuid4(),
            email=effective_email,
            username=username,
            hashed_password=ph.hash(secrets.token_urlsafe(32)),
            is_active=True,
            is_superuser=is_admin,
            is_verified=True,
            must_change_password=False,
            auth_source="forward_auth",
            last_login_at=now,
        )
        db.add(user)
        await db.commit()
        await db.refresh(user)
        logger.info("ForwardAuth: provisioned user '%s'", username)
    else:
        effective_is_admin = is_admin
        # Last-admin protection: never demote the only remaining admin
        if user.is_superuser and not is_admin:
            admin_count = await db.execute(
                select(func.count())
                .select_from(User)
                .where(User.is_superuser == True)  # noqa: E712
            )
            if admin_count.scalar() <= 1:
                logger.warning(
                    "ForwardAuth: not demoting '%s' — would remove last admin", username
                )
                effective_is_admin = True

        dirty = False
        if user.is_superuser != effective_is_admin:
            user.is_superuser = effective_is_admin
            dirty = True
        if email and user.email != email:
            user.email = email
            dirty = True
        if user.auth_source != "forward_auth":
            user.auth_source = "forward_auth"
            dirty = True
        user.last_login_at = now
        await db.commit()
        if dirty:
            await db.refresh(user)
            logger.info(
                "ForwardAuth: updated user '%s' (admin=%s)", username, effective_is_admin
            )

    return user


async def _create_fwd_token(db: AsyncSession, user: "User") -> str:
    """Create and persist a new access token for a forward-auth user."""
    token_value = secrets.token_urlsafe(32)
    token = AccessToken(
        token=token_value,
        user_id=user.id,
        created_at=datetime.now(timezone.utc),
    )
    db.add(token)
    await db.commit()
    logger.debug("ForwardAuth: created session token for user '%s'", user.username)
    return token_value


def _inject_request_cookie(request: Request, name: str, value: str) -> None:
    """Replace or add a named cookie in the ASGI request scope headers."""
    existing: dict[str, str] = {}
    other_headers = []
    for k, v in request.scope["headers"]:
        if k == b"cookie":
            for part in v.decode("latin-1").split(";"):
                part = part.strip()
                if "=" in part:
                    ck, cv = part.split("=", 1)
                    existing[ck.strip()] = cv.strip()
        else:
            other_headers.append((k, v))
    existing[name] = value
    cookie_str = "; ".join(f"{k}={v}" for k, v in existing.items())
    other_headers.append((b"cookie", cookie_str.encode("latin-1")))
    request.scope["headers"] = other_headers


class ForwardAuthMiddleware(BaseHTTPMiddleware):
    """Authenticate requests via forward-auth proxy headers (Authelia, Authentik, etc.).

    When FORWARD_AUTH_ENABLED is set, reads user identity from trusted headers,
    provisions the user in the DB if needed, and injects a valid session cookie so
    downstream auth dependencies work transparently.

    SECURITY: Only enable when WatchBack is exclusively accessed through the reverse
    proxy. Direct access that bypasses the proxy must be blocked at the network level.
    """

    async def dispatch(self, request: Request, call_next):
        if not _fwd_auth_active:
            return await call_next(request)

        username = request.headers.get(_FWD_USER_HEADER, "").strip()
        if not username:
            return await call_next(request)

        # Reject obviously invalid header values before touching the DB
        if len(username) > 150:
            logger.warning("ForwardAuth: rejected oversized username header (%d chars)", len(username))
            return await call_next(request)

        email = request.headers.get(_FWD_EMAIL_HEADER, "").strip()
        if len(email) > 254:  # RFC 5321 maximum
            email = ""
        groups = {
            g.strip().lower()
            for g in request.headers.get(_FWD_GROUPS_HEADER, "").split(",")
            if g.strip()
        }
        is_admin = bool(groups & _FWD_ADMIN_GROUPS) or (username in _FWD_ADMIN_USERS)

        # Check in-memory cache (keyed by username + admin status to detect group changes)
        entry = _fwd_cache.get(username)
        if entry and entry[1] == is_admin and entry[2] > _time.monotonic():
            token = entry[0]
        else:
            # Full DB provision/sync + token creation
            try:
                async with async_session_maker() as db:
                    user = await _find_or_provision_fwd_user(db, username, email, is_admin)
                    token = await _create_fwd_token(db, user)
            except Exception:
                logger.exception("ForwardAuth: error provisioning user '%s'", username)
                return await call_next(request)
            _fwd_cache[username] = (token, is_admin, _time.monotonic() + _FWD_CACHE_TTL)

        _inject_request_cookie(request, _COOKIE_NAME, token)
        response = await call_next(request)
        response.set_cookie(
            _COOKIE_NAME,
            token,
            max_age=_COOKIE_MAX_AGE,
            httponly=True,
            samesite="lax",
            secure=_COOKIE_SECURE,
        )
        return response


# ─── Database Init ────────────────────────────────────────────────────────────


async def init_db():
    """Create tables and bootstrap admin user on first run."""
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)

    async with async_session_maker() as session:
        result = await session.execute(select(func.count()).select_from(User))
        if result.scalar() > 0:
            return

        ph = PasswordHelper()
        temp_password = secrets.token_urlsafe(16)
        admin = User(
            id=uuid.uuid4(),
            email="admin@watchback.local",
            username="admin",
            hashed_password=ph.hash(temp_password),
            is_active=True,
            is_superuser=True,
            is_verified=True,
            must_change_password=True,
            auth_source="local",
        )
        session.add(admin)
        await session.commit()

        logger.warning("=" * 60)
        logger.warning("  WATCHBACK FIRST RUN")
        logger.warning("  Default admin credentials:")
        logger.warning("  Username: admin")
        logger.warning("  Password: %s", temp_password)
        logger.warning("  You will be asked to set new credentials on first login.")
        logger.warning("=" * 60)
