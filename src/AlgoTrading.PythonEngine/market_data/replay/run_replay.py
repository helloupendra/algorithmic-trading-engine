"""
Plays one recorded trading day through the desk (see desk_replay.py). Started by the API's replay
supervisor, never by hand while the desk is live:

    python market_data/replay/run_replay.py --date 2026-09-30 --speed 1 --from 09:15 --session 3 --runs 401,402
"""

from __future__ import annotations

import argparse
import os
import signal
import sys
import threading
import time
from datetime import date, datetime

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

#: A batch the API takes at once (ReplayController.MaxTicks).
POST_LIMIT = 2000


def book_poster(http, url: str, verify, *, log=print, sleep=time.sleep):
    """
    Sends ticks to the API's replay book (POST /api/Replay/ticks), in parts of at most POST_LIMIT. The poster
    answers whether the API said its book was opened again (`reopened`: it restarted and lost every price),
    so that the player sends it the latest price of every symbol. A 409, no replay on, fails the replay; a
    part the API cannot take for a moment is tried three times, then dropped.
    """
    def post(ticks: list[dict]) -> bool:
        reopened = False
        for start in range(0, len(ticks), POST_LIMIT):
            batch = ticks[start:start + POST_LIMIT]
            for attempt in range(3):
                try:
                    response = http.post(url, json=batch, verify=verify, timeout=15)
                except Exception as ex:
                    if attempt == 2:
                        log(f"[replay] the API did not take {len(batch)} ticks: {type(ex).__name__}")
                    sleep(0.5)
                    continue
                if response.status_code == 409:
                    raise RuntimeError("the API has no market replay on (it was stopped, or the API lost it)")
                if response.status_code < 500:
                    if response.status_code >= 400:
                        log(f"[replay] the API refused a batch: {response.status_code} {response.text[:200]}")
                    elif _said_reopened(response):
                        reopened = True
                    break
                sleep(0.5)
        return reopened

    return post


def _said_reopened(response) -> bool:
    try:
        body = response.json()
    except Exception:  # an answer with no JSON body: an older API
        return False
    return isinstance(body, dict) and body.get("reopened") is True


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--date", required=True, help="the recorded day, yyyy-mm-dd")
    parser.add_argument("--speed", type=float, default=1.0)
    parser.add_argument("--from", dest="start", default="09:15", help="IST start in the session, HH:MM")
    parser.add_argument("--session", type=int, required=True, help="the API's replay session id")
    parser.add_argument("--runs", default="", help="the recap runs to wait for, comma-separated")
    args = parser.parse_args(argv)

    # Before anything prints: output goes to logs/engine/market-replay-<pid>.log should the API's pipe close.
    from core.safe_output import install_safe_stdio
    install_safe_stdio(name="market-replay")

    import core.config  # noqa: F401  (the repo-root .env: database, Redis, API)
    from analysis.data import connect
    from core.api_client import build_session
    from core.config import API_BASE_URL, VERIFY_SSL
    from market_data.replay.desk_replay import (CONTROL_KEY, LISTENING_KEY, STATUS_KEY, Player, ReplayPlan,
                                                read_prime, read_window, status_json)
    from messaging.redis_publisher import build_publisher_from_env, normalize_tick

    plan = ReplayPlan(
        day=date.fromisoformat(args.date),
        speed=args.speed,
        start=datetime.strptime(args.start, "%H:%M").time(),
        session=args.session,
        runs=[int(x) for x in args.runs.split(",") if x.strip()],
    )

    stop = threading.Event()
    signal.signal(signal.SIGTERM, lambda *_: stop.set())
    signal.signal(signal.SIGINT, lambda *_: stop.set())

    conn = connect(readonly=True)
    publisher = build_publisher_from_env()
    publisher.ensure_connection()
    redis = publisher.client
    http = build_session()
    post = book_poster(http, f"{API_BASE_URL.rstrip('/')}/api/Replay/ticks", VERIFY_SSL, log=lambda line: print(line, flush=True))

    def publish(ticks: list[dict]) -> None:
        messages = []
        for tick in ticks:
            message = normalize_tick(tick, include_raw=False)
            message["sourceKey"] = tick["sourceKey"]
            message["isReplay"] = True
            message["rawPayload"] = ""
            messages.append(message)
        publisher.publish_ticks(messages)

    def report(status: dict) -> None:
        redis.set(STATUS_KEY, status_json(status), ex=2 * 24 * 3600)

    def control() -> str:
        value = redis.get(CONTROL_KEY)
        return value.decode() if isinstance(value, bytes) else (value or "")

    def listening(run_id: int) -> bool:
        return bool(redis.exists(LISTENING_KEY.format(run_id=run_id)))

    print(f"[replay] session {plan.session}: {plan.day} at {plan.speed:g}x from {plan.start:%H:%M}, "
          f"runs {plan.runs or 'none'}", flush=True)
    player = Player(
        plan,
        read=lambda since, until: read_window(conn, since, until),
        prime=lambda p: read_prime(conn, p),
        post=post,
        publish=publish,
        report=report,
        control=control,
        listening=listening,
        stop_requested=stop.is_set,
        log=lambda line: print(line, flush=True),
    )
    state = player.run()
    try:
        conn.close()
    except Exception:
        pass
    return 0 if state in ("finished", "stopped") else 1


if __name__ == "__main__":
    sys.exit(main())
