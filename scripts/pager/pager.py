"""
OpenFNO pager: pages the owner when the live desk is in serious trouble.

Sentinel watches the desk all day and writes to Telegram about everything; this pages only for the few things
that cannot wait, in the Desk System channel, with the reason. It has a slot for a phone-call service and none
is set up: CallMeBot filled it until 1 Oct 2026 and was removed, because its calls were refused as spam and it
asked the owner for money. It runs on the
server beside the desk as its own systemd unit, openfno-pager, from ~/openfno-pager: outside the deploy path,
because a deploy is gated while runs are live and the pager has to work on the day it is needed
(docs/modules/pager.md). From the desk's checkout it reads only .env (the admin sign-in, Telegram, Redis) and
sentinel/clock.py. Everything it does to the desk is a GET, and one read-only XREVRANGE.

Once a minute it reads the desk and judges every rule: found, healthy, "could not tell", or outside its window.

  api-down          08:40 to the day's last close: GET /health does not answer, or the API has gone down 3 times
                    in 30 minutes (a crash loop the desk keeps restarting, each outage too short to page alone)
  api-degraded      the same hours: /health answers (it is only the console's page, Program.cs's SPA fallback,
                    and never touches the database) but every signed-in read fails, or one has failed for 5 min
  no-broker         09:00-15:30, and the MCX evening: neither Dhan nor FYERS is signed in
  no-ticks          NSE open, 09:17-15:29: the newest NSE/BSE tick in Redis, which is what the strategies read,
                    is over 3 minutes old; the API's index rows stand in only when the stream cannot be read
  redis-down        the same hours: Redis refuses the connection, so every strategy is blind
  runs-not-up       09:30-15:00: the morning plan has runs and none is live, or from 09:40 fewer than half of
                    them have ever been live today (the morning job deploys only after the 09:15 open)
  runs-dropped      09:20-15:10: at least 4 of today's runs died (runner exited, failed to start, lost in an API
                    restart) and they are half the day's peak; a stop anyone meant never counts
  disk-low          08:40 to the last close: under 3 GB free on /
  mcx-no-ticks      15:30 to 5 min before the MCX close, only when the plan has MCX runs: no MCX tick for 5 min
  mcx-runs-dropped  the same evening: no MCX run is live and at least one of them died

Only on days the desk trades: a trading day, and never at a weekend (Muhurat, a Budget Sunday) unless a planned
run is live. The calendar is the API's, kept through an API outage as it said earlier the same day
(sentinel/clock.remember_day), then the repo's seeded holidays, then the weekday rule.

A reading it cannot make is never a page by itself, and one slow minute never rings: a problem pages only
after it has been seen on as many passes as its grace period holds, and it is over only after 3 clean passes
in a row. A pass where the API is down is "could not tell" for every rule that reads through the API. A page is
a Telegram text, again every 30 minutes while it lasts, and one "resolved" text when it is over. With a call
service in the slot a page also calls, again every 10 minutes, at most 3 calls per episode and 9 per problem
per day. A rule whose window ends
while it is open says so, and never that it was resolved.

Config: pager.env beside this file (never the repo's .env, which it only reads):
  PAGER_HEARTBEAT_URL    optional dead-man's switch (healthchecks.io style), pinged each pass on weekdays
                         from 08:00 IST, so a dead box, network or pager is noticed from outside

  python pager.py            run forever
  python pager.py --once     one pass: print what it sees, page nothing, write nothing
  python pager.py --test     send one test text
"""
from __future__ import annotations

import json
import logging
import os
import re
import shutil
import sys
import time
from dataclasses import asdict, dataclass, field, fields
from datetime import date, datetime, time as dtime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Optional

import requests

HERE = Path(__file__).resolve().parent


def _find_repo() -> Path:
    """
    The desk's checkout: OPENFNO_REPO when set; the checkout this file sits in when it runs from
    scripts/pager/ (the tests); otherwise ~/algorithmic-trading-engine, the server's, since the pager itself
    runs from ~/openfno-pager.
    """
    configured = os.environ.get("OPENFNO_REPO")
    if configured:
        return Path(configured)
    checkout = HERE.parent.parent
    if (checkout / "src" / "AlgoTrading.PythonEngine" / "sentinel" / "clock.py").is_file():
        return checkout
    return Path.home() / "algorithmic-trading-engine"


REPO = _find_repo()
_ENGINE = str(REPO / "src" / "AlgoTrading.PythonEngine")
if _ENGINE not in sys.path:
    sys.path.insert(0, _ENGINE)
from sentinel.clock import IST, ask_calendar, remember_day, session_from_answers  # noqa: E402

log = logging.getLogger("pager")

CHECK_EVERY = 60
GRACE = {
    "api-down": 180, "api-degraded": 180, "no-broker": 120, "no-ticks": 180, "redis-down": 180,
    "runs-not-up": 120, "runs-dropped": 120, "disk-low": 120, "mcx-no-ticks": 180, "mcx-runs-dropped": 120,
}
RULES = tuple(GRACE)
RESOLVE_AFTER = 3                       # clean passes in a row before a problem is over
STALE_AFTER = timedelta(minutes=5)      # an unpaged problem not seen for this long starts its grace again
RECALL_EVERY = timedelta(minutes=10)
TEXT_EVERY = timedelta(minutes=30)
MAX_CALLS = 3                           # call attempts per episode
DAILY_CALL_CEILING = 9                  # call attempts per problem per day, across episodes: the flood guard
FLAP_WINDOW = timedelta(minutes=30)
FLAP_OUTAGES = 3
DEGRADED_STREAK = 5                     # passes one read may fail in a row before it is the API's problem
BLIND_AFTER = timedelta(minutes=15)
BLIND_PASSES = 15
TICK_STALE = 180
MCX_TICK_STALE = 300                    # crude trades thinner in the evening than the indices by day
RUNS_DROPPED_MIN = 4
DISK_MIN_FREE = 3 * 1024 ** 3
STREAM_COUNT = 500
HEARTBEAT_FROM = dtime(8, 0)

INDEX_SYMBOLS = ("NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", "BSE:SENSEX-INDEX")
# A commodity is priced by its futures (scripts/lib/morning_checks.py and Sentinel's agents/trading.py).
MCX_UNDERLYINGS = frozenset({
    "CRUDEOIL", "CRUDEOILM", "NATURALGAS", "NATGASMINI", "GOLD", "GOLDM", "GOLDMINI", "GOLDPETAL",
    "GOLDGUINEA", "GOLDTEN", "SILVER", "SILVERM", "SILVERMIC", "SILVERMINI", "COPPER", "ZINC",
    "ZINCMINI", "LEAD", "LEADMINI", "ALUMINIUM", "ALUMINI", "NICKEL", "MENTHAOIL", "COTTON",
    "COTTONCNDY", "CASTORSEED", "KAPAS",
})
_MCX_FUTURE = re.compile(r"^MCX:([A-Z]+)\d{2}(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)FUT$")
# Which exchanges each session covers in the tick stream (sentinel/agents/health.py GROUPS).
_GROUP_OF = {"NSE": "NSE", "BSE": "NSE", "NFO": "NSE", "BFO": "NSE", "MCX": "MCX"}
GROUP_LABEL = {"NSE": "NSE/BSE", "MCX": "MCX"}

