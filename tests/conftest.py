import os
import tempfile

# Must be set before main.py is imported — it creates CONFIG_DIR at module level.
_tmp = tempfile.mkdtemp(prefix="watchback_test_")
os.environ.setdefault("CONFIG_DIR", _tmp)
