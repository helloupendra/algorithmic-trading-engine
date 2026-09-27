"""
The checks. Each is a function of one checkup's :class:`Inputs` and returns
its items — usually one. A check says what it found in numbers, and when a
person has to do something, what: the console page, the command, the log.

A check that cannot look (the API does not answer, no database on this
machine) is reported as not checked, never as fine: :func:`run_checks` turns
its exception into a ``skip`` item.

Every rule here is one of the things the desk's operator looked at by hand in
the first weeks — the Dhan token that ended before the MCX close, the plan
that started 21 of 23 runs, the 24 runs left Pending with 18 open legs, the
archive that failed silently for four days, the supervisor still running
last week's desk.sh.
"""
from __future__ import annotations

import json
import re
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Optional

from sentinel.agents.trading import RUNNING_PATH, _parse_runs, today_path
from sentinel.checkup.model import Item, State
from sentinel.checkup.reads import DeskReads
from sentinel.clock import IST, MCX_CLOSE, NSE_CLOSE, ist_date, to_ist
from sentinel.context import SentinelContext

DATA, STRATEGIES, POSITIONS, ANALYSIS, DESK, CALENDAR = (
    "Brokers & data", "Strategies", "Positions", "Analysis", "Desk", "Calendar")

# Console pages an item can point to.
CONNECTORS = "/admin/broker"
FEEDS = "/admin/data/live"
LIVE_RUNS = "/admin/strategies"
POSITIONS_PAGE = "/trader/positions"
FORECASTS = "/admin/analysis"
INCIDENTS = "/admin/incidents"
DEPLOYMENTS = "/admin/system/deployments"
MARKET_CALENDAR = "/admin/system/calendar"

#: How many names a detail lists before "and N more".
LISTED = 6


class Unavailable(Exception):
    """
    The check could not be made, and why. ``problem`` False: it does not apply
    here or now (no FYERS on this desk, forecasts before 08:50) — "not
    checked", and nothing to do. ``problem`` True: it should have been
    possible (the API answered with an error) — worth a look.
    """

    def __init__(self, reason: str, problem: bool = False) -> None:
        super().__init__(reason)
        self.problem = problem


@dataclass
class Inputs:
    """What one checkup reads, each API answer fetched once for all its checks."""

    ctx: SentinelContext
    reads: Optional[DeskReads]
    slot: str
    _answers: dict[str, tuple[bool, Any]] = field(default_factory=dict)
    # Set when the API timed out or refused the connection, or the database
    # did not answer: the checks that follow skip it at once instead of each
    # waiting for the same answer (which would hold up the other agents'
    # rounds), and the report says so once, not once per check.
    _api_down: str = ""
    _db_down: str = ""

    @property
    def now(self) -> datetime:
        return self.ctx.now()

    @property
    def ist(self) -> datetime:
        return to_ist(self.now)

    @property
    def day(self) -> str:
        return ist_date(self.now)

    def api(self, path: str) -> Any:
        if self._api_down and path not in self._answers:
            raise Unavailable("the API is not answering")
        if path not in self._answers:
            try:
                self._answers[path] = (True, self.ctx.api_get(path))
            except Exception as exc:
                self._answers[path] = (False, exc)
                if "Timeout" in type(exc).__name__ or "ConnectionError" in type(exc).__name__:
                    self._api_down = type(exc).__name__
        ok, value = self._answers[path]
        if not ok:
            if self._api_down:
                raise Unavailable("the API is not answering")
            raise Unavailable(f"the API answered {path.split('?')[0]} with an error ({_short(value)})", problem=True)
        return value

    def db(self) -> DeskReads:
        if self.reads is None:
            raise Unavailable("no database connection here (POSTGRES_PASSWORD is not set)")
        if self._db_down:
            raise Unavailable("the database is not answering")
        return self.reads

    def at_ist(self, hhmm: time, day: Optional[date] = None) -> datetime:
        """A time of day in IST on today's IST date (or ``day``), as UTC."""
        d = day or self.ist.date()
        return datetime.combine(d, hhmm, tzinfo=IST).astimezone(timezone.utc)

    def last_close(self) -> Optional[datetime]:
        """When today's last market closes — MCX's close, from the calendar — or None on a day with no session."""
        session = self.ctx.session()
        if not session.trading_day:
            return None
        return session.mcx_close or self.at_ist(MCX_CLOSE)


Check = Callable[[Inputs], list[Item]]


# ----------------------------------------------------------------- helpers

def _short(exc: BaseException) -> str:
    text = " ".join(str(exc).split())
    return f"{type(exc).__name__}: {text[:160]}" if text else type(exc).__name__


def _hm(moment: Optional[datetime]) -> str:
    return to_ist(moment).strftime("%H:%M") if moment else "?"


def _when(moment: Optional[datetime], now: datetime) -> str:
    """ "14:05" today, "08:12 tomorrow", else "08:12 on 29 Sep". """
    if moment is None:
        return "?"
    local, today = to_ist(moment), to_ist(now).date()
    if local.date() == today:
        return local.strftime("%H:%M today")
    if local.date() == today + timedelta(days=1):
        return local.strftime("%H:%M tomorrow")
    if local.date() == today - timedelta(days=1):
        return local.strftime("%H:%M yesterday")
    return local.strftime("%H:%M on %-d %b")


def _rupees(amount: float) -> str:
    sign = "−" if amount < 0 else "+" if amount > 0 else ""
    return f"{sign}₹{abs(amount):,.0f}"


def _n(count: int, one: str, many: Optional[str] = None) -> str:
    return f"{count} {one if count == 1 else (many or one + 's')}"


