"""WatchBack CLI commands (password reset, etc.)."""

import asyncio
import os
import secrets
import sys

from sqlalchemy import select
from sqlalchemy.ext.asyncio import async_sessionmaker, create_async_engine

CONFIG_DIR = os.environ.get("CONFIG_DIR", "/config")
DATABASE_URL = f"sqlite+aiosqlite:///{os.path.join(CONFIG_DIR, 'watchback.db')}"


async def reset_password(username: str):
    from fastapi_users.password import PasswordHelper

    from auth import Base, User  # noqa: F811

    engine = create_async_engine(DATABASE_URL)

    # Ensure tables exist
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)

    session_maker = async_sessionmaker(engine, expire_on_commit=False)
    async with session_maker() as session:
        result = await session.execute(select(User).where(User.username == username))
        user = result.scalar_one_or_none()
        if not user:
            print(f"Error: User '{username}' not found.", file=sys.stderr)
            await engine.dispose()
            sys.exit(1)

        ph = PasswordHelper()
        temp_password = secrets.token_urlsafe(16)
        user.hashed_password = ph.hash(temp_password)
        user.must_change_password = True
        await session.commit()

    print("=" * 60)
    print(f"  Password reset for user '{username}'")
    print(f"  Temporary password: {temp_password}")
    print("  User will be required to change it on next login.")
    print("=" * 60)

    await engine.dispose()


def main():
    if len(sys.argv) < 2:
        print("Usage: watchback <command> [args]")
        print("Commands:")
        print("  reset-password <username>")
        sys.exit(1)

    command = sys.argv[1]

    if command == "reset-password":
        if len(sys.argv) < 3:
            print("Usage: watchback reset-password <username>", file=sys.stderr)
            sys.exit(1)
        asyncio.run(reset_password(sys.argv[2]))
    else:
        print(f"Unknown command: {command}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