PROVIDERS_PATH = "/api/Providers"
QUOTES_PATH = "/api/LiveData/latest/all"
PLAN_PATH = "/api/Desk/plan"
RUNS_PATH = "/api/Strategy/runs?status=any&fromDate={day}&take=500"
# The reads a rule depends on. One stuck failing is the API's problem even while the others answer; the
# calendar is not among them, since what it said earlier today stands in for it.
WATCHED_READS = ("providers", "quotes", "plan", "runs")

# How a run ended, as the API records it (StrategyRunControl, StrategyProcessRegistry, StrategyRiskGuardService),
# read the way Sentinel's agents/trading.ended_on_purpose reads it; a test keeps the two in step.
_DELIBERATE_BY = frozenset({"market-hours", "risk-guard"})
_MACHINE_BY = frozenset({"runner", "api"})
_ABNORMAL_REASONS = ("runner exited", "runner failed", "api restarted")
_NORMAL_REASONS = ("market closed", "mcx closed", "target hit", "stop loss hit", "trailing stop hit", "stopped by ")

WORDS = {
    "api-down": "OpenFNO alert. The desk API is not answering.",
    "api-degraded": "OpenFNO alert. The desk API is up but its reads are failing, most likely the database.",
    "no-broker": "OpenFNO alert. Dhan and FYERS are both signed out. No live feed.",
    "no-ticks": "OpenFNO alert. No live index prices. The feed is down.",
    "redis-down": "OpenFNO alert. Redis is unreachable. Every strategy is blind to prices.",
    "runs-not-up": "OpenFNO alert. The morning plan's runs are not up.",
    "runs-dropped": "OpenFNO alert. Many of today's strategy runs have died.",
    "disk-low": "OpenFNO alert. The server disk is almost full. The database will stop writing.",
    "mcx-no-ticks": "OpenFNO alert. No MCX prices this evening. The commodity feed is down.",
    "mcx-runs-dropped": "OpenFNO alert. The evening MCX runs have died.",
}

FOUND, HEALTHY, UNKNOWN, CLOSED = "found", "healthy", "unknown", "closed"


def load_env(path: Path) -> dict[str, str]:
    env: dict[str, str] = {}
    try:
        lines = path.read_text().splitlines()
    except OSError:
        return env
    for line in lines:
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            env[key.strip()] = value.strip().strip('"').strip("'")
    return env


