"""
WatchBack Authentication Module

Provides user auth, session management, and admin functionality
using fastapi-users with SQLAlchemy + SQLite backend.
"""

import os
import uuid
import secrets
import logging
from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel
from sqlalchemy import Boolean, DateTime, String, select, func
from sqlalchemy.ext.asyncio import (
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column

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
