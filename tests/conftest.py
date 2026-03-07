import os
import sys
import tempfile
import warnings

# Suppress GC warnings from diskcache SQLite teardown and unawaited coroutines
# from mocked restart tests.  Three layers are needed on Python 3.14+:
#   1. warnings.filterwarnings — covers normal warning path
#   2. sys.unraisablehook     — catches C-level __del__ warnings during GC
#   3. PYTHONWARNINGS env var — catches interpreter-shutdown warnings after exit
warnings.filterwarnings("ignore", category=ResourceWarning)
warnings.filterwarnings("ignore", category=RuntimeWarning)
os.environ.setdefault(
    "PYTHONWARNINGS",
    "ignore::ResourceWarning,ignore::RuntimeWarning",
)

_original_unraisablehook = sys.unraisablehook

def _quiet_unraisablehook(args):
    if isinstance(args.exc_value, (ResourceWarning, RuntimeWarning)):
        return
    _original_unraisablehook(args)

sys.unraisablehook = _quiet_unraisablehook

# Must be set before main.py is imported -- it reads these at module level.
_tmp = tempfile.mkdtemp(prefix="watchback_test_")
os.environ.setdefault("CONFIG_DIR", _tmp)
os.environ.setdefault("STATIC_DIR", os.path.join(os.path.dirname(__file__), "..", "static"))

import pytest
from diskcache import Cache
from fastapi.testclient import TestClient

import main
from main import app, ENV_MAP


# ---------------------------------------------------------------------------
# Shared fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(autouse=True, scope="session")
def _close_module_cache():
    """Close the module-level diskcache after all tests to avoid leaked SQLite connections."""
    yield
    main.cache.close()

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