def parse_utc(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value:
        return None
    try:
        moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def price_time(quote: dict) -> Optional[datetime]:
    """
    When the exchange made a price: its own stamp, unless a vendor sent only a date (00:00 IST), then the
    arrival time (scripts/lib/morning_checks.price_time). A reconnect's snapshot on a closed day arrives now
    but carries the last session's stamp, so it never reads as "priced since the open".
    """
    stamped = parse_utc(quote.get("exchangeTimestampUtc"))
    if stamped is not None and stamped.astimezone(IST).time() != dtime(0, 0):
        return stamped
    return parse_utc(quote.get("updatedUtc") or quote.get("receivedUtc"))


def only_next_year(warning: str, today: date) -> bool:
    """December's notice that next year's holiday list is missing says nothing about today (morning_checks)."""
    return str(today.year + 1) in warning and str(today.year) not in warning


def _hm(moment: datetime) -> str:
    return moment.astimezone(IST).strftime("%H:%M")


def _age(seconds: float) -> str:
    return f"{int(seconds // 60)} min {int(seconds % 60)} s"


# ------------------------------------------------------------------ what it sees

@dataclass
class StreamHead:
    """The newest part of market:ticks, per exchange group (sentinel/agents/health.py _read_stream)."""

    newest: dict[str, datetime] = field(default_factory=dict)   # group -> newest arrival
    priced: dict[str, datetime] = field(default_factory=dict)   # group -> newest exchange-stamped price
    scanned: int = 0
    oldest: Optional[datetime] = None   # entry-id time of the oldest entry read
    exhausted: bool = False             # the whole stream was read


@dataclass
class Seen:
    """One minute's reading of the desk. None means "could not tell", which never pages by itself."""

    now: datetime
    api_up: bool
    trading_day: bool = False
    nse_open: bool = False
    mcx_open: bool = False
    calendar: str = "weekday rule"
    last_close: Optional[datetime] = None      # the MCX close, when MCX trades today
    calendar_warning: Optional[str] = None
    day_unknown: bool = False                  # the calendar warned it cannot vouch for today
    kept: Optional[dict] = None                # the calendar's facts for today, to keep through an outage
    dhan: Optional[bool] = None
    fyers: Optional[bool] = None
    dhan_expires: Optional[datetime] = None
    quotes_known: bool = False
    newest_index_tick: Optional[datetime] = None
    newest_mcx_tick: Optional[datetime] = None
    index_priced_since_open: bool = False
    planned: Optional[int] = None
    plan_live: Optional[int] = None            # planned runs whose runner is live; the manual book never counts
    plan_missing: bool = False
    mcx_planned: Optional[int] = None
    mcx_expected: Optional[bool] = None        # MCX runs are planned (or running) today
    live: Optional[int] = None                 # plan slots live now, from today's runs
    failed: Optional[int] = None               # plan slots whose newest run today died, with nothing live
    mcx_live: Optional[int] = None
    mcx_failed: Optional[int] = None
    stream: Optional[StreamHead] = None
    redis_error: Optional[str] = None          # Redis refused the connection: itself the fault
    reads_ok: int = 0
    read_ok: list[str] = field(default_factory=list)
    read_errors: dict[str, str] = field(default_factory=dict)   # read -> "GET path -> why"
    login_refused: bool = False
    disk_free: Optional[int] = None


class LoginRefused(Exception):
    """The admin sign-in was refused (400/401/403): the pager's credentials, not the desk."""


class RedisDown(Exception):
    """Redis refused or timed out the connection."""


class Api:
    def __init__(self, base: str, user: str, password: str) -> None:
        self.base, self.user, self.password = base.rstrip("/"), user, password
        self.s = requests.Session()
        self.token: Optional[str] = None
        self.login_error: Optional[Exception] = None

    def health(self) -> bool:
        """Kestrel is serving. Only that: /health is the SPA fallback's index.html, not a health check."""
        try:
            return self.s.get(f"{self.base}/health", timeout=10).status_code == 200
        except requests.RequestException:
            return False

    def begin_pass(self) -> None:
        """One sign-in attempt per pass: a refused or failing sign-in fails the rest of the pass at once."""
        self.login_error = None

    def _login(self) -> str:
        r = self.s.post(f"{self.base}/api/UserAuth/login", timeout=15,
                        json={"userNameOrEmail": self.user, "password": self.password})
        if r.status_code in (400, 401, 403):
            raise LoginRefused(f"admin sign-in refused (HTTP {r.status_code})")
        r.raise_for_status()
        return r.json()["accessToken"]

    def get(self, path: str) -> Any:
        for attempt in (1, 2):
            if self.token is None:
                if self.login_error is not None:
                    raise self.login_error
                try:
                    self.token = self._login()
                except Exception as exc:
                    self.login_error = exc
                    raise
            r = self.s.get(f"{self.base}{path}", timeout=15, headers={"Authorization": f"Bearer {self.token}"})
            if r.status_code == 401 and attempt == 1:
                self.token = None
                continue
            r.raise_for_status()
            return r.json()
        raise RuntimeError("unreachable")


_FAILED = object()
_MISSING = object()


def _why(exc: BaseException) -> str:
    if isinstance(exc, requests.HTTPError) and exc.response is not None:
        return f"HTTP {exc.response.status_code}"
    if isinstance(exc, LoginRefused):
        return str(exc)
    return type(exc).__name__


def _read(api: Api, seen: Seen, name: str, path: str, expect: type, missing_ok: bool = False) -> Any:
    """GET as the admin, noting whether it answered; never raises. A 404 is an answer where missing_ok."""
    shown = path.split("?", 1)[0]
    try:
        body = api.get(path)
    except Exception as exc:
        if missing_ok and isinstance(exc, requests.HTTPError) and exc.response is not None \
                and exc.response.status_code == 404:
            seen.reads_ok += 1
            seen.read_ok.append(name)
            return _MISSING
        seen.login_refused = seen.login_refused or isinstance(exc, LoginRefused)
        seen.read_errors[name] = f"GET {shown} -> {_why(exc)}"
        return _FAILED
    if not isinstance(body, expect):
        seen.read_errors[name] = f"GET {shown} -> unexpected body ({type(body).__name__})"
        return _FAILED
    seen.reads_ok += 1
    seen.read_ok.append(name)
    return body


def seed_holiday(day: date) -> Optional[str]:
    """
    Today's NSE full-day holiday from the repo's seeded calendar: the last resort when the API has been down
    since before anything was kept today, so a crash overnight before Gandhi Jayanti does not call.
    """
    try:
        data = json.loads((REPO / "src" / "AlgoTrading.Api" / "SeedData" / "market_calendar.json").read_text())
    except (OSError, ValueError):
        return None
    for row in data.get("holidays", []) if isinstance(data, dict) else []:
        if isinstance(row, dict) and row.get("exchange") == "NSE" and row.get("date") == day.isoformat() \
                and row.get("closure", "FullDay") == "FullDay":
            return str(row.get("name") or "a holiday")
    return None


def run_failed(row: dict) -> bool:
    """
    Whether a run that has ended died rather than being stopped: the runner exited on its own ("Runner exited
    (code 1)", StoppedBy "runner"), failed to start (Status "Failed"), was lost in an API restart ("API
    restarted; runner not found", StoppedBy "api"), or nothing was recorded (the track record's "Not
    recorded"). The risk guard, the market-hours stop and a person's stop are meant, and never count.
    """
    status = str(row.get("status") or "").strip()
    if status in ("Running", "Stopping"):
        return False   # not ended; a Running row without its runner is between an API restart and adoption
    if status == "Failed":
        return True
    reason = str(row.get("stopReason") or "").strip().lower()
    by = str(row.get("stoppedBy") or "").strip().lower()
    if not reason and not by:
        return True
    if reason.startswith(_ABNORMAL_REASONS):
        return True
    if by in _DELIBERATE_BY:
        return False
    if by and by not in _MACHINE_BY:
        return False   # a person's name
    return not reason.startswith(_NORMAL_REASONS)


def tally_runs(rows: list, slots: Optional[set]) -> tuple[set, set]:
    """
    (live, died) plan slots — (userId, strategy, underlying) — from today's runs, newest first. The manual book
    and alerter runs are not strategies; with the plan known, runs outside it do not count either, so another
    trader's ad-hoc runs cannot move the day's peak. A slot whose newest run died and that nothing has
    replaced is one death, however many times it was restarted and died again.
    """
    live: set = set()
    newest: dict[tuple, dict] = {}
    for row in rows:
        if not isinstance(row, dict) or row.get("role"):
            continue
        strategy = str(row.get("strategyName") or "").strip().lower()
        if strategy == "manual":
            continue
        key = (row.get("userId"), strategy, str(row.get("underlying") or "").strip().upper())
        if slots and key not in slots:
            continue
        if row.get("isActive") is True:
            live.add(key)
        newest.setdefault(key, row)
    died = {k for k, row in newest.items() if k not in live and run_failed(row)}
    return live, died


def read_stream(client: Any, stream: str, count: int = STREAM_COUNT) -> StreamHead:
    """The head of the tick stream, one read-only XREVRANGE; replays skipped, as Sentinel reads it."""
    head = StreamHead()
    entries = client.xrevrange(stream, max="+", min="-", count=count)
    head.exhausted = len(entries) < count
    for entry_id, fields_ in entries:
        head.scanned += 1
        try:
            id_time: Optional[datetime] = datetime.fromtimestamp(int(str(entry_id).split("-", 1)[0]) / 1000.0,
                                                                 tz=timezone.utc)
        except ValueError:
            id_time = None
        if id_time is not None:
            head.oldest = id_time
        fields_ = fields_ or {}
        try:
            payload = json.loads(fields_.get("payload") or "{}")
        except (TypeError, ValueError):
            payload = {}
        payload = payload if isinstance(payload, dict) else {}
        if payload.get("isReplay") is True:
            continue
        exchange = str(fields_.get("exchange") or payload.get("exchange") or "").upper()
        symbol = str(fields_.get("symbol") or payload.get("symbol") or "")
        if not exchange and ":" in symbol:
            exchange = symbol.split(":", 1)[0].upper()
        group = _GROUP_OF.get(exchange)
        if group is None:
            continue
        at = parse_utc(payload.get("receivedUtc")) or id_time
        if at is not None and (group not in head.newest or at > head.newest[group]):
            head.newest[group] = at
        priced = price_time(payload) or id_time
        if priced is not None and (group not in head.priced or priced > head.priced[group]):
            head.priced[group] = priced
    return head


def read_desk(api: Api, now: datetime, state: "State",
              stream: Optional[Callable[[], StreamHead]] = None,
              disk: Optional[Callable[[], int]] = None) -> Seen:
    seen = Seen(now=now, api_up=api.health())
    ist = now.astimezone(IST)
    today = ist.date()
    if disk is not None:
        try:
            seen.disk_free = int(disk())
        except Exception as exc:
            log.warning("disk unreadable: %s", type(exc).__name__)

    answers: dict[str, dict] = {}
    if seen.api_up:
        api.begin_pass()

        def calendar_get(path: str) -> Any:
            name = "calendar-" + ("MCX" if "exchange=MCX" in path else "NSE")
            body = _read(api, seen, name, path, dict)
            if body is _FAILED:
                raise RuntimeError("unreadable")
            return body

        answers = ask_calendar(calendar_get)
    # What the calendar said earlier today stands in while the API is down or cannot read it: a holiday does
    # not stop being one because the API crashed (sentinel/clock.remember_day).
    seen.kept = remember_day(now, answers, state.kept)
    session = session_from_answers(now, answers, seen.kept)
    seen.trading_day, seen.nse_open, seen.mcx_open = session.trading_day, session.nse_open, session.mcx_open
    seen.calendar = ("calendar" if answers else
                     "kept from earlier today" if session.from_calendar else "weekday rule")
    if not session.from_calendar:
        holiday = seed_holiday(today)
        if holiday:
            seen.trading_day = seen.nse_open = seen.mcx_open = False
            seen.calendar = f"seeded holiday ({holiday})"
    markets = (seen.kept or {}).get("markets") or {}
    mcx_trades = (markets.get("MCX") or {}).get("tradingDay")
    # A closed day still reports its usual hours (MarketSessionService), so the close counts only when MCX trades.
    seen.last_close = session.mcx_close if mcx_trades is True else None
    if "NSE" in answers:
        seen.calendar_warning = str(answers["NSE"].get("calendarWarning") or "").strip()
    elif state.warning_day == today.isoformat():
        seen.calendar_warning = state.warning
    warning = seen.calendar_warning or ""
    seen.day_unknown = bool(warning) and not only_next_year(warning, today)
    if not seen.api_up:
        return seen   # everything else is read through the API: "could not tell"

    providers = _read(api, seen, "providers", PROVIDERS_PATH, list)
    if providers is not _FAILED:
        for p in providers:
            if not isinstance(p, dict) or p.get("key") not in ("dhan", "fyers"):
                continue
            sess = p.get("session") or {}
            # isConnected already applies each broker's real expiry (Dhan 24 h from issue, FYERS 06:00 IST).
            # needsReconnect only means "saved on an earlier IST date", which is wrong for Dhan's 24-hour token.
            signed = bool(sess.get("isConnected"))
            expires = parse_utc(sess.get("expiresUtc"))
            if expires is not None and expires <= now:
                signed = False
            if p["key"] == "dhan":
                seen.dhan, seen.dhan_expires = signed, expires
            else:
                seen.fyers = signed

    slots: set = set()
    mcx_names: set = set()
    plan = _read(api, seen, "plan", PLAN_PATH, dict, missing_ok=True)
    if plan is _MISSING:
        seen.plan_missing = True
    elif plan is not _FAILED:
        try:
            seen.planned = int(plan.get("planned") or 0)
            seen.plan_live = int(plan.get("live") or 0)
            for run in plan.get("runs") or []:
                if not isinstance(run, dict):
                    continue
                underlying = str(run.get("underlying") or "").strip().upper()
                if run.get("userId") is not None:
                    slots.add((run.get("userId"), str(run.get("strategy") or "").strip().lower(), underlying))
                if underlying in MCX_UNDERLYINGS:
                    mcx_names.add(underlying)
                    seen.mcx_planned = (seen.mcx_planned or 0) + 1
            seen.mcx_planned = seen.mcx_planned or 0
        except (TypeError, ValueError):
            slots, mcx_names = set(), set()
            seen.planned = seen.plan_live = seen.mcx_planned = None
            seen.reads_ok -= 1
            seen.read_ok.remove("plan")
            seen.read_errors["plan"] = f"GET {PLAN_PATH} -> unexpected body"

    quotes = _read(api, seen, "quotes", QUOTES_PATH, list)
    if quotes is not _FAILED:
        seen.quotes_known = True
        opened = datetime.combine(today, dtime(9, 15), IST)
        index_rows = [q for q in quotes if isinstance(q, dict) and q.get("symbol") in INDEX_SYMBOLS]
        stamps = [s for s in (parse_utc(q.get("updatedUtc")) for q in index_rows) if s is not None]
        seen.newest_index_tick = max(stamps) if stamps else None
        seen.index_priced_since_open = any(at is not None and at >= opened for at in map(price_time, index_rows))
        mcx_stamps = []
        for q in quotes:
            match = _MCX_FUTURE.match(str(q.get("symbol") or "")) if isinstance(q, dict) else None
            if match and (not mcx_names or match.group(1) in mcx_names):
                mcx_stamps.append(parse_utc(q.get("updatedUtc")))
        mcx_stamps = [s for s in mcx_stamps if s is not None]
        seen.newest_mcx_tick = max(mcx_stamps) if mcx_stamps else None

    runs = _read(api, seen, "runs", RUNS_PATH.format(day=today.isoformat()), list)
    if runs is not _FAILED:
        live, died = tally_runs(runs, slots or None)
        seen.live, seen.failed = len(live), len(died)
        seen.mcx_live = sum(1 for k in live if k[2] in MCX_UNDERLYINGS)
        seen.mcx_failed = sum(1 for k in died if k[2] in MCX_UNDERLYINGS)
        started_mcx = any(isinstance(r, dict) and str(r.get("underlying") or "").upper() in MCX_UNDERLYINGS
                          and str(r.get("strategyName") or "").lower() != "manual" and not r.get("role")
                          for r in runs)
    else:
        started_mcx = False
    if seen.mcx_planned is not None:
        seen.mcx_expected = seen.mcx_planned > 0 or bool(seen.mcx_live)
    elif started_mcx:
        seen.mcx_expected = True

    if stream is not None:
        try:
            seen.stream = stream()
        except RedisDown as exc:
            seen.redis_error = str(exc)
        except Exception as exc:   # the stream cannot vouch either way; the API's table stands in
            log.warning("stream unreadable: %s", type(exc).__name__)
    if seen.stream is not None:
        priced = seen.stream.priced.get("NSE")
        if priced is not None and priced >= datetime.combine(today, dtime(9, 15), IST):
            seen.index_priced_since_open = True
    return seen


# ------------------------------------------------------------------ what it remembers

@dataclass
class Problem:
    since: str
    last_seen: Optional[str] = None
    hits: int = 0                        # passes it was found on this episode
    clear_passes: int = 0                # clean passes in a row
    paged: bool = False
    attempts: int = 0                    # call attempts this episode
    placed: int = 0                      # of those, the ones the call service placed
    last_call: Optional[str] = None
    last_text: Optional[str] = None
    last_fail_text: Optional[str] = None

    @classmethod
    def from_dict(cls, raw: dict) -> "Problem":
        if "calls" in raw and "attempts" not in raw:   # a v1.1 state.json
            raw = {**raw, "attempts": raw["calls"], "placed": raw["calls"]}
        names = {f.name for f in fields(cls)}
        return _checked(cls(**{k: v for k, v in raw.items() if k in names}))


_TYPES = {"int": int, "bool": bool, "str": str, "list": list, "dict": dict}


def _checked(instance: Any) -> Any:
    """A loaded dataclass whose every field has its annotated type: one that does not (a hand-edited
    state.json) would fail every pass rather than just the load."""
    for f in fields(instance):
        spec = str(f.type)
        optional = spec.startswith("Optional[")
        wanted = _TYPES.get((spec[len("Optional["):-1] if optional else spec).split("[", 1)[0])
        value = getattr(instance, f.name)
        if wanted is not None and not (optional and value is None) and not isinstance(value, wanted):
            raise TypeError(f"{f.name} is {type(value).__name__}")
    return instance


@dataclass
class State:
    day: str = ""
    kept: Optional[dict] = None          # sentinel/clock.remember_day's facts for today
    warning_day: str = ""
    warning: str = ""
    priced_day: str = ""                 # the day an index last priced after the 09:15 open
    peak_runs: int = 0                   # the day's peak of live + died plan slots
    peak_plan_live: int = 0
    min_planned: Optional[int] = None
    mcx_expected: Optional[bool] = None
    api_was_down: bool = False
    api_outages: list[str] = field(default_factory=list)
    read_streaks: dict[str, int] = field(default_factory=dict)
    blind_since: Optional[str] = None
    calls_today: dict[str, int] = field(default_factory=dict)
    notices: dict[str, str] = field(default_factory=dict)
    open: dict[str, Problem] = field(default_factory=dict)

    @classmethod
    def load(cls, path: Path) -> "State":
        try:
            raw = json.loads(path.read_text())
        except FileNotFoundError:
            return cls()
        except (OSError, ValueError) as exc:
            log.warning("state unreadable, starting fresh: %s", type(exc).__name__)
            return cls()
        try:
            names = {f.name for f in fields(cls)} - {"open"}
            state = _checked(cls(**{k: v for k, v in raw.items() if k in names}))
            state.open = {k: Problem.from_dict(v) for k, v in (raw.get("open") or {}).items()
                          if isinstance(v, dict) and isinstance(v.get("since"), str)}
            return state
        except (AttributeError, TypeError, ValueError) as exc:
            log.warning("state unusable, starting fresh: %s", type(exc).__name__)
            return cls()

    def save(self, path: Path) -> None:
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(asdict(self), indent=1))
        tmp.replace(path)

    def outages(self, now: datetime) -> int:
        return sum(1 for s in self.api_outages if now - datetime.fromisoformat(s) <= FLAP_WINDOW)


