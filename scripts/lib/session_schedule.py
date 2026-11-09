#!/usr/bin/env python3
"""Desk timings from the exchange calendar, including weekend special sessions.

No authentication or trading endpoints are used. Unknown calendars never
schedule a weekend open; the caller retains its weekday recovery fallback.
"""
from __future__ import annotations

import argparse
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import urlopen

IST = timezone(timedelta(hours=5, minutes=30))
EXCHANGES = (("NSE", "FO"), ("BSE", "FO"), ("MCX", "COM"))


def stamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(IST)


def plan(rows, now, open_override=None, close_override=None):
    today = now.astimezone(IST).date()
    result = {"date": str(today), "state": "unknown", "openAt": "0845", "openUntil": "1500",
              "closeAt": "2358", "deployUntil": "0840", "feedOpen": "0915",
              "feedJudge": "091630", "mastersUntil": "0912", "loginUntil": "1430", "sessions": {}}
    if len(rows) != len(EXCHANGES):
        return result
    active = []
    for row, (exchange, _) in zip(rows, EXCHANGES):
        if not isinstance(row, dict):
            return result
        warning = str(row.get("calendarWarning") or "")
        if (warning and (str(today.year) in warning or str(today.year + 1) not in warning)) or row.get("isTradingDay") not in (True, False):
            return result
        if row["isTradingDay"]:
            opened, closed = stamp(row["sessionOpenUtc"]), stamp(row["sessionCloseUtc"])
            if opened.date() != today or closed <= opened or closed.date() != today:
                return result
            active.append((exchange, opened, closed, bool(row.get("specialSessionName"))))
            result["sessions"][exchange] = {"open": opened.isoformat(), "close": closed.isoformat()}
        else:
            result["sessions"][exchange] = None
    if not active:
        result["state"] = "closed"
        return result
    first = min(s[1] for s in active)
    last = max(s[2] for s in active)
    special = any(s[3] for s in active)
    # Preserve normal-day operator windows. Special/late openings use their
    # actual times, so a Sunday 14:20 session starts its feeds at 13:50.
    start = first - timedelta(minutes=30)
    if not special and first.hour < 10:
        start = first.replace(hour=8, minute=45)
    close = last + timedelta(minutes=3)
    if not special:
        close = max(close, last.replace(hour=23, minute=58))
    feed_open = min((s[1] for s in active if s[0] in ("NSE", "BSE")), default=first)
    fmt = lambda dt: dt.strftime("%H%M")
    result.update(state="trading", openAt=open_override or fmt(start), openUntil=fmt(min(last, feed_open + timedelta(hours=5, minutes=45))),
                  closeAt=close_override or fmt(close), deployUntil=fmt(start - timedelta(minutes=5)),
                  feedOpen=fmt(feed_open), feedJudge=(feed_open + timedelta(seconds=90)).strftime("%H%M%S"),
                  mastersUntil=fmt(feed_open - timedelta(minutes=3)),
                  loginUntil=fmt(min(last - timedelta(minutes=5), feed_open + timedelta(hours=5, minutes=15))))
    return result


def fetch(api, now):
    rows = []
    for exchange, segment in EXCHANGES:
        query = urlencode({"exchange": exchange, "segment": segment, "utc": now.astimezone(timezone.utc).isoformat()})
        with urlopen(api.rstrip("/") + "/api/MarketSession/check?" + query, timeout=3) as response:
            rows.append(json.load(response))
    return rows


def build_schedule(api, now, open_at=None, close_at=None):
    try:
        result = plan(fetch(api, now), now, open_at, close_at)
        result["closeDay"] = str(now.date()) if result["state"] == "trading" and now.strftime("%H%M") >= result["closeAt"] else "-"
        if now.hour < 6:
            yesterday = now - timedelta(days=1)
            if plan(fetch(api, yesterday), yesterday)["state"] == "trading":
                result["closeDay"] = str(yesterday.date())
    except (OSError, ValueError, KeyError, TypeError):
        result = plan([], now)
        result["closeDay"] = "-"
    return result


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--api")
    p.add_argument("--output")
    p.add_argument("--read")
    p.add_argument("--now")
    p.add_argument("--open-at")
    p.add_argument("--close-at")
    args = p.parse_args()
    if args.read:
        result = json.loads(Path(args.read).read_text())
        print(" ".join(str(result[k]) for k in ("state", "openAt", "openUntil", "closeAt", "deployUntil", "feedOpen", "feedJudge", "mastersUntil", "loginUntil", "closeDay")))
        return
    now = stamp(args.now) if args.now else datetime.now(IST)
    result = build_schedule(args.api, now, args.open_at, args.close_at)
    encoded = json.dumps(result)
    if args.output:
        path = Path(args.output)
        temp = path.with_suffix(".tmp")
        temp.write_text(encoded)
        temp.replace(path)
    else:
        print(encoded)


if __name__ == "__main__":
    main()
