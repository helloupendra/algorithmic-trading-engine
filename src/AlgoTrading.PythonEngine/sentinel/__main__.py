"""
Run Sentinel.

    python -m sentinel              watch forever (what the desk supervisor runs)
    python -m sentinel --once       every agent once, then exit
    python -m sentinel --dry-run    store nothing, send nothing — log instead
    python -m sentinel --only health,trading

Reads the repository's .env for API_BASE_URL, ADMIN_USERNAME/ADMIN_PASSWORD
(read-only GETs), POSTGRES_* (its own incidents table), REDIS_* and
TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID.
"""
from __future__ import annotations

import argparse
import logging
import signal
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]


def _read_env(path: Path) -> dict[str, str]:
    env: dict[str, str] = {}
    try:
        for raw in path.read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            env[key.strip()] = value.strip().strip('"').strip("'")
    except OSError:
        pass
    return env


def build(dry_run: bool, only: set[str] | None):
    import os

    import redis
    import requests

    from sentinel.agents import all_agents
    from sentinel.context import SentinelContext
    from sentinel.engine import SentinelEngine
    from sentinel.notify import notifier_from_env
    from sentinel.store import MemoryIncidentStore, PostgresIncidentStore, dsn_from_env

    env = {**_read_env(REPO_ROOT / ".env"), **{k: v for k, v in os.environ.items() if k.isupper()}}
    base = env.get("API_BASE_URL", "http://localhost:5025").rstrip("/")

    session = requests.Session()
    token: dict[str, str] = {}

    def sign_in() -> None:
        r = session.post(f"{base}/api/UserAuth/login", timeout=15,
                         json={"userNameOrEmail": env.get("ADMIN_USERNAME", ""),
                               "password": env.get("ADMIN_PASSWORD", "")})
        r.raise_for_status()
        token["value"] = r.json()["accessToken"]

    def api_get(path: str):
        """A GET as the admin, signing in again once on a 401. Never anything but GET."""
        for attempt in (1, 2):
            if "value" not in token:
                sign_in()
            r = session.get(f"{base}{path}", timeout=15,
                            headers={"Authorization": f"Bearer {token['value']}"})
            if r.status_code == 401 and attempt == 1:
                token.pop("value", None)
                continue
            r.raise_for_status()
            return r.json()
        raise RuntimeError("unreachable")

    def redis_factory():
        return redis.Redis(host=env.get("REDIS_HOST", "localhost"), port=int(env.get("REDIS_PORT", "6379")),
                           password=env.get("REDIS_PASSWORD") or None, db=int(env.get("REDIS_DB", "0")),
                           socket_timeout=5, decode_responses=True)

    ctx = SentinelContext(repo_root=REPO_ROOT, env=env, api_get=api_get, redis_factory=redis_factory)

    if dry_run:
        store = MemoryIncidentStore()
    else:
        dsn = dsn_from_env(env)
        if dsn is None:
            logging.getLogger("sentinel").warning("POSTGRES_PASSWORD not set — incidents are kept in memory only.")
            store = MemoryIncidentStore()
        else:
            store = PostgresIncidentStore(dsn, REPO_ROOT / "logs" / "sentinel" / "unstored-incidents.jsonl")

    agents = [a for a in all_agents() if only is None or a.name in only]
    return SentinelEngine(agents, store, notifier_from_env(env, dry_run), ctx)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="sentinel", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--once", action="store_true", help="run every agent once and exit")
    parser.add_argument("--dry-run", action="store_true", help="store nothing, send nothing")
    parser.add_argument("--only", default="", help="comma-separated agent names")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args(argv)

    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    only = {a.strip() for a in args.only.split(",") if a.strip()} or None
    engine = build(args.dry_run, only)

    if args.once:
        engine.run_all_once()
        return 0

    stopping = {"flag": False}

    def stop(*_):
        stopping["flag"] = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    logging.getLogger("sentinel").info("Sentinel watching.")
    engine.run_forever(should_stop=lambda: stopping["flag"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