def roll_day(state: State, now: datetime) -> None:
    today = now.astimezone(IST).date().isoformat()
    if state.day == today:
        return
    state.day = today
    state.calls_today, state.notices, state.read_streaks = {}, {}, {}
    state.peak_runs = state.peak_plan_live = 0
    state.min_planned = state.mcx_expected = None
    # A problem from yesterday that never paged starts again from nothing. A paged one stays until its rule
    # speaks again, so its end is still announced and its episode's call cap still holds.
    state.open = {k: p for k, p in state.open.items() if p.paged and k in GRACE}


def observe(state: State, seen: Seen) -> None:
    """Fold one reading into what the pager remembers, before any rule is judged: the day's peaks, the
    API's outages, each read's failure streak, and the calendar's facts to keep through an outage."""
    now = seen.now
    roll_day(state, now)
    today = state.day
    if seen.kept is not None:
        state.kept = seen.kept
    if seen.api_up and seen.calendar_warning is not None and "calendar-NSE" in seen.read_ok:
        state.warning_day, state.warning = today, seen.calendar_warning
    # Outages count toward a crash loop only from 08:40, when the deploy gate shuts (desk-common.sh): before
    # it, the owner's own deploys restart the API and would add up to a "loop" by the 08:45 restart.
    if not seen.api_up and not state.api_was_down and now.astimezone(IST).time() >= dtime(8, 40):
        state.api_outages.append(now.isoformat())
    state.api_was_down = not seen.api_up
    state.api_outages = [s for s in state.api_outages if now - datetime.fromisoformat(s) <= FLAP_WINDOW]
    if seen.plan_live is not None:
        state.peak_plan_live = max(state.peak_plan_live, seen.plan_live)
    if seen.planned and now.astimezone(IST).time() >= dtime(9, 15):
        state.min_planned = seen.planned if state.min_planned is None else min(state.min_planned, seen.planned)
    if seen.live is not None and seen.failed is not None:
        state.peak_runs = max(state.peak_runs, seen.live + seen.failed)
    if seen.mcx_expected is not None:
        state.mcx_expected = seen.mcx_expected
    if seen.index_priced_since_open:
        state.priced_day = today
    if seen.api_up:
        for name in seen.read_ok:
            state.read_streaks.pop(name, None)
        for name in seen.read_errors:
            state.read_streaks[name] = state.read_streaks.get(name, 0) + 1
        if seen.reads_ok:
            state.blind_since = None
        elif state.blind_since is None:
            state.blind_since = now.isoformat()


