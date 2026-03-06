import os
import tempfile

# Must be set before main.py is imported -- it creates CONFIG_DIR at module level.
_tmp = tempfile.mkdtemp(prefix="watchback_test_")
os.environ.setdefault("CONFIG_DIR", _tmp)

import pytest
from diskcache import Cache
from fastapi.testclient import TestClient

import main
from main import app, ENV_MAP


# ---------------------------------------------------------------------------
# Shared fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(autouse=True)
def clean_env(monkeypatch):
    """Strip all WatchBack env vars before each test."""
    for _, (env_name, _) in ENV_MAP.items():
        monkeypatch.delenv(env_name, raising=False)


@pytest.fixture
def fresh_cache(tmp_path, monkeypatch):
    c = Cache(str(tmp_path / "cache"))
    monkeypatch.setattr(main, "cache", c)
    yield c
    c.close()


@pytest.fixture
def client(fresh_cache):
    return TestClient(app)