def _listed(names: list[str]) -> str:
    shown = ", ".join(names[:LISTED])
    return shown + (f" and {len(names) - LISTED} more" if len(names) > LISTED else "")


def _parse_utc(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value:
        return None
    try:
        moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def _provider(inp: Inputs, key: str) -> Optional[dict]:
    body = inp.api("/api/Providers")
    for row in body if isinstance(body, list) else []:
        if isinstance(row, dict) and str(row.get("key", "")).lower() == key:
            return row
    return None


def _signed_in(provider: dict) -> tuple[bool, Optional[datetime]]:
    session = provider.get("session") if isinstance(provider.get("session"), dict) else {}
    ok = bool(session.get("isConnected")) and not session.get("needsReconnect")
    return ok, _parse_utc(session.get("expiresUtc"))


def _live_strategy_runs(inp: Inputs):
    """Runs live now that are strategies (not an alerter, not a manual book)."""
    return [r for r in _parse_runs(inp.api(RUNNING_PATH)) if r.live and not r.not_a_strategy]


def _leg(row: dict) -> str:
    return f"{row['symbol']} {str(row['direction']).lower()} {row['quantity']}"


# ------------------------------------------------------------ brokers & data

def _auto_sign_in_words(auto: Optional[dict]) -> str:
    """What the automatic Dhan sign-in has to say for itself, when it is not simply working."""
    if not isinstance(auto, dict):
        return ""
    if not auto.get("configured"):
        missing = ", ".join(str(m) for m in auto.get("missing") or []) or "its secrets"
        return f"the automatic sign-in is not set up (missing {missing})"
    if not auto.get("enabled"):
        return "the automatic sign-in is switched off"
    message = str(auto.get("lastMessage") or "").strip()
    if auto.get("stoppedForToday"):
        return "the automatic sign-in stopped for today" + (f": {message}" if message else "")
    if auto.get("lastOk") is False:
        return "the last automatic sign-in failed" + (f": {message}" if message else "")
    return ""


def dhan_token(inp: Inputs) -> list[Item]:
    def item(state: State, detail: str, action: str = "") -> list[Item]:
        return [Item("dhan-token", DATA, "Dhan token", state, detail, action, CONNECTORS)]

    dhan = _provider(inp, "dhan")
    if dhan is None or not dhan.get("isConfigured"):
        raise Unavailable("Dhan is not set up on this desk")
    try:
        auto = inp.api("/api/Dhan/auto-sign-in")
    except Unavailable:
        auto = None

    if inp.slot == "weekly":
        # No session on a Sunday, and the token is replaced on Monday morning:
        # what matters is that the replacing will happen.
        if not isinstance(auto, dict):
            raise Unavailable("the API did not say how the automatic sign-in is set up")
        trouble = _auto_sign_in_words(auto)
        if trouble and (not auto.get("configured") or not auto.get("enabled")):
            return item(State.WARN, f"On Monday the token will not renew itself: {trouble}.",
                        "Put DHAN_PIN and DHAN_TOTP_SECRET in the server's .env and switch Dhan:AutoSignIn:Enabled "
                        "on; otherwise sign in by hand on Connectors → Dhan every morning before 08:45.")
        last = _parse_utc(auto.get("lastAttemptUtc"))
        said = f"; its last sign-in was at {_when(last, inp.now)}" if last else ""
        if auto.get("lastOk") is False:
            return item(State.WARN, f"The automatic sign-in is on, but it failed last time{said}: "
                                    f"{auto.get('lastMessage') or 'no reason given'}.",
                        "Try Connectors → Dhan → Connect once to see Dhan's answer before Monday 08:00.")
        return item(State.OK, f"The automatic sign-in is set up and on{said}; it renews the token at 08:00 "
                              f"on trading days.")

    signed_in, expires = _signed_in(dhan)
    now = inp.now
    if not signed_in or (expires is not None and expires <= now):
        trouble = _auto_sign_in_words(auto)
        return item(State.FAIL, "Dhan is signed out" + (f", and {trouble}" if trouble else "") + ".",
                    "Sign in on Connectors → Dhan → Connect. Until then no strategy gets Dhan's data.")
    need = inp.last_close()
    if need is not None and now < need and expires is not None and expires < need:
        window = ""
        if isinstance(auto, dict) and auto.get("morningFromIst"):
            window = f" (the automatic sign-in renews only between {auto['morningFromIst']} and {auto.get('morningUntilIst')})"
        return item(State.WARN, f"Signed in, but the token ends at {_when(expires, now)}, before today's last close "
                                f"at {_hm(need)}.",
                    f"Sign in again on Connectors → Dhan → Connect before {_hm(expires)}{window}.")
    detail = f"Signed in; the token is good until {_when(expires, now)}." if expires else "Signed in."
    if isinstance(auto, dict) and auto.get("lastOk") and _parse_utc(auto.get("lastAttemptUtc")):
        taken = _parse_utc(auto.get("lastAttemptUtc"))
        if ist_date(taken) == inp.day:
            detail += f" Taken by the automatic sign-in at {_hm(taken)}."
    return item(State.OK, detail)


def fyers_backup(inp: Inputs) -> list[Item]:
    fyers = _provider(inp, "fyers")
    if fyers is None or not fyers.get("isConfigured"):
        raise Unavailable("FYERS is not set up on this desk")
    signed_in, _ = _signed_in(fyers)
    if signed_in:
        return [Item("fyers-backup", DATA, "FYERS backup", State.OK, "Signed in: ready as the backup feed.",
                     link=CONNECTORS)]
    enabled, dry_run = _failover_settings(inp)
    if enabled and not dry_run:
        return [Item("fyers-backup", DATA, "FYERS backup", State.WARN,
                     "Not signed in, and the feed failover is live: if Dhan goes silent it has nothing to switch to.",
                     "Sign in on Connectors → FYERS before the open.", CONNECTORS)]
    return [Item("fyers-backup", DATA, "FYERS backup", State.INFO,
                 "Not signed in. The data runs on Dhan; FYERS is only the backup.",
                 "If you want the backup ready, sign in on Connectors → FYERS.", CONNECTORS)]


def _feeds_expected(inp: Inputs) -> Optional[bool]:
    """True: a feed should be running now; False: none should; None: either is fine."""
    if inp.slot == "morning":
        return True
    if inp.slot == "night":
        return False
    last = inp.last_close()
    if last is None:
        return None   # no session today: a feed started by hand is someone's choice
    if inp.now >= last + timedelta(minutes=30):
        return False
    if inp.now < inp.at_ist(time(8, 50)):
        return None   # before the morning job: last night's feed is market-close's business
    return True


def feeds(inp: Inputs) -> list[Item]:
    body = inp.api("/api/Feeds")
    rows = [f for f in body if isinstance(f, dict)] if isinstance(body, list) else []
    running = [str(f.get("displayName") or f.get("key")) for f in rows if f.get("isRunning")]
    expected = _feeds_expected(inp)

    def item(state: State, detail: str, action: str = "") -> list[Item]:
        return [Item("feeds", DATA, "Live feeds", state, detail, action, FEEDS)]

    if expected is True and not running:
        return item(State.FAIL, "No live feed is running.",
                    f"Start the Dhan feed on Data → Live feeds. The 08:45 job starts it; what it said is in "
                    f"logs/market-open-{inp.day}.log.")
    if expected is False and running:
        return item(State.WARN, f"Still running after the day's last close: {_listed(running)}.",
                    "Stop it on Data → Live feeds. A feed left on overnight reconnects in a loop once its token "
                    "ends (16 Sep); market-close.sh should have stopped it — its report is "
                    "logs/market-close-<date>.log.")
    if running:
        return item(State.OK, f"Running: {_listed(running)}.")
    return item(State.OK, "Every feed is stopped.")


# ---------------------------------------------------------------- strategies

def plan(inp: Inputs) -> list[Item]:
    ist = inp.ist
    if inp.slot != "morning" and not (time(8, 50) <= ist.time() < time(15, 25) and inp.ctx.session().trading_day):
        raise Unavailable("the plan is checked between 08:50 and 15:25 on trading days")
    wanted = inp.ctx.plan()
    if wanted is None:
        raise Unavailable("there is no morning plan (config/morning-plan.txt)")
    live = {r.key for r in _live_strategy_runs(inp)}
    expected = [(a.lower(), s.lower(), u.upper()) for a, s, u in wanted.expected_runs()]
    missing = [k for k in expected if k not in live]
    per_account = Counter(a for a, _, _ in expected)
    accounts = ", ".join(f"{a} {n}" for a, n in per_account.items())
    if not missing:
        return [Item("plan", STRATEGIES, "Morning plan", State.OK,
                     f"All {len(expected)} planned runs are live ({accounts}).", link=LIVE_RUNS)]
    names = [f"{a} {s} {u}" for a, s, u in missing]
    return [Item("plan", STRATEGIES, "Morning plan", State.FAIL,
                 f"{len(missing)} of {len(expected)} planned runs are not live: {_listed(names)}.",
                 f"Start them from Strategies → Live Runner. Why they did not start is in "
                 f"logs/market-open-{inp.day}.log.", LIVE_RUNS)]


def runs_after_close(inp: Inputs) -> list[Item]:
    live = _live_strategy_runs(inp)
    nse = [r for r in live if r.exchange == "NSE"]
    mcx = [r for r in live if r.exchange == "MCX"]
    if nse:
        names = [f"{r.user} {r.strategy} {r.underlying} (#{r.run_id})" for r in nse]
        return [Item("runs-after-close", STRATEGIES, "Runs after the close", State.FAIL,
                     f"{_n(len(nse), 'NSE/BSE run')} still running after the 15:30 close: {_listed(names)}.",
                     "Stop them from Strategies → Live Runner. The close stop is MarketHoursService: look for it in "
                     "logs/api.log around 15:30.", LIVE_RUNS)]
    tail = ""
    if mcx:
        close = inp.ctx.session().mcx_close or inp.at_ist(MCX_CLOSE)
        tail = f" {_n(len(mcx), 'MCX run')} trade{'s' if len(mcx) == 1 else ''} on until the MCX close at {_hm(close)}."
    return [Item("runs-after-close", STRATEGIES, "Runs after the close", State.OK,
                 "Every NSE and BSE run stopped at the close." + tail, link=LIVE_RUNS)]


def runs_overnight(inp: Inputs) -> list[Item]:
    live = _live_strategy_runs(inp)
    if not live:
        return [Item("runs-overnight", STRATEGIES, "Runs overnight", State.OK, "No strategy run is live.")]
    names = [f"{r.user} {r.strategy} {r.underlying} (#{r.run_id})" for r in live]
    return [Item("runs-overnight", STRATEGIES, "Runs overnight", State.FAIL,
                 f"{_n(len(live), 'run')} still live after the day's last close: {_listed(names)}.",
                 "Stop them from Strategies → Live Runner. market-close.sh stops everything at 23:58; its report "
                 "is logs/market-close-<date>.log.", LIVE_RUNS)]


def day_result(inp: Inputs) -> list[Item]:
    runs = [r for r in _parse_runs(inp.api(today_path(inp.day))) if not r.role]
    if not runs:
        return [Item("day-result", STRATEGIES, "Today's result", State.INFO, "No run traded today.")]
    per: dict[str, list] = defaultdict(list)
    for r in runs:
        per[r.user].append(r)
    parts = []
    for account, rs in sorted(per.items()):
        strategies = [r for r in rs if r.strategy.lower() != "manual"]
        trades = sum(r.trades for r in rs)
        parts.append(f"{account}: {_n(len(strategies), 'run')}, {_n(trades, 'trade')}, net "
                     f"{_rupees(sum(r.net for r in rs))}")
    return [Item("day-result", STRATEGIES, "Today's result", State.INFO,
                 "; ".join(parts) + ". Net is after charges; paper trading.", link=LIVE_RUNS)]


# ----------------------------------------------------------------- positions

def manual_books(inp: Inputs) -> list[Item]:
    legs = inp.db().manual_book_legs()
    key, title = "manual-books", "Manual books"
    if not legs:
        return [Item(key, POSITIONS, title, State.OK, "Nothing is held in the manual books.", link=POSITIONS_PAGE)]
    after_nse_close = inp.slot == "close" or (inp.slot == "on-request" and inp.ist.time() >= NSE_CLOSE)
    unticked = [l for l in legs if not l["carry"] and str(l["symbol"]).startswith(("NSE:", "BSE:"))]
    if after_nse_close and unticked:
        return [Item(key, POSITIONS, title, State.WARN,
                     f"{_n(len(unticked), 'NSE/BSE leg')} still open without the Carry tick; the close should have "
                     f"squared {'it' if len(unticked) == 1 else 'them'} off: "
                     f"{_listed([l['account'] + ' ' + _leg(l) for l in unticked])}.",
                     "Close them on Positions, or tick Carry if you meant to hold them. The square-off is "
                     "ManualIntradaySquareOff in logs/api.log around 15:30.", POSITIONS_PAGE)]
    unrealized = sum(l["unrealized"] for l in legs)
    carried = [l for l in legs if l["carry"]]
    naked = [l for l in carried if l["stop"] is None and l["target"] is None]
    detail = (f"{_n(len(legs), 'leg')} held ({len(carried)} ticked to carry overnight), unrealized "
              f"{_rupees(unrealized)}: {_listed([l['account'] + ' ' + _leg(l) for l in legs])}.")
    action = ""
    if naked:
        detail += f" {_n(len(naked), 'carried leg')} ha{'s' if len(naked) == 1 else 've'} no stop-loss or target."
        action = "Watch them on Positions: nothing closes a carried leg overnight except its expiry."
    return [Item(key, POSITIONS, title, State.INFO, detail, action, POSITIONS_PAGE)]


def carried_today(inp: Inputs) -> list[Item]:
    count = inp.db().carried_since(inp.at_ist(time(0, 0)))
    if count == 0:
        return [Item("carried-today", POSITIONS, "Carried at the close", State.INFO,
                     "No strategy leg was ticked to carry today.")]
    return [Item("carried-today", POSITIONS, "Carried at the close", State.INFO,
                 f"{_n(count, 'strategy leg')} moved to the manual books at the close, at their entry price.",
                 link=POSITIONS_PAGE)]


def strategy_legs_after_close(inp: Inputs) -> list[Item]:
    rows = inp.db().strategy_legs_open(("NSE:", "BSE:"))
    if not rows:
        return [Item("legs-after-close", POSITIONS, "Strategy legs after the close", State.OK,
                     "No NSE or BSE leg is open in a strategy run.")]
    names = [f"#{r['run']} {r['strategy']} ({r['run_status']}) {_leg(r)}" for r in rows]
    return [Item("legs-after-close", POSITIONS, "Strategy legs after the close", State.FAIL,
                 f"{_n(len(rows), 'NSE/BSE leg')} still open in strategy runs: {_listed(names)}.",
                 "Close them from the run's card on Strategies → Live Runner. A square-off that failed at the close "
                 "is in logs/api.log around 15:30.", LIVE_RUNS)]


def stale_runs(inp: Inputs) -> list[Item]:
    rows = inp.db().stale_pending_runs(1)
    if not rows:
        return [Item("stale-runs", POSITIONS, "Runs stuck in Pending", State.OK, "No live run is stuck in Pending.")]
    legs = sum(r["open_legs"] for r in rows)
    oldest = min((r["created_utc"] for r in rows if r["created_utc"]), default=None)
    since = f" since {to_ist(oldest).strftime('%-d %b')}" if oldest else ""
    return [Item("stale-runs", POSITIONS, "Runs stuck in Pending", State.WARN,
                 f"{_n(len(rows), 'live run')} never left Pending{since}, holding {_n(legs, 'open leg')}. "
                 "Every page that lists runs or open positions still shows them.",
                 "On the server: ./scripts/close-stale-runs.sh lists them; run it again with --apply to close them "
                 "(it backs the rows up first and books no P&L).")]


def orphaned_legs(inp: Inputs) -> list[Item]:
    rows = inp.db().orphaned_legs()
    if not rows:
        return [Item("orphaned-legs", POSITIONS, "Legs in ended runs", State.OK, "No open leg in a run that has ended.")]
    names = [f"#{r['run']} {r['strategy']} ({r['run_status']}) {_leg(r)}" for r in rows]
    return [Item("orphaned-legs", POSITIONS, "Legs in ended runs", State.WARN,
                 f"{_n(len(rows), 'open leg')} in runs that have ended: {_listed(names)}.",
                 "Close them from the run's card. When a run's square-off fails, logs/api.log says why at the "
                 "time it stopped.", LIVE_RUNS)]


# ------------------------------------------------------------------ analysis

def _forecasts_today(inp: Inputs) -> list[dict]:
    body = inp.api(f"/api/Forecasts?from={inp.day}&to={inp.day}&take=1000")
    return [r for r in body if isinstance(r, dict)] if isinstance(body, list) else []


def forecasts_issued(inp: Inputs) -> list[Item]:
    if inp.ist.time() < time(8, 52):
        raise Unavailable("forecasts are issued at 08:50")
    rows = _forecasts_today(inp)
    if not rows:
        return [Item("forecasts-issued", ANALYSIS, "Forecasts issued", State.WARN,
                     "No forecast has been issued for today.",
                     "The API issues them at 08:50 (ForecastScheduler in logs/api.log). By hand on the server: "
                     f"cd src/AlgoTrading.PythonEngine && ../../.venv/bin/python -m analysis issue --session {inp.day}",
                     FORECASTS)]
    models = len({(r.get("modelKey"), r.get("modelVersion")) for r in rows})
    return [Item("forecasts-issued", ANALYSIS, "Forecasts issued", State.OK,
                 f"{len(rows)} forecasts for today from {_n(models, 'model')}.", link=FORECASTS)]


def forecasts_scored(inp: Inputs) -> list[Item]:
    if inp.ist.time() < time(15, 52):
        raise Unavailable("forecasts are scored at 15:50")
    rows = _forecasts_today(inp)
    scored = sum(1 for r in rows if r.get("scoredUtc"))
    if not rows:
        return [Item("forecasts-scored", ANALYSIS, "Forecasts scored", State.WARN,
                     "None was issued today, so none could be scored.",
                     "See why the 08:50 issue did not run: ForecastScheduler in logs/api.log.", FORECASTS)]
    if scored < len(rows):
        return [Item("forecasts-scored", ANALYSIS, "Forecasts scored", State.WARN,
                     f"{scored} of {len(rows)} of today's forecasts are scored.",
                     "The API scores them at 15:50 and again when it starts. By hand on the server: "
                     "cd src/AlgoTrading.PythonEngine && ../../.venv/bin/python -m analysis score", FORECASTS)]
    return [Item("forecasts-scored", ANALYSIS, "Forecasts scored", State.OK,
                 f"All {len(rows)} of today's forecasts are scored; the scoreboard has them.", link=FORECASTS)]


# ---------------------------------------------------------------------- desk

_SEVERITY_RANK = {"critical": 3, "high": 2, "medium": 1, "low": 0}


def incidents(inp: Inputs) -> list[Item]:
    rows = inp.db().live_incidents()
    if not rows:
        return [Item("incidents", DESK, "Open incidents", State.OK, "No open incidents.", link=INCIDENTS)]
    rows.sort(key=lambda r: (-_SEVERITY_RANK.get(str(r["severity"]), 0), r["first_seen_utc"] or inp.now))
    listed = _listed([f"#{r['id']} [{str(r['severity']).upper()}] {r['title']}" for r in rows])
    unhandled = [r for r in rows if r["status"] == "open" and _SEVERITY_RANK.get(str(r["severity"]), 0) >= 2]
    forgotten = [r for r in rows if r["status"] == "acknowledged" and r["acknowledged_utc"]
                 and inp.now - r["acknowledged_utc"] > timedelta(hours=24)]
    detail = f"{_n(len(rows), 'open incident')}: {listed}."
    if unhandled:
        return [Item("incidents", DESK, "Open incidents", State.FAIL, detail,
                     "Open System → Incidents: each says what to try first. Acknowledge it while you work on it, "
                     "and resolve it with the cause and the fix.", INCIDENTS)]
    if forgotten:
        return [Item("incidents", DESK, "Open incidents", State.WARN,
                     detail + f" {_n(len(forgotten), 'was', 'were')} acknowledged more than a day ago.",
                     "Resolve them with the cause and the fix, or they are not being handled.", INCIDENTS)]
    worst = max(_SEVERITY_RANK.get(str(r["severity"]), 0) for r in rows)
    return [Item("incidents", DESK, "Open incidents", State.WARN if worst >= 1 else State.INFO, detail,
                 "Read them on System → Incidents.", INCIDENTS)]


def incident_notes(inp: Inputs) -> list[Item]:
    rows = inp.db().resolved_without_notes(inp.now - timedelta(days=7))
    if not rows:
        return [Item("incident-notes", DESK, "Incident notes", State.OK,
                     "Every incident a person closed this week has its cause or fix written down.", link=INCIDENTS)]
    names = [f"#{r['id']} {r['title']}" for r in rows]
    return [Item("incident-notes", DESK, "Incident notes", State.INFO,
                 f"{_n(len(rows), 'incident')} closed this week without notes: {_listed(names)}.",
                 "Add the cause and what was done (Incidents → the incident → Edit notes): the next time it happens, "
                 "its message says what worked.", INCIDENTS)]


def deploy(inp: Inputs) -> list[Item]:
    path = inp.ctx.repo_root / "data" / "deploy-history.json"
    try:
        records = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise Unavailable(f"no deploy history here ({type(exc).__name__})")
    records = [r for r in records if isinstance(r, dict)] if isinstance(records, list) else []
    if not records:
        raise Unavailable("the deploy history is empty")
    last = max(records, key=lambda r: str(r.get("startedUtc") or ""))
    when = _when(_parse_utc(last.get("startedUtc")), inp.now)
    commit = str(last.get("toCommit") or "?")
    if str(last.get("outcome")) == "failed":
        return [Item("deploy", DESK, "Deploys", State.FAIL,
                     f"The last deploy ({last.get('fromCommit') or '?'} → {commit}, {when}) failed: "
                     f"{last.get('summary') or 'no summary'}.",
                     "Its steps are on System → Deployments and the detail in logs/desk.log at that time. "
                     "The desk tries again on the next commit.", DEPLOYMENTS)]
    code, out = inp.ctx.run(["git", "log", "--oneline", "HEAD..origin/main"])
    waiting = [line for line in out.splitlines() if line.strip()] if code == 0 else []
    if waiting:
        return [Item("deploy", DESK, "Deploys", State.INFO,
                     f"Live on {commit} (deployed {when}); {_n(len(waiting), 'newer commit')} on GitHub "
                     "wait for the deploy gate, which holds builds and restarts while a run is live on a weekday.",
                     link=DEPLOYMENTS)]
    return [Item("deploy", DESK, "Deploys", State.OK, f"Live on {commit}, deployed {when}.", link=DEPLOYMENTS)]


_ETIME = re.compile(r"^(?:(\d+)-)?(?:(\d+):)?(\d+):(\d+)$")


def _elapsed(etime: str) -> Optional[timedelta]:
    """ps's etime, [[dd-]hh:]mm:ss, as a timedelta."""
    m = _ETIME.match(etime.strip())
    if not m:
        return None
    days, hours, minutes, seconds = (int(g) if g else 0 for g in m.groups())
    return timedelta(days=days, hours=hours, minutes=minutes, seconds=seconds)


#: The desk supervisor reads these once, when it starts; a deploy that changes
#: them changes nothing until it is restarted (the API, the console and the
#: notifier are restarted by the deploy itself).
DESK_CODE = ("scripts/desk.sh", "scripts/lib/desk-common.sh")


def desk_code(inp: Inputs) -> list[Item]:
    code, out = inp.ctx.run(["ps", "-eo", "pid,ppid,etime,args"])
    if code != 0:
        raise Unavailable("ps did not answer")
    procs = {}
    for line in out.splitlines()[1:]:
        parts = line.split(None, 3)
        if len(parts) == 4 and parts[0].isdigit() and parts[1].isdigit():
            procs[int(parts[0])] = (int(parts[1]), parts[2], parts[3])
    # The supervisor, not the subshells it forks to start the API.
    desks = [(pid, etime) for pid, (ppid, etime, args) in procs.items()
             if "scripts/desk.sh" in args and "scripts/desk.sh" not in procs.get(ppid, (0, "", ""))[2]]
    if not desks:
        raise Unavailable("the desk supervisor is not running here (it runs on the server)")
    elapsed = _elapsed(max(desks, key=lambda d: _elapsed(d[1]) or timedelta())[1])
    if elapsed is None:
        raise Unavailable("could not read how long the desk supervisor has run")
    started = inp.now - elapsed
    changed = []
    for rel in DESK_CODE:
        try:
            mtime = datetime.fromtimestamp((inp.ctx.repo_root / rel).stat().st_mtime, tz=timezone.utc)
        except OSError:
            continue
        if mtime > started + timedelta(minutes=1):
            changed.append((rel, mtime))
    if not changed:
        return [Item("desk-code", DESK, "Desk supervisor", State.OK,
                     f"Running since {_when(started, inp.now)} on the code that is on disk.")]
    rel, mtime = max(changed, key=lambda c: c[1])
    return [Item("desk-code", DESK, "Desk supervisor", State.WARN,
                 f"Running since {_when(started, inp.now)}, but {rel} changed at {_when(mtime, inp.now)}: it still "
                 "runs the old one, so the change does nothing yet.",
                 "Restart it when no run is live (after the day's last close, or at the weekend): "
                 "sudo systemctl restart algotrading-desk")]


def disk(inp: Inputs) -> list[Item]:
    code, out = inp.ctx.run(["df", "-Pk", str(inp.ctx.repo_root)])
    lines = out.splitlines()
    if code != 0 or len(lines) < 2:
        raise Unavailable("df did not answer")
    fields = lines[1].split()
    size_kb, avail_kb = int(fields[1]), int(fields[3])
    free = avail_kb / size_kb * 100 if size_kb else 0.0
    detail = f"{free:.0f}% free ({avail_kb / 1024 ** 2:.0f} GB of {size_kb / 1024 ** 2:.0f} GB)."
    action = ("The 06:00 archive to Drive frees space once each day is verified (logs/archive-<date>.log); if it "
              "runs and space still falls, look at logs/ and ~/backups.")
    if free < 10:
        return [Item("disk", DESK, "Disk space", State.FAIL, detail, action)]
    if free < 20:
        return [Item("disk", DESK, "Disk space", State.WARN, detail, action)]
    return [Item("disk", DESK, "Disk space", State.OK, detail)]


_ARCHIVE_OK = re.compile(r"archive to Drive: ok, (\d+) file")


def _archive_day(logs: Path, day: str) -> Optional[tuple[str, int]]:
    """("ok", files verified), ("failed", 0), ("running", 0), or None when it did not run that day."""
    try:
        text = (logs / f"archive-{day}.log").read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    ok = _ARCHIVE_OK.findall(text)
    if "archive to Drive FAILED" in text.rsplit("archive to Drive: ok", 1)[-1]:
        return "failed", 0
    if ok:
        return "ok", int(ok[-1])
    return "running", 0


def archive(inp: Inputs) -> list[Item]:
    logs = inp.ctx.logs_dir
    if not any(logs.glob("archive-20*.log")):
        raise Unavailable("no archive has run on this machine")
    fix = ("Nothing is deleted when it fails. Run ./scripts/archive-to-drive.sh on the server and read its log; an "
           "expired Drive sign-in needs: rclone config reconnect openfno-drive:")
    if inp.slot == "weekly":
        days = [(inp.ist.date() - timedelta(days=i)).isoformat() for i in range(7)]
        seen = {d: _archive_day(logs, d) for d in days}
        failed = [d for d, r in seen.items() if r and r[0] == "failed"]
        missing = [d for d, r in seen.items() if r is None]
        files = sum(r[1] for r in seen.values() if r)
        if failed or missing:
            parts = ([f"failed on {', '.join(failed)}"] if failed else []) + \
                    ([f"did not run on {', '.join(missing)}"] if missing else [])
            return [Item("archive", DESK, "Archive to Drive", State.WARN,
                         f"This week it {' and '.join(parts)}; {files} files verified in all.", fix)]
        return [Item("archive", DESK, "Archive to Drive", State.OK,
                     f"Ran every day this week; {files} files copied and verified.")]
    today = _archive_day(logs, inp.day)
    if today is None:
        last = max((p.name[8:18] for p in logs.glob("archive-20*.log")), default="?")
        return [Item("archive", DESK, "Archive to Drive", State.WARN,
                     f"It has not run today; the last run was on {last}.",
                     "It runs from cron at 06:00: crontab -l on the server should list scripts/archive-to-drive.sh.")]
    state, files = today
    if state == "failed":
        return [Item("archive", DESK, "Archive to Drive", State.FAIL, "Today's archive failed.", fix)]
    if state == "running":
        return [Item("archive", DESK, "Archive to Drive", State.INFO, "Today's archive is still running.")]
    return [Item("archive", DESK, "Archive to Drive", State.OK, f"Ran today: {_n(files, 'file')} copied and verified.")]


def _failover_settings(inp: Inputs) -> tuple[bool, bool]:
    """(Enabled, DryRun) as the API reads them: appsettings.json, then .Local.json, then the environment."""
    enabled, dry_run = True, True
    api = inp.ctx.repo_root / "src" / "AlgoTrading.Api"
    for name in ("appsettings.json", "appsettings.Local.json"):
        try:
            section = json.loads((api / name).read_text(encoding="utf-8")).get("FeedFailover") or {}
        except (OSError, ValueError, AttributeError):
            continue
        if isinstance(section.get("Enabled"), bool):
            enabled = section["Enabled"]
        if isinstance(section.get("DryRun"), bool):
            dry_run = section["DryRun"]
    for key, current in (("FeedFailover__Enabled", enabled), ("FeedFailover__DryRun", dry_run)):
        value = inp.ctx.env.get(key, "").strip().lower()
        if value in ("true", "false"):
            if key.endswith("Enabled"):
                enabled = value == "true"
            else:
                dry_run = value == "true"
    return enabled, dry_run


def failover(inp: Inputs) -> list[Item]:
    enabled, dry_run = _failover_settings(inp)
    title = "Feed failover"
    if not enabled:
        return [Item("failover", DATA, title, State.INFO, "Switched off (FeedFailover:Enabled is false).")]
    weekly = inp.slot == "weekly"
    since = inp.now - timedelta(days=7) if weekly else inp.at_ist(time(0, 0))
    period = "in the last 7 days" if weekly else "today"
    events = inp.db().failover_events(since)
    if dry_run:
        n = events["would_switch"]
        return [Item("failover", DATA, title, State.INFO,
                     f"In log mode (dry run): it would have switched Dhan → FYERS {_n(n, 'time')} {period}, and "
                     "switched nothing.",
                     "Your decision: to let it switch by itself, set FeedFailover:DryRun to false in "
                     "appsettings.Local.json and restart the API after the close. FYERS must be signed in for a "
                     "switch to work." if weekly or n else "", FEEDS)]
    return [Item("failover", DATA, title, State.OK,
                 f"Live: it switched Dhan → FYERS {_n(events['switched'], 'time')} {period}.", link=FEEDS)]


# ------------------------------------------------------------------ calendar

#: An MCX holiday can close one of its two sessions only (MarketClosure).
_CLOSURE = {"MorningSession": " morning closed", "EveningSession": " evening closed"}


def _calendar(inp: Inputs, year: int) -> dict:
    body = inp.api(f"/api/MarketCalendar?year={year}")
    return body if isinstance(body, dict) else {}


def _calendar_warnings(body: dict) -> list[str]:
    return [f"{e.get('exchange')}: {e['warning']}" for e in body.get("exchanges") or []
            if isinstance(e, dict) and e.get("warning")]


def calendar_today(inp: Inputs) -> list[Item]:
    session = inp.ctx.session()
    body = _calendar(inp, inp.ist.year)
    warnings = _calendar_warnings(body)
    if warnings:
        return [Item("calendar-today", CALENDAR, "Market calendar", State.WARN, _listed(warnings) + ".",
                     "Fix the calendar on System → Market calendar.", MARKET_CALENDAR)]
    close = session.mcx_close or inp.at_ist(MCX_CLOSE)
    return [Item("calendar-today", CALENDAR, "Today's session", State.INFO,
                 f"NSE and BSE trade 09:15–15:30; MCX until {_hm(close)}.", link=MARKET_CALENDAR)]


def calendar_week(inp: Inputs) -> list[Item]:
    today = inp.ist.date()
    horizon = today + timedelta(days=7)
    body = _calendar(inp, today.year)
    rows = list(body.get("holidays") or [])
    if horizon.year != today.year:
        rows += list(_calendar(inp, horizon.year).get("holidays") or [])
    coming: dict[tuple[str, str], list[str]] = defaultdict(list)
    for h in rows:
        if not isinstance(h, dict):
            continue
        try:
            day = date.fromisoformat(str(h.get("date")))
        except ValueError:
            continue
        if today < day <= horizon:
            coming[(day.isoformat(), str(h.get("name") or "holiday"))].append(
                str(h.get("exchange")) + _CLOSURE.get(str(h.get("closure")), ""))
    items = []
    if coming:
        listed = [f"{date.fromisoformat(d).strftime('%a %-d %b')} {name} ({', '.join(sorted(ex))})"
                  for (d, name), ex in sorted(coming.items())]
        items.append(Item("holidays", CALENDAR, "Holidays this week", State.INFO, _listed(listed) + ".",
                          link=MARKET_CALENDAR))
    else:
        items.append(Item("holidays", CALENDAR, "Holidays this week", State.OK, "No market holiday in the next 7 days."))
    # A month's notice: the exchanges publish next year's list in December,
    # and the desk treats a day it has no calendar for as a plain weekday.
    if today.month >= 11:
        next_year = today.year + 1
        short = [str(e.get("exchange")) for e in body.get("exchanges") or []
                 if isinstance(e, dict) and next_year not in (e.get("yearsLoaded") or [])]
        if short:
            items.append(Item("next-year-holidays", CALENDAR, f"{next_year} holidays", State.WARN,
                              f"{next_year}'s holidays are not loaded for {', '.join(short)}.",
                              "Add them on System → Market calendar from the exchanges' circulars, and in "
                              "src/AlgoTrading.Api/SeedData/market_calendar.json, which the Python engine reads.",
                              MARKET_CALENDAR))
    warnings = _calendar_warnings(body)
    if warnings:
        items.append(Item("calendar-warnings", CALENDAR, "Market calendar", State.WARN, _listed(warnings) + ".",
                          "Fix the calendar on System → Market calendar.", MARKET_CALENDAR))
    return items


# -------------------------------------------------------------------- runner

#: Every check by key: its area and title (for the item when it cannot run) and the function.
CHECKS: dict[str, tuple[str, str, Check]] = {
    "dhan-token": (DATA, "Dhan token", dhan_token),
    "fyers-backup": (DATA, "FYERS backup", fyers_backup),
    "feeds": (DATA, "Live feeds", feeds),
    "failover": (DATA, "Feed failover", failover),
    "plan": (STRATEGIES, "Morning plan", plan),
    "runs-after-close": (STRATEGIES, "Runs after the close", runs_after_close),
    "runs-overnight": (STRATEGIES, "Runs overnight", runs_overnight),
    "day-result": (STRATEGIES, "Today's result", day_result),
    "manual-books": (POSITIONS, "Manual books", manual_books),
    "carried-today": (POSITIONS, "Carried at the close", carried_today),
    "legs-after-close": (POSITIONS, "Strategy legs after the close", strategy_legs_after_close),
    "stale-runs": (POSITIONS, "Runs stuck in Pending", stale_runs),
    "orphaned-legs": (POSITIONS, "Legs in ended runs", orphaned_legs),
    "forecasts-issued": (ANALYSIS, "Forecasts issued", forecasts_issued),
    "forecasts-scored": (ANALYSIS, "Forecasts scored", forecasts_scored),
    "incidents": (DESK, "Open incidents", incidents),
    "incident-notes": (DESK, "Incident notes", incident_notes),
    "deploy": (DESK, "Deploys", deploy),
    "desk-code": (DESK, "Desk supervisor", desk_code),
    "disk": (DESK, "Disk space", disk),
    "archive": (DESK, "Archive to Drive", archive),
    "calendar-today": (CALENDAR, "Today's session", calendar_today),
    "calendar-week": (CALENDAR, "Holidays this week", calendar_week),
}


def run_checks(inp: Inputs, keys: tuple[str, ...]) -> list[Item]:
    """
    Every check in ``keys``, in order. One that cannot be made is an item
    saying so, and the rest still run. An API or a database that does not
    answer is one item at the end, not one per check that needed it.
    """
    items: list[Item] = []
    for key in keys:
        area, title, check = CHECKS[key]
        try:
            items.extend(check(inp))
        except Unavailable as why:
            if why.problem:
                items.append(Item(key, area, title, State.WARN, f"Could not check: {why}.",
                                  "Look at logs/api.log for the error at this time."))
            else:
                items.append(Item(key, area, title, State.SKIP, f"Not checked: {why}."))
        except Exception as exc:
            if type(exc).__module__.split(".")[0] == "psycopg2":
                inp._db_down = inp._db_down or type(exc).__name__
                items.append(Item(key, area, title, State.SKIP, "Not checked: the database is not answering."))
            else:   # a bug in one check must not cost the rest of the report
                items.append(Item(key, area, title, State.WARN, f"Could not check: the check failed ({_short(exc)}).",
                                  "A bug in Sentinel's checkup (sentinel/checkup/checks.py); the rest of the report "
                                  "is unaffected."))
    if inp._api_down:
        skipped = sum(1 for i in items if i.detail == "Not checked: the API is not answering.")
        items.append(Item("api", DESK, "Desk API", State.FAIL,
                          f"The API did not answer ({inp._api_down}), so {_n(skipped, 'check')} could not be made.",
                          "Sentinel's health agent opens an incident for it (System → Incidents); the desk restarts "
                          "the API on its own. If it stays down: logs/api.log and logs/desk.log.", INCIDENTS))
    if inp._db_down:
        skipped = sum(1 for i in items if i.detail == "Not checked: the database is not answering.")
        items.append(Item("database", DESK, "Database", State.FAIL,
                          f"The database did not answer ({inp._db_down}), so {_n(skipped, 'check')} could not be made.",
                          "On the server: docker ps should show algotrading_db healthy; Sentinel's health agent "
                          "opens an incident for it."))
    return items