# ------------------------------------------------------------------ the rules

@dataclass
class Reading:
    """Each rule's verdict on one reading, with a line of detail, and the day's one-off texts."""

    verdicts: dict[str, tuple[str, str]] = field(default_factory=dict)
    notices: dict[str, str] = field(default_factory=dict)

    def verdict(self, key: str) -> str:
        return self.verdicts.get(key, (UNKNOWN, ""))[0]

    def found(self) -> dict[str, str]:
        return {k: d for k, (v, d) in self.verdicts.items() if v == FOUND}

    @classmethod
    def of(cls, found: dict[str, str], rest: str = HEALTHY) -> "Reading":
        reading = cls({key: (rest, "") for key in RULES})
        reading.verdicts.update({key: (FOUND, detail) for key, detail in found.items()})
        return reading


def _ticks(seen: Seen, group: str, table_known: bool, table_tick: Optional[datetime], stale: int,
           table_label: str) -> tuple[str, str]:
    """Freshness where the strategies read it, the stream; the API's table only when the stream cannot say."""
    if seen.redis_error:
        return UNKNOWN, ""   # redis-down speaks for it
    label = GROUP_LABEL[group]
    head = seen.stream
    last: Optional[datetime] = None
    how = ""
    known = False
    if head is not None:
        if group in head.newest:
            known, last, how = True, head.newest[group], f"newest {label} tick in Redis"
        elif head.exhausted:
            known, how = True, f"no {label} tick anywhere in Redis"
        elif head.oldest is not None and (seen.now - head.oldest).total_seconds() > stale:
            known, last = True, head.oldest
            how = f"no {label} tick in the newest {head.scanned} Redis entries, back to {_hm(head.oldest)}"
    if not known and table_known:
        known, last, how = True, table_tick, f"newest {table_label} in the API's table"
    if not known:
        return UNKNOWN, ""
    if last is None:
        return FOUND, f"{how}: none at all."
    age = (seen.now - last).total_seconds()
    if age > stale:
        return FOUND, f"{how} is {_age(age)} old."
    return HEALTHY, ""


