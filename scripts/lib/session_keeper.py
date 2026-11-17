#!/usr/bin/env python3
"""
The session keeper's decisions: what to do so the morning plan keeps trading.

The owner, 17 Nov 2026: until Dhan's data plan is back, NIFTY must not stop under any
condition. Two failures could stop a planned run for the rest of a day, with nothing to
bring it back:
  - the feed. That evening the FYERS socket reconnected into an empty subscription at
    20:27 and stayed silent for hours (fixed in e611f5b). A feed process that dies is not
    restarted by anything; only the pager says so.
  - the runner. One that dies is closed "Runner exited" and stays closed.

scripts/session-keeper.sh asks this once a minute. Arguments:
    --dir DIR         the API's answers, saved as files:
                        users.json     GET /api/Users
                        running.json   GET /api/Strategy/runs?status=Running
                        today.json     GET /api/Strategy/runs?fromDate=<today>
                        quotes.json    GET /api/LiveData/latest/all
                        ingestor.json  GET /api/Ingestor/status
                      A file that is missing or unreadable means "the API did not answer".
    --expected FILE   the planned runs, "account|strategy|underlying" per line (market-open's parser)
    --day-state S     trading | unknown, from the morning job's calendar check
    --now ISO         the time to judge at (tests); default now

It prints two lines:
    feed ok|start|restart|wait|unknown <why>
    runs ok|redeploy|hold|unknown <keys, comma-separated, for redeploy> <why>
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone
from typing import Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from morning_checks import IST, OPENS, _parse, spot_for  # noqa: E402
from morning_tally import Key, _norm, _rows, parse_expected  # noqa: E402

#: A spot the plan trades with no price for this long in the session: the feed is restarted.
#: Longer than the feed's own watchdog (120 s silent: a full reconnect), so that one goes first.
STALE_AFTER_SECONDS = 300

#: Who ended a run, as the API records it ("by" in RUN_STOPPED). Only these are brought back:
#: a runner that exited by itself, and one an API restart could not find. A person's Stop,
#: the risk guard, the kill switch and the market close are decisions, and stay.
RESTARTABLE_BY = frozenset({"runner"})
RESTARTABLE_REASONS = ("API restarted; runner not found",)


def _load(path: str):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def _quote_time(quote: dict) -> Optional[datetime]:
    """When the feed last delivered this quote: its arrival, else the exchange's stamp."""
    return _parse(quote.get("updatedUtc")) or _parse(quote.get("exchangeTimestampUtc"))


def feed_decision(ingestor, quotes, underlyings: list[str], now: datetime, day_state: str) -> tuple[str, str]:
    """
    ("ok" | "start" | "restart" | "wait" | "unknown", why). Judged on the NSE and BSE spots the
    plan trades; MCX is not watched (the plan has no commodity until Dhan is back).
    """
    if not isinstance(ingestor, dict):
        return "unknown", "the feed's status could not be read"
    if ingestor.get("isRunning") is not True:
        return "start", "the FYERS feed is not running"

    rows = _rows(quotes) if quotes is not None else None
    if rows is None:
        return "unknown", "the latest prices could not be read"
    today = now.astimezone(IST).date()
    stale: list[str] = []
    priced_today = False
    watched = 0
    for underlying in dict.fromkeys(u.strip().upper() for u in underlyings if u.strip()):
        label, pattern, exchange = spot_for(underlying)
        if exchange not in ("NSE", "BSE"):
            continue
        watched += 1
        opened = datetime.combine(today, OPENS[exchange], tzinfo=IST)
        stamps = [t for t in (_quote_time(q) for q in rows if pattern.match(str(q.get("symbol") or ""))) if t]
        last = max(stamps) if stamps else None
        if last is not None and last >= opened:
            priced_today = True
        if last is None or (now - last).total_seconds() > STALE_AFTER_SECONDS:
            seen = f"last {last.astimezone(IST):%H:%M:%S} IST" if last else "never priced"
            stale.append(f"{underlying} ({seen})")
    if not watched:
        return "ok", "no NSE/BSE spot in the plan"
    if not stale:
        return "ok", "every spot the plan trades is pricing"
    # A day the calendar cannot vouch for, with nothing priced since the open, is probably a
    # holiday: restarting the feed would not bring prices, only pages.
    if day_state != "trading" and not priced_today:
        return "wait", "no price since the open on a day the calendar cannot vouch for: " + ", ".join(stale)
    return "restart", f"no price for {STALE_AFTER_SECONDS // 60} min: " + ", ".join(stale)


def runs_decision(expected: list[Key], users, running, today) -> tuple[str, list[Key], str]:
    """
    ("ok" | "redeploy" | "hold" | "unknown", the planned runs to start again, why). A planned run
    is started again when it is not live and its latest run today ended by itself (a runner
    exit, an API restart that lost it) or there was none today.
    """
    if running is None:
        return "unknown", [], "the running list could not be read"
    users = _rows(users or [])
    names = {str(u.get("id")): str(u.get("userName") or "").strip().lower() for u in users if u.get("id") is not None}

    def key_of(run: dict) -> Key:
        owner = names.get(str(run.get("userId")), str(run.get("userName") or "").strip().lower())
        return _norm((owner, str(run.get("strategyName") or ""), str(run.get("underlying") or "")))

    live = {key_of(r) for r in _rows(running)
            if str(r.get("status") or "Running").lower() == "running" and r.get("isActive", True) is not False}
    latest: dict[Key, dict] = {}
    for run in sorted(_rows(today or []), key=lambda r: str(r.get("startedUtc") or ""), reverse=True):
        latest.setdefault(key_of(run), run)

    again: list[Key] = []
    reasons: list[str] = []
    held: list[str] = []
    for key in expected:
        norm = _norm(key)
        if norm in live:
            continue
        label = " ".join(key)
        run = latest.get(norm)
        if run is None:
            again.append(key)
            reasons.append(f"{label}: not started today")
            continue
        by = str(run.get("stoppedBy") or "").strip().lower()
        reason = str(run.get("stopReason") or "").strip()
        if by in RESTARTABLE_BY or reason.startswith(RESTARTABLE_REASONS):
            again.append(key)
            reasons.append(f"{label}: {reason or 'ended by itself'}")
        else:
            held.append(f"{label}: {reason or 'stopped'}{f' (by {by})' if by else ''}")
    if again:
        return "redeploy", again, "; ".join(reasons)[:300]
    if held:
        return "hold", [], "left stopped — " + "; ".join(held)[:280]
    return "ok", [], f"{len(expected)} of {len(expected)} planned live"


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--dir", required=True)
    parser.add_argument("--expected", required=True)
    parser.add_argument("--day-state", default="unknown")
    parser.add_argument("--now", default="")
    args = parser.parse_args(argv)

    now = _parse(args.now) if args.now else datetime.now(timezone.utc)
    try:
        with open(args.expected, encoding="utf-8") as handle:
            expected = parse_expected(handle.read())
    except OSError:
        expected = []
    folder = args.dir
    underlyings = [key[2] for key in expected]

    feed, feed_why = feed_decision(_load(os.path.join(folder, "ingestor.json")),
                                   _load(os.path.join(folder, "quotes.json")), underlyings, now, args.day_state)
    runs, keys, runs_why = runs_decision(expected, _load(os.path.join(folder, "users.json")),
                                         _load(os.path.join(folder, "running.json")),
                                         _load(os.path.join(folder, "today.json")))
    print(f"feed {feed} {feed_why}")
    print(f"runs {runs} {','.join('|'.join(k) for k in keys) or '-'} {runs_why}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
