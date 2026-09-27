"""
Run Sentinel.

    python -m sentinel              watch until stopped (what the algotrading-sentinel service runs);
                                    ends by itself when its code or the .env changes, for systemd to
                                    restart; exits at once if another Sentinel is already watching
    python -m sentinel --once       every agent once, then exit
    python -m sentinel --dry-run    store nothing, send nothing — log instead; the agents' state is a
                                    private copy, so the service's log offsets are left alone
    python -m sentinel --only health,trading

Reads the repository's .env for API_BASE_URL, ADMIN_USERNAME/ADMIN_PASSWORD
(read-only GETs), POSTGRES_* (its own incidents table), REDIS_* and
TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID.
"""
from __future__ import annotations

import argparse
import atexit
import logging
import os
import shutil
import signal
import sys
import tempfile
import time
from pathlib import Path
from typing import Callable, Optional

from sentinel.reload import CodeWatch

REPO_ROOT = Path(__file__).resolve().parents[3]

#: How often a watching Sentinel confirms it still holds the watch lock.
LOCK_CHECK_SECONDS = 60.0


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


def load_env() -> dict[str, str]:
    """The repository's .env, with the process environment's upper-case names over it."""
    return {**_read_env(REPO_ROOT / ".env"), **{k: v for k, v in os.environ.items() if k.isupper()}}


def dry_run_state(live: Path) -> Path:
    """
    A private copy of the service's state for a dry run. The logs agent keeps
    how far it has read each log, the security agent which activity-log
    entries it has seen: a dry run writing those into the service's own files
    would take lines and events the service then never reports. A copy lets
    the dry run start where the service is, and throws its own reading away.
    """
    copy = Path(tempfile.mkdtemp(prefix="sentinel-dry-run-"))
    for path in live.glob("*.json"):
        try:
            shutil.copy2(path, copy / path.name)
        except OSError:
            pass   # a file the service is replacing right now: that agent starts without its memory
    atexit.register(shutil.rmtree, copy, True)
    return copy


def watch_lock(env: dict[str, str]):
    """The lock that makes this the only watching Sentinel, or None without a database to hold it in."""
    from sentinel.store import WatchLock, dsn_from_env

    dsn = dsn_from_env(env)
    return WatchLock(dsn) if dsn is not None else None


def make_context(env: dict[str, str], dry_run: bool = False, state_root: Optional[Path] = None):
    """What every agent is handed: the API as the admin (GETs only), Redis, the repository."""
    import redis
    import requests

    from sentinel.context import SentinelContext

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

    return SentinelContext(repo_root=REPO_ROOT, env=env, api_get=api_get, redis_factory=redis_factory,
                           state_root=state_root, dry_run=dry_run)


def build(dry_run: bool, only: set[str] | None, env: Optional[dict[str, str]] = None):
    from sentinel.agents import all_agents
    from sentinel.engine import SentinelEngine
    from sentinel.notify import notifier_from_env
    from sentinel.pack import ContextPack
    from sentinel.store import MemoryIncidentStore, PostgresIncidentStore, dsn_from_env

    env = env if env is not None else load_env()
    live_state = REPO_ROOT / "logs" / "sentinel"
    state_root = dry_run_state(live_state) if dry_run else None
    if state_root is not None:
        logging.getLogger("sentinel").info("dry run: agents keep their state in %s, not in %s", state_root, live_state)
    ctx = make_context(env, dry_run, state_root)

    if dry_run:
        store = MemoryIncidentStore()
    else:
        dsn = dsn_from_env(env)
        if dsn is None:
            logging.getLogger("sentinel").warning("POSTGRES_PASSWORD not set — incidents are kept in memory only.")
            store = MemoryIncidentStore()
        else:
            store = PostgresIncidentStore(dsn, live_state / "unstored-incidents.jsonl")

    agents = [a for a in all_agents() if only is None or a.name in only]
    return SentinelEngine(agents, store, notifier_from_env(env, dry_run), ctx, pack=ContextPack(ctx))


def watch(engine, code: CodeWatch, stopping: dict, sleep: Optional[Callable[[float], None]] = None,
          lock=None, monotonic: Callable[[], float] = time.monotonic) -> Optional[str]:
    """
    Run until stopped, until the code or the .env changes, or until another
    Sentinel holds the watch lock (this one lost it with its connection, and
    the other took it); why, or None when stopped. All are looked at only
    between rounds, so a round is never cut in half: its state is saved and
    its heartbeat written.
    """
    changed: dict[str, Optional[str]] = {"why": None}
    next_lock = [monotonic() + LOCK_CHECK_SECONDS]

    def should_stop() -> bool:
        if stopping["flag"]:
            return True
        changed["why"] = code.changed()
        if changed["why"] is None and lock is not None and monotonic() >= next_lock[0]:
            next_lock[0] = monotonic() + LOCK_CHECK_SECONDS
            if lock.held_elsewhere():
                changed["why"] = "another Sentinel is watching now (it took the watch lock)"
        return changed["why"] is not None

    engine.run_forever(should_stop=should_stop, sleep=sleep or time.sleep)
    return changed["why"]


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
    # The baseline is taken before the agents are imported, and before the
    # .env is read: a pull or an edit that lands while this process starts is
    # then a change, not the starting point.
    code = None if args.once else CodeWatch(Path(__file__).resolve().parent, env_file=REPO_ROOT / ".env")
    env = load_env()

    # One watcher per desk: only watching takes the lock. --dry-run stores and
    # sends nothing, so it is how Sentinel is tried next to the service; --once
    # is one round someone asked for on purpose (it stores and sends like the
    # service, so next to it, add --dry-run). Exiting, rather than watching
    # without writing, is what lets the service take over: systemd restarts it
    # every 15 s, and it starts watching once the other has gone.
    lock = None if args.once or args.dry_run else watch_lock(env)
    if lock is not None and lock.held_elsewhere():
        logging.getLogger("sentinel").error(
            "another Sentinel is already watching this desk (the algotrading-sentinel service, or one started "
            "by hand); two would send every message twice. Exiting. To try Sentinel next to it: --dry-run.")
        return 1

    engine = build(args.dry_run, only, env)

    if args.once:
        engine.run_all_once()
        return 0

    stopping = {"flag": False}

    def stop(*_):
        stopping["flag"] = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    logging.getLogger("sentinel").info("Sentinel watching.")
    try:
        why = watch(engine, code, stopping, lock=lock)
    finally:
        if lock is not None:
            lock.release()
    if why is not None:
        logging.getLogger("sentinel").info("%s — exiting so the service restarts it", why)
    return 0


if __name__ == "__main__":
    sys.exit(main())