def problems(seen: Seen, state: State) -> Reading:
    """Every rule's verdict on this reading. Reads the state; changes nothing (observe() has run)."""
    out = Reading({key: (CLOSED, "") for key in RULES})

    def put(key: str, verdict: str, detail: str = "") -> None:
        out.verdicts[key] = (verdict, detail)

    now = seen.now
    ist = now.astimezone(IST)
    t = ist.time()
    today = ist.date().isoformat()
    same_day = state.day == today

    if seen.api_up and (seen.login_refused or (state.blind_since is not None
                                               and now - datetime.fromisoformat(state.blind_since) >= BLIND_AFTER)):
        why = next(iter(seen.read_errors.values()), "") or "no read has succeeded"
        out.notices["pager-blind"] = (f"⚠️ Pager blind {_hm(now)}: the API answers but the pager cannot read it "
                                      f"({why}). It cannot see the desk; no call is placed for this.")

    # The desk does not trade at weekends even when the calendar has a special session (Muhurat, a Budget
    # Sunday): no morning job, no Dhan sign-in. Only a planned run someone started makes it a desk day
    # (Sentinel's health.py has the same rule).
    runs_seen = bool(seen.plan_live) or (same_day and state.peak_plan_live > 0)
    if not seen.trading_day or (ist.weekday() >= 5 and not runs_seen):
        return out

    mcx_expected = seen.mcx_expected if seen.mcx_expected is not None else (
        state.mcx_expected if same_day else None)
    # The evening is watched only when MCX runs are expected; not knowing keeps api-down's reach to the close.
    desk_end = seen.last_close if seen.last_close is not None and mcx_expected is not False \
        else datetime.combine(ist.date(), dtime(15, 30), IST)
    desk_hours = dtime(8, 40) <= t < desk_end.astimezone(IST).time()
    evening_end = (seen.last_close - timedelta(minutes=5)).astimezone(IST).time() if seen.last_close else None
    mcx_evening = bool(mcx_expected) and seen.mcx_open and evening_end is not None and dtime(15, 30) <= t < evening_end
    priced = seen.index_priced_since_open or state.priced_day == today
    # The calendar cannot vouch for today (a year's holidays not loaded) and nothing has priced since the open:
    # that is how a holiday the calendar does not know looks, and the morning job concludes the same. On a day
    # the calendar vouches for, the same silence is a feed dead from the open (10 Sep), and it rings.
    probable_holiday = seen.day_unknown and not priced

    if desk_hours:
        if not seen.api_up:
            put("api-down", FOUND, "GET /health has not answered.")
        elif state.outages(now) >= FLAP_OUTAGES:
            put("api-down", FOUND, f"It has gone down {state.outages(now)} times in 30 minutes; "
                                   "the desk keeps restarting it.")
        else:
            put("api-down", HEALTHY)
        streaks = [(n, state.read_streaks.get(n, 0)) for n in WATCHED_READS
                   if state.read_streaks.get(n, 0) >= DEGRADED_STREAK and n in seen.read_errors]
        if not seen.api_up or seen.login_refused:
            put("api-degraded", UNKNOWN)
        elif seen.reads_ok == 0 and len(seen.read_errors) >= 3:
            put("api-degraded", FOUND, "/health answers but every signed-in read failed: "
                                       f"{next(iter(seen.read_errors.values()))}.")
        elif streaks:
            name, count = streaks[0]
            put("api-degraded", FOUND, f"{seen.read_errors[name]}, {count} checks in a row.")
        else:
            put("api-degraded", HEALTHY)
        if seen.disk_free is None:
            put("disk-low", UNKNOWN)
        elif seen.disk_free < DISK_MIN_FREE:
            put("disk-low", FOUND, f"{seen.disk_free / 1024 ** 3:.1f} GB free on /.")
        else:
            put("disk-low", HEALTHY)
        if seen.api_up and seen.dhan and seen.fyers is False and seen.dhan_expires is not None \
                and seen.dhan_expires < desk_end:
            out.notices["dhan-expiring"] = (f"⚠️ Dhan's token ends at {_hm(seen.dhan_expires)}, before today's "
                                            "close, and FYERS is not signed in. No call until it really ends.")

    if dtime(9, 0) <= t < dtime(15, 30) or mcx_evening:
        if not seen.api_up:
            put("no-broker", UNKNOWN)
        elif seen.dhan is False and seen.fyers is False:
            put("no-broker", FOUND, "Dhan and FYERS both signed out.")
        elif seen.dhan or seen.fyers:
            put("no-broker", HEALTHY)
        else:
            put("no-broker", UNKNOWN)

    nse_ticks = seen.nse_open and dtime(9, 17) <= t < dtime(15, 29)
    if nse_ticks or mcx_evening:
        if not seen.api_up or (nse_ticks and probable_holiday):
            put("redis-down", UNKNOWN)
        elif seen.redis_error:
            put("redis-down", FOUND, f"{seen.redis_error}.")
        else:
            put("redis-down", HEALTHY if seen.stream is not None else UNKNOWN)
    if nse_ticks:
        if not seen.api_up or probable_holiday:
            put("no-ticks", UNKNOWN)
        else:
            put("no-ticks", *_ticks(seen, "NSE", seen.quotes_known, seen.newest_index_tick, TICK_STALE,
                                    "index price"))
        if probable_holiday and t >= dtime(9, 17):
            out.notices["probable-holiday"] = (
                f"📅 {today}: the calendar cannot vouch for today ({seen.calendar_warning}) and no index has "
                "priced since the 09:15 open — probably a holiday. No call for no-ticks or runs-not-up.")

    if seen.plan_missing and seen.api_up and t >= dtime(9, 5):
        out.notices["plan-missing"] = (f"⚠️ {today}: no morning plan file (GET {PLAN_PATH} -> 404). The pager "
                                       "cannot check that the plan's runs are up.")
    # The two run rules can describe one event; each call should say it once. Nothing live at all is the most
    # urgent and says it best; otherwise deaths explain a short count better than "fewer than half came up".
    dropped: Optional[bool] = None
    if seen.api_up and seen.live is not None and seen.failed is not None:
        peak_runs = max(state.peak_runs if same_day else 0, seen.live + seen.failed)
        dropped = seen.failed >= RUNS_DROPPED_MIN and seen.failed * 2 >= peak_runs
    none_live = False
    if dtime(9, 30) <= t < dtime(15, 0):
        planned = seen.planned
        if seen.plan_missing or planned == 0:
            pass   # nothing planned: the rule has nothing to check
        elif not seen.api_up or probable_holiday or planned is None or seen.plan_live is None:
            put("runs-not-up", UNKNOWN)
        elif seen.plan_live == 0:
            none_live = True
            put("runs-not-up", FOUND, f"{planned} runs planned, 0 live.")
        else:
            # The day's peak, not the live count: runs that later stop on their own rules lower plan_live.
            peak = max(state.peak_plan_live if same_day else 0, seen.plan_live)
            base = state.min_planned if same_day and state.min_planned else planned
            if not (t >= dtime(9, 40) and peak * 2 < base):
                put("runs-not-up", HEALTHY)
            elif dropped:
                put("runs-not-up", UNKNOWN)   # runs-dropped says why (its window spans this one)
            else:
                put("runs-not-up", FOUND, f"{planned} runs planned; at most {peak} have been live at once today.")

    if dtime(9, 20) <= t < dtime(15, 10):
        if dropped is None or none_live:
            put("runs-dropped", UNKNOWN)
        elif dropped:
            put("runs-dropped", FOUND, f"{seen.failed} runs died today (runner exited, failed to start or "
                                       f"lost in an API restart); {seen.live} live.")
        else:
            put("runs-dropped", HEALTHY)

    if mcx_evening:
        if not seen.api_up:
            put("mcx-no-ticks", UNKNOWN)
            put("mcx-runs-dropped", UNKNOWN)
        else:
            put("mcx-no-ticks", *_ticks(seen, "MCX", seen.quotes_known, seen.newest_mcx_tick, MCX_TICK_STALE,
                                        "MCX future"))
            if seen.mcx_live is None or seen.mcx_failed is None:
                put("mcx-runs-dropped", UNKNOWN)
            elif seen.mcx_live == 0 and seen.mcx_failed >= 1:
                put("mcx-runs-dropped", FOUND, f"{seen.mcx_failed} MCX run(s) died; none is live.")
            else:
                put("mcx-runs-dropped", HEALTHY)
    return out


