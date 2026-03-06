"""Shared test helpers."""
from unittest.mock import MagicMock


def mock_response(status_code=200, body=None, text=None):
    """Build a mock requests.Response with configurable status, JSON body, and text."""
    m = MagicMock()
    m.status_code = status_code
    m.json.return_value = body if body is not None else {}
    m.text = text if text is not None else "Mock response"
    return m
