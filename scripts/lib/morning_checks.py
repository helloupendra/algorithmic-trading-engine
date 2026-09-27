#!/usr/bin/env python3
"""
The morning job's two gates, read from the API's own answers.

    session   Is today a trading day?        GET /api/MarketSession/check
    feed      Has every spot the plan trades priced since today's open?
                                             GET /api/LiveData/latest/all

scripts/market-open.sh calls them with the answer on stdin;
scripts/tests/market-open-gates.test.sh feeds them fixtures.

session  (--answered yes|no, whether the API said 2xx; --today YYYY-MM-DD)
    Prints three lines: trading | holiday | unknown, then the holiday's name or
    why the day is unknown, then the calendar's own warning (may be empty).

    Unknown is not "trading". The calendar answers from weekends alone when a
    year's holiday list is missing, and on 2026-09-14 (Ganesh Chaturthi) that
    let the job restart everything and wait for a FYERS sign-in until 14:30 on
    a day NSE never opened. A December warning that only NEXT year's list is
    missing says nothing about today, and is not "unknown".

feed  (--underlyings A,B,C; --now ISO-8601, default the clock)
    Prints the verdict (pass | partial | fail), a summary, then one line per
    spot; exits 0, 1 or 2 to match.

    Until 28 Sep the check passed when any one of ~260 symbols had updated in
    the last five minutes, and the MCX ones update from 09:00: a dead NSE feed
    read as "live" at the 09:15 open. Now each spot the plan trades needs a
    price the exchange stamped at or after today's open: 09:15 IST on NSE and
    BSE, 09:00 on MCX.

      pass     every spot is fresh
      partial  some are missing, but the equity session is flowing
      fail     none of the plan's NSE/BSE spots is fresh (for an MCX-only
               plan: none at all) — the feed is not delivering the session
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import date, datetime, time, timedelta, timezone
from typing import Optional

IST = timezone(timedelta(hours=5, minutes=30))

# The spot each index is quoted under (UnderlyingCatalog.cs, SpotByUnderlying).
INDEX_SPOTS = {
    "NIFTY": "NSE:NIFTY50-INDEX",
    "BANKNIFTY": "NSE:NIFTYBANK-INDEX",
    "FINNIFTY": "NSE:FINNIFTY-INDEX",
    "MIDCPNIFTY": "NSE:MIDCPNIFTY-INDEX",
    "NIFTYNXT50": "NSE:NIFTYNXT50-INDEX",
    "SENSEX": "BSE:SENSEX-INDEX",
    "BANKEX": "BSE:BANKEX-INDEX",
}

# A commodity has no spot: its price is its near future, which changes every
# month, so any of its futures counts (UnderlyingCatalog.cs, CommodityUnderlyings;
# the longer list is Sentinel's, agents/trading.py).
MCX_UNDERLYINGS = frozenset({
    "CRUDEOIL", "CRUDEOILM", "NATURALGAS", "NATGASMINI", "GOLD", "GOLDM", "GOLDMINI", "GOLDPETAL",
    "GOLDGUINEA", "GOLDTEN", "SILVER", "SILVERM", "SILVERMIC", "SILVERMINI", "COPPER", "ZINC",
    "ZINCMINI", "LEAD", "LEADMINI", "ALUMINIUM", "ALUMINI", "NICKEL", "MENTHAOIL", "COTTON",
    "COTTONCNDY", "CASTORSEED", "KAPAS",
})

OPENS = {"NSE": time(9, 15), "BSE": time(9, 15), "MCX": time(9, 0)}
_MONTHS = "JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC"


# --- session -------------------------------------------------------------------
def session_verdict(body: object, answered: bool, today: date) -> tuple[str, str, str]:
    """(trading | holiday | unknown, detail, the calendar's warning)."""
    if not answered or not isinstance(body, dict):
        return "unknown", "the API did not answer whether today is a trading day", ""
    warning = str(body.get("calendarWarning") or "").strip()
    if body.get("isTradingDay") is False:
        return "holiday", str(body.get("holidayName") or "a non-trading day"), warning
    if body.get("isTradingDay") is not True:
        return "unknown", "the API's answer did not say whether today is a trading day", warning
    if warning and not _only_next_year(warning, today):
        return "unknown", warning, warning
    return "trading", "", warning


def _only_next_year(warning: str, today: date) -> bool:
    """December's notice that next year's list is missing: today itself is known."""
    return str(today.year + 1) in warning and str(today.year) not in warning


# --- feed ----------------------------------------------------------------------
def spot_for(underlying: str) -> tuple[str, Optional[re.Pattern[str]], str]:
    """(label, pattern for its symbols, exchange) of what prices an underlying."""
    u = underlying.strip().upper()
    if u in INDEX_SPOTS:
        symbol = INDEX_SPOTS[u]
        return symbol, re.compile(re.escape(symbol) + r"$", re.I), symbol.split(":", 1)[0]
    if u in MCX_UNDERLYINGS:
        return f"MCX:{u} futures", re.compile(rf"MCX:{re.escape(u)}\d{{2}}(?:{_MONTHS})FUT$", re.I), "MCX"
    symbol = f"NSE:{u}-EQ"
    return symbol, re.compile(re.escape(symbol) + r"$", re.I), "NSE"


def _parse(value: object) -> Optional[datetime]:
    if not value:
        return None
    try:
        at = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    except ValueError:
        return None
    return at if at.tzinfo else at.replace(tzinfo=timezone.utc)


def price_time(quote: dict) -> Optional[datetime]:
    """
    When the exchange made this price. A vendor that sends no stamp, or only a
    date (00:00:00 IST, some BSE contracts), leaves the arrival time as the
    only answer (LiveQuoteResponse.ExchangeTimestampUtc).
    """
    if quote.get("lastTradedPrice") is None:
        return None
    stamped = _parse(quote.get("exchangeTimestampUtc"))
    if stamped is not None and stamped.astimezone(IST).time() != time(0, 0):
        return stamped
    return _parse(quote.get("updatedUtc"))


def feed_verdict(quotes: object, underlyings: list[str], now: datetime) -> tuple[str, list[str]]:
    if isinstance(quotes, dict):
        quotes = quotes.get("items") or quotes.get("quotes") or quotes.get("data") or []
    rows = [q for q in quotes if isinstance(q, dict)] if isinstance(quotes, list) else []
    today = now.astimezone(IST).date()

    lines: list[str] = []
    fresh = total = equity = equity_fresh = 0
    for underlying in dict.fromkeys(u.strip().upper() for u in underlyings if u.strip()):
        label, pattern, exchange = spot_for(underlying)
        opened = datetime.combine(today, OPENS.get(exchange, OPENS["NSE"]), tzinfo=IST)
        stamps = [t for t in (price_time(q) for q in rows if pattern.match(str(q.get("symbol") or ""))) if t]
        last = max(stamps) if stamps else None
        ok = last is not None and last >= opened
        total += 1
        fresh += ok
        if exchange in ("NSE", "BSE"):
            equity += 1
            equity_fresh += ok
        if ok:
            lines.append(f"  fresh:   {underlying} ({label}) at {_ist(last, today)}")
        else:
            seen = f"last price {_ist(last, today)}" if last else "no price at all"
            lines.append(f"  missing: {underlying} ({label}) — nothing since {opened:%H:%M} IST; {seen}")

    if total and fresh == total:
        verdict = "pass"
    elif (equity and equity_fresh == 0) or (not equity and fresh == 0):
        verdict = "fail"
    else:
        verdict = "partial"
    summary = f"{fresh} of {total} spot price(s) the plan trades are fresh since today's open"
    return verdict, [summary] + lines


def _ist(moment: datetime, today: date) -> str:
    local = moment.astimezone(IST)
    return local.strftime("%H:%M:%S IST") if local.date() == today else local.strftime("%d %b %H:%M:%S IST")


# --- command line ----------------------------------------------------------------
def _read_json() -> object:
    raw = sys.stdin.read()
    try:
        return json.loads(raw) if raw.strip() else None
    except ValueError:
        return None


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="gate", required=True)
    s = sub.add_parser("session")
    s.add_argument("--answered", choices=("yes", "no"), default="yes")
    s.add_argument("--today", default=None, help="YYYY-MM-DD, default today in IST")
    f = sub.add_parser("feed")
    f.add_argument("--underlyings", required=True)
    f.add_argument("--now", default=None, help="ISO-8601, default the clock")
    args = parser.parse_args(argv)

    if args.gate == "session":
        today = date.fromisoformat(args.today) if args.today else datetime.now(IST).date()
        verdict, detail, warning = session_verdict(_read_json(), args.answered == "yes", today)
        print(verdict)
        print(detail)
        print(warning)
        return 0

    now = _parse(args.now) if args.now else datetime.now(timezone.utc)
    if now is None:
        parser.error("--now must be ISO-8601")
    verdict, lines = feed_verdict(_read_json(), args.underlyings.replace(" ", ",").split(","), now)
    print(verdict)
    print("\n".join(lines))
    return {"pass": 0, "partial": 1}.get(verdict, 2)


if __name__ == "__main__":
    sys.exit(main())