# ------------------------------------------------------------------ who it tells

def _needed_hits(key: str) -> int:
    """Sightings a page needs: as many as a problem present on every pass of its grace would have."""
    return GRACE[key] // CHECK_EVERY + 1


def step(state: State, seen: Seen, reading: Reading,
         text: Callable[[str], bool], call: Callable[[str], bool]) -> None:
    """Advance every problem by one reading: page after its grace, re-call, text, resolve, close."""
    now = seen.now
    roll_day(state, now)
    stamp = now.isoformat()
    ist = _hm(now)
    can_call = getattr(call, "configured", True)
    for key, (verdict, detail) in reading.verdicts.items():
        p = state.open.get(key)
        if verdict == FOUND:
            # An unpaged problem not seen for a while (the pager was stopped, or could not tell) starts again:
            # its old `since` must not page on the first reading after the gap.
            if p is not None and not p.paged and (p.last_seen is None or
                                                  now - datetime.fromisoformat(p.last_seen) > STALE_AFTER):
                p = None
            if p is None:
                p = state.open[key] = Problem(since=stamp)
            p.last_seen, p.hits, p.clear_passes = stamp, p.hits + 1, 0
            held = (now - datetime.fromisoformat(p.since)).total_seconds()
            if not p.paged and (held < GRACE[key] or p.hits < _needed_hits(key)):
                continue
            today = state.calls_today.get(key, 0)
            may_call = can_call and p.attempts < MAX_CALLS and today < DAILY_CALL_CEILING
            due = p.last_call is None or now - datetime.fromisoformat(p.last_call) >= RECALL_EVERY
            if not p.paged:
                p.paged, p.last_text = True, stamp
                if may_call:
                    tail = "Calling the owner."
                elif not can_call:
                    tail = "No phone call: no call service is set up."
                else:
                    tail = f"No call: {today} made today for {key}, the most a day allows."
                text(f"🚨 PAGE {ist} — {key}: {detail} (for {int(held // 60)} min). {tail}")
            if may_call and due:
                state.calls_today[key] = today + 1
                p.attempts += 1
                p.last_call = stamp
                if call(f"{WORDS[key]} {detail}"):
                    p.placed += 1
                elif p.last_fail_text is None or now - datetime.fromisoformat(p.last_fail_text) >= TEXT_EVERY:
                    p.last_fail_text = stamp
                    why = getattr(call, "last_error", "") or "see pager.log"
                    text(f"📵 CALL FAILED {ist} — {key}: the phone did not ring ({why}). "
                         + ("Trying again in 10 min." if p.attempts < MAX_CALLS else "No more calls this episode."))
            elif now - datetime.fromisoformat(p.last_text or p.since) >= TEXT_EVERY:
                p.last_text = stamp
                text(f"🚨 STILL {ist} — {key}: {detail} (for {int(held // 60)} min; {p.placed} of {p.attempts} "
                     f"call(s) placed this episode, {state.calls_today.get(key, 0)} today).")
        elif p is None:
            continue
        elif verdict == HEALTHY:
            p.clear_passes += 1
            if p.clear_passes >= RESOLVE_AFTER:
                del state.open[key]
                if p.paged:
                    text(f"✅ RESOLVED {ist} — {key} (clear for {RESOLVE_AFTER} checks; {p.placed} call(s) placed).")
        elif verdict == CLOSED:
            del state.open[key]
            if p.paged:
                text(f"⏹ {ist} — {key}: its check window has closed. NOT verified fixed; look before you trust it.")
        # UNKNOWN: held as it is, neither nearer to paging nor to resolving.

    today = state.day
    for key, message in reading.notices.items():
        if state.notices.get(key) != today:
            state.notices[key] = today
            text(message)
    if "pager-blind" not in reading.notices:
        state.notices.pop("pager-blind", None)   # seeing again re-arms it


def telegram_sender(env: dict[str, str]) -> Callable[[str], bool]:
    token = env.get("TELEGRAM_BOT_TOKEN")
    chat = env.get("TELEGRAM_SYSTEM_CHAT_ID") or env.get("TELEGRAM_CHAT_ID")

    def send(message: str) -> bool:
        log.info("TEXT %s", message)
        if not token or not chat:
            return False
        try:
            r = requests.post(f"https://api.telegram.org/bot{token}/sendMessage", timeout=15,
                              json={"chat_id": chat, "text": message, "disable_web_page_preview": True})
        except requests.RequestException as exc:
            log.warning("telegram failed: %s", type(exc).__name__)   # never the URL: it holds the token
            return False
        if not r.ok:
            log.warning("telegram answered %s", r.status_code)
        return r.ok
    return send


def no_call() -> Callable[[str], bool]:
    """
    The phone-call slot with nothing in it, so every page is a text.

    CallMeBot filled it until 1 Oct 2026. Its calls were refused ("Someone reported CallMeBot as spammer") and
    it asked the owner for money, so it was removed. A call service put here returns True only when a call
    really went out, and sets last_error when it did not; step() does the rest.
    """
    def call(words: str) -> bool:
        call.last_error = "no phone-call service is set up"
        return False

    call.configured = False
    call.last_error = ""
    return call


