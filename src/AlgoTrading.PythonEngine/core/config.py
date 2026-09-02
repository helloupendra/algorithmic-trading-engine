"""
core/config.py

Configuration for the Python trading engine. Every value is read from the
repo-root `.env` (see `.env.example`), which is the single source of truth
shared with the .NET services and the Docker stack.

The `.env` path is resolved from this file's location rather than the current
working directory, so the engine behaves identically whether you launch it from
the repo root, from `src/AlgoTrading.PythonEngine`, or from an IDE run config.
"""

import os
from pathlib import Path

from dotenv import load_dotenv

# core/ -> AlgoTrading.PythonEngine/ -> src/ -> <repo root>
REPO_ROOT = Path(__file__).resolve().parents[3]
ENV_FILE = REPO_ROOT / ".env"

FYERS_LOG_PATH = os.path.join(REPO_ROOT, "logs", "fyers")
os.makedirs(FYERS_LOG_PATH, exist_ok=True)

# override=False so a real exported environment variable (CI, Docker, systemd)
# always beats the local file.
load_dotenv(dotenv_path=ENV_FILE, override=False)


def _flag(name: str, default: str = "False") -> bool:
    return os.getenv(name, default).strip().lower() in ("true", "1", "t", "yes", "y")


# --- Backend API -----------------------------------------------------------
API_BASE_URL = os.getenv("API_BASE_URL", "http://localhost:5025")
VERIFY_SSL = _flag("VERIFY_SSL", "True")

# --- FYERS broker ----------------------------------------------------------
# No default: an empty APP_ID must fail loudly rather than silently connecting
# with someone else's credentials. Set FYERS_APP_ID in .env.
APP_ID = os.getenv("FYERS_APP_ID", "").strip()

# For now keep everything on symbolUpdate
DEFAULT_DATA_TYPE = "SymbolUpdate"

# --- Diagnostics -----------------------------------------------------------
DEBUG_PRINT_MESSAGES = _flag("DEBUG_PRINT_MESSAGES", "False")

# Refresh active watchlist every N seconds
WATCHLIST_REFRESH_SECONDS = 5

# Heartbeat settings
SOURCE_NAME = "python-live-ingestor"
HEARTBEAT_SECONDS = 15


def require_app_id() -> str:
    """Return the configured FYERS app id, or explain how to set it."""
    if not APP_ID:
        raise RuntimeError(
            "FYERS_APP_ID is not set.\n"
            f"  Add it to {ENV_FILE}\n"
            "  (copy .env.example to .env first, then get the app id from "
            "https://myapi.fyers.in/dashboard)"
        )
    return APP_ID