def heartbeat_sender(url: Optional[str]) -> Optional[Callable[[datetime], bool]]:
    """
    A ping to a dead-man's switch each pass on weekdays from 08:00 IST, so a dead box, a lost network or a dead
    pager is noticed from outside (healthchecks.io, cron "* 8-23 * * 1-5" in Asia/Kolkata, its "down" action
    an email or push from healthchecks.io). It reports that the pager is alive, not that the desk is healthy. It never raises.
    """
    if not url:
        return None
    failing = {"now": False}

    def ping(now: datetime) -> bool:
        ist = now.astimezone(IST)
        if ist.weekday() >= 5 or ist.time() < HEARTBEAT_FROM:
            return False
        try:
            ok = requests.get(url, timeout=10).ok
            why = "" if ok else "not 2xx"
        except Exception as exc:
            ok, why = False, type(exc).__name__
        if not ok and not failing["now"]:
            log.warning("heartbeat ping failed: %s", why)   # never the URL: anyone holding it can ping
        failing["now"] = not ok
        return ok
    return ping


def redis_stream_reader(env: dict[str, str]) -> Optional[Callable[[], StreamHead]]:
    """Reads the head of the tick stream the way Sentinel does; None when redis-py is not installed."""
    try:
        import redis
    except ImportError:
        log.warning("redis-py is not installed: freshness falls back to the API's table")
        return None
    try:
        client = redis.Redis(host=env.get("REDIS_HOST", "localhost"), port=int(env.get("REDIS_PORT") or 6379),
                             password=env.get("REDIS_PASSWORD") or None, db=int(env.get("REDIS_DB") or 0),
                             socket_timeout=5, socket_connect_timeout=5, decode_responses=True)
    except (TypeError, ValueError) as exc:
        log.warning("Redis settings unusable (%s): freshness falls back to the API's table", type(exc).__name__)
        return None
    name = env.get("REDIS_STREAM_NAME") or "market:ticks"

    def read() -> StreamHead:
        try:
            return read_stream(client, name)
        except (redis.exceptions.ConnectionError, redis.exceptions.TimeoutError) as exc:
            raise RedisDown(f"Redis unreachable ({type(exc).__name__})") from exc
    return read


def disk_free_reader(path: str = "/") -> Callable[[], int]:
    return lambda: shutil.disk_usage(path).free


class Pager:
    """The loop between passes: the state it keeps in memory, and whether it could last write it down."""

    def __init__(self, state_path: Path, api: Api, text: Callable[[str], bool], call: Callable[[str], bool],
                 stream: Optional[Callable[[], StreamHead]] = None, disk: Optional[Callable[[], int]] = None,
                 heartbeat: Optional[Callable[[datetime], bool]] = None) -> None:
        self.state_path, self.api, self.text, self.call = state_path, api, text, call
        self.stream, self.disk, self.heartbeat = stream, disk, heartbeat
        # Loaded once. Reloaded every pass, a write that failed (a full disk) lost each new problem's `since`
        # and the pager could never page.
        self.state = State.load(state_path)
        self.save_failed = False
        self.failed_passes = 0

    def run_pass(self, now: datetime) -> Optional[tuple[Seen, Reading]]:
        result: Optional[tuple[Seen, Reading]] = None
        try:
            seen = read_desk(self.api, now, self.state, self.stream, self.disk)
            observe(self.state, seen)
            reading = problems(seen, self.state)
            step(self.state, seen, reading, self.text, self.call)
            result = (seen, reading)
            self.failed_passes = 0
        except Exception as exc:
            # A pass that failed is "could not tell": nothing opens, nothing resolves.
            log.warning("pass failed: %s: %s", type(exc).__name__, str(exc)[:160])
            self.failed_passes += 1
            if self.failed_passes == BLIND_PASSES:
                self.text(f"⚠️ Pager {_hm(now)}: its last {BLIND_PASSES} passes failed ({type(exc).__name__}); "
                          "it cannot see the desk. See pager.log.")
        self.save()
        if result is not None and self.heartbeat is not None:
            self.heartbeat(now)
        return result

    def save(self) -> None:
        try:
            self.state.save(self.state_path)
        except OSError as exc:
            if not self.save_failed:
                log.warning("state save failed: %s", exc)
                self.text(f"⚠️ Pager cannot write its state ({exc.strerror or type(exc).__name__}); "
                          "it keeps paging from memory.")
            self.save_failed = True
            return
        if self.save_failed:
            log.info("state saved again")
        self.save_failed = False


def _jsonable(value: Any) -> Any:
    if isinstance(value, datetime):
        return value.isoformat()
    if isinstance(value, dict):
        return {str(k): _jsonable(v) for k, v in value.items()}
    if isinstance(value, (list, tuple, set)):
        return [_jsonable(v) for v in value]
    if hasattr(value, "__dataclass_fields__"):
        return _jsonable(vars(value))
    return value


def main(argv: list[str]) -> int:
    once = "--once" in argv
    handlers: list[logging.Handler] = [logging.StreamHandler()]
    if not once:
        handlers.append(logging.FileHandler(HERE / "pager.log"))
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)
    env = load_env(REPO / ".env")
    cfg = load_env(HERE / "pager.env")
    api = Api(env.get("API_BASE_URL", "http://localhost:5025"), env.get("ADMIN_USERNAME", ""),
              env.get("ADMIN_PASSWORD", ""))
    text, call = telegram_sender(env), no_call()

    if "--test" in argv:
        t_ok = text("🔔 Pager test: this is the channel a real page would use.")
        print(f"text sent: {t_ok}")
        return 0 if t_ok else 1

    stream, disk = redis_stream_reader(env), disk_free_reader("/")
    if once:
        state = State.load(HERE / "state.json")
        now = datetime.now(timezone.utc)
        seen = read_desk(api, now, state, stream, disk)
        observe(state, seen)
        reading = problems(seen, state)
        print(json.dumps(_jsonable(vars(seen)), indent=1, default=str))
        print("verdicts:", json.dumps({k: v for k, v in reading.verdicts.items() if v[0] != CLOSED}))
        print("problems:", reading.found() or "none")
        print("notices:", reading.notices or "none")
        return 0

    pager = Pager(HERE / "state.json", api, text, call, stream, disk,
                  heartbeat_sender(cfg.get("PAGER_HEARTBEAT_URL")))
    while True:
        began = time.monotonic()
        now = datetime.now(timezone.utc)
        result = pager.run_pass(now)
        if result is not None:
            seen, reading = result
            state = pager.state
            log.info("pass: api=%s dhan=%s fyers=%s plan=%s/%s runs=%s live, %s died (peak %s) stream=%s "
                     "calendar=%s problems=%s", seen.api_up, seen.dhan, seen.fyers, seen.plan_live, seen.planned,
                     seen.live, seen.failed, state.peak_runs,
                     "down" if seen.redis_error else ("read" if seen.stream else "-"), seen.calendar,
                     ",".join(reading.found()) or "-")
        time.sleep(max(5.0, CHECK_EVERY - (time.monotonic() - began)))


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
