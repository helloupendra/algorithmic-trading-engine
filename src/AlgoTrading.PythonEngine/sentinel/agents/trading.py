"""
The trading agent: is every run the morning plan asked for alive, and is
anything the runs are doing out of the ordinary?

Everything it knows comes from two admin GETs a minute — the runs that are
running now, and every run started today (IST) — plus config/morning-plan.txt,
the same file scripts/market-open.sh deploys from, and, at most once a day, the
last few trading days' runs to know what a strategy's ordinary day looks like.

Rules, each one of this desk's own days:

``run-missing`` (HIGH)
    A run the plan asked for (account x strategy x underlying) that is not
    running between 09:25 and 15:25 IST while its market is open. 09:25,
    because the morning job deploys from 09:16 and a fallback to FYERS pushes
    the last start towards 09:23; 15:25, because the desk stops every run at
    15:30 — CRUDEOIL ones included — and the close must not read as 26 deaths.
    Not reported when the key's last run today was ended on purpose (its day
    target or stop-loss, a person's stop): nobody wants to hear that a run they
    stopped is stopped. One run missing is one incident for that run; two or
    more are one incident for the account ("3 of 13 planned runs for admin are
    not running"), and it stays that one incident while the runs come back, so
    a recovery reads as one message when it is done, not a burst of new ones.
    While an account's newest run started less than three minutes ago the
    morning job (or a person) is still working through the plan: nothing new
    is called missing, and what was already reported stays reported. Never on
    a Saturday or Sunday: the morning job runs Monday to Friday, so a special
    weekend session in the exchange calendar is not one the plan was deployed
    for.

``run-stopped-early`` (HIGH)
    A run started today that ended inside market hours for a reason nobody
    chose — "Runner exited (code 1): … 429 … /api/UserAuth/login" (24 Sep),
    "Runner failed to start.", "API restarted; runner not found", or no reason
    at all. Open while that run is the latest of its account/strategy/
    underlying and its market is still trading; it resolves when someone starts
    the run again (a newer run supersedes it) or at the close. A run id, once
    finished with, is kept in the agent's state for the day and never reported
    again. A planned run that died this way is reported here, with its reason,
    and not a second time as run-missing. A run that died before 15:30 is let
    go at 15:30, when the desk stops every run anyway; an MCX run started by
    hand for the evening is watched until the MCX close.

    Three or more runs of one account dying of one cause within five minutes
    of each other are one incident, not one per run: on 24 Sep the sign-in
    limiter killed 13 coderforchange runs and 3 admin runs in half a minute,
    and the useful message is "13 coderforchange runs died at 09:18: sign-in
    limiter (429)", with the fix — run scripts/market-open.sh again — not
    thirteen "restart it from the Live runner". A run once in such a group
    stays in it until it is restarted. Fewer than three are a run each; a pair
    that died less than a minute ago waits one check, in case it is the start
    of a larger one.

``run-duplicated`` (CRITICAL)
    More than one Running run for the same (userId, strategy, underlying): two
    runners on one book double every order. The risk on this desk is the
    morning job fired more than once (desk.sh launched 3–4 times within seconds
    on 24 and 26 Sep). Two accounts running the same thing are not duplicates.

``overtrading`` (MEDIUM)
    A strategy whose closed trades today on one account/underlying are past
    what is ordinary for it: three times its median trades a day over its last
    five trading days (never below 30), or 150 when it has no history. Fulcrum
    makes 270–1,000 a day by design and is judged against that; Ghost makes
    6–13 and ChainFlowBuy 1–3, so a Ghost at 60 is news. One incident per
    strategy per day, however many books it churns in, and only while those
    books' market is open — so it closes at the close, not at midnight.
    ``SENTINEL_MAX_TRADES_<STRATEGY>=N`` (name upper-cased, anything not a
    letter or digit as ``_``) sets a strategy's own limit over its history;
    ``SENTINEL_MAX_TRADES`` replaces 150 as the fallback; 0 in either turns the
    check off (for that strategy, or for every strategy without its own).

``account-loss`` (HIGH)
    The day's P&L of an account — booked on today's runs plus what their open
    positions are marked at — below ``-SENTINEL_MAX_DAY_LOSS`` (₹50,000), while
    the account is still trading. Each run's own day stop-loss guards that
    run; nothing guards the account.

Quiet by default: nothing is checked on a non-trading day. When the API does
not answer, "the API is down" is the health agent's to say; this agent repeats
what it last saw (for up to 15 minutes, the same day), because answering
"nothing" would close every open trading incident and reopen it as new the
moment the API came back. Alerter runs (a ``role``) and the manual order book
are not strategy runs and are left out of every rule but the account's P&L.
"""
from __future__ import annotations

import copy
import math
import re
import statistics
from collections import defaultdict
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta, timezone
from typing import Any, Optional

from sentinel.agents.base import Agent
from sentinel.clock import ist_date, to_ist
from sentinel.context import MorningPlan, SentinelContext
from sentinel.model import Finding, Severity
from sentinel.notify import redact

RUNNING_PATH = "/api/Strategy/runs?status=Running&take=500"


def today_path(day: str) -> str:
    """Every live run started on one IST day, newest first."""
    return f"/api/Strategy/runs?fromDate={day}&take=500"


def history_path(first_day: str, last_day: str) -> str:
    """Every live run started between two IST days, both included."""
    return f"/api/Strategy/runs?fromDate={first_day}&toDate={last_day}&take=500"


NSE_HOURS = (time(9, 15), time(15, 30))
MCX_HOURS = (time(9, 0), time(23, 30))

# The window the plan is held to: the morning job's starts are done by 09:25,
# and the desk's own 15:30 stop of every run must not read as missing runs.
PLAN_WINDOW = (time(9, 25), time(15, 25))

# MarketHoursService stops every strategy run at 15:30 IST, MCX ones too.
DESK_STOPS_RUNS_AT = time(15, 30)

# An account whose newest run started this recently is still being deployed.
DEPLOY_QUIET = timedelta(minutes=3)

# Deaths: this many runs of one account, one cause, this close together, are one incident.
MASS_DEATH_RUNS = 3
MASS_DEATH_GAP = timedelta(minutes=5)
# A smaller cluster younger than this waits a check: it may be the start of a mass death.
DEATH_SETTLE = timedelta(seconds=60)

# When the API does not answer, the last findings stand in for this long.
CARRY_FOR = timedelta(minutes=15)

DEFAULT_MAX_TRADES = 150
DEFAULT_MAX_DAY_LOSS = 50_000.0

# A strategy's ordinary day: its median trades per book-day over this many trading days.
HISTORY_TRADING_DAYS = 5
HISTORY_LOOKBACK = timedelta(days=9)   # calendar days asked for, to cover five trading days
HISTORY_MIN_SAMPLES = 3                # book-days needed before a history is believed
HISTORY_MULTIPLE = 3
HISTORY_FLOOR = 30                     # a strategy that trades twice a day is not overtrading at 7
HISTORY_RETRY = timedelta(minutes=10)

# Commodity underlyings. A run's spot symbol ("MCX:CRUDEOIL26OCTFUT") is the
# better witness and is used whenever one is at hand; this list covers a plan
# line that has no run today to ask.
MCX_UNDERLYINGS = frozenset({
    "CRUDEOIL", "CRUDEOILM", "NATURALGAS", "NATGASMINI", "GOLD", "GOLDM", "GOLDMINI", "GOLDPETAL",
    "GOLDGUINEA", "GOLDTEN", "SILVER", "SILVERM", "SILVERMIC", "SILVERMINI", "COPPER", "ZINC",
    "ZINCMINI", "LEAD", "LEADMINI", "ALUMINIUM", "ALUMINI", "NICKEL", "MENTHAOIL", "COTTON",
    "COTTONCNDY", "CASTORSEED", "KAPAS",
})

# Who ends a run on purpose without being a person.
_DELIBERATE_BY = frozenset({"market-hours", "risk-guard"})
# Who ends a run because something went wrong.
_MACHINE_BY = frozenset({"runner", "api"})
_ABNORMAL_REASONS = ("runner exited", "runner failed", "api restarted")
_NORMAL_REASONS = ("market closed", "mcx closed", "target hit", "stop loss hit", "trailing stop hit", "stopped by ")

_CAUSE_LABELS = {
    "429-login": "sign-in limiter (429)",
    "429": "rate limited (429)",
    "no-reason": "no reason recorded",
    "failed-to-start": "runner failed to start",
    "runner-failed-to-start": "runner failed to start",
    "runner-exited": "runner exited",
    "api-restarted": "lost in an API restart",
}

_RERUN = ("run scripts/market-open.sh again: it starts only the planned runs that are not running and leaves the "
          "live ones alone (it restarts the feeds first, so those see a short gap in ticks)")

_URL_QUERY = re.compile(r"(https?://[^\s?#]+)[?#]\S*")
# What notify.redact does not catch in free text from a runner's stderr.
_MORE_SECRETS = [
    re.compile(r"(?i)\b([a-z][a-z0-9+.\-]*://)[^\s/@:]+:[^\s/@]+@"),              # scheme://user:password@host
    re.compile(r"(?i)\b(bot)\d{6,}:[A-Za-z0-9_\-]{20,}"),                          # a Telegram bot token in a URL
    re.compile(r"(?i)\b(authorization\s*[=:]\s*)(?:basic|bearer)?\s*[^\s;,'\"]+"),
    re.compile(r"(?i)\b([\w\-]*(?:secret|token|passw|pwd|api[_\-]?key|auth[_\-]?key|credential)[\w\-]*\s*[=:]\s*)"
               r"[\"']?[^\s;&,'\"]+"),                                              # secretKey=…, Password=…;
]


@dataclass(frozen=True)
class _Run:
    run_id: int
    user_id: str
    user: str
    strategy: str
    underlying: str
    status: str
    active: bool
    started: Optional[datetime]
    stopped: Optional[datetime]
    reason: str
    by: str
    trades: int
    open_positions: int
    net: float
    unrealized: float
    role: Optional[str]
    spot: str

    @property
    def key(self) -> tuple[str, str, str]:
        """What the plan names: account, strategy, underlying."""
        return self.user.lower(), self.strategy.lower(), self.underlying.upper()

    @property
    def live(self) -> bool:
        """Running with a runner behind it ("Stopping" still counts for a moment)."""
        return self.status in ("Running", "Stopping") and self.active

    @property
    def not_a_strategy(self) -> bool:
        """An alerter's run, or the manual order book: nothing a strategy rule applies to."""
        return bool(self.role) or self.strategy.lower() == "manual"

    @property
    def exchange(self) -> str:
        if self.spot.upper().startswith("MCX:") or self.underlying.upper() in MCX_UNDERLYINGS:
            return "MCX"
        return "NSE"

    @property
    def ended(self) -> bool:
        return self.status in ("Stopped", "Failed")

    @property
    def when(self) -> Optional[datetime]:
        """When it ended — or, for a run that never got going, when it was started."""
        return self.stopped or self.started


def _parse_time(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value.strip():
        return None
    try:
        moment = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def _num(value: Any) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return 0.0
    return number if math.isfinite(number) else 0.0


def _parse_runs(body: Any) -> list[_Run]:
    runs: list[_Run] = []
    for row in body if isinstance(body, list) else []:
        if not isinstance(row, dict) or not isinstance(row.get("runId"), int) or isinstance(row.get("runId"), bool):
            continue
        user_id = str(row.get("userId") or "")
        user = str(row.get("userName") or "").strip() or f"user {user_id or '?'}"
        runs.append(_Run(
            run_id=row["runId"],
            user_id=user_id,
            user=user,
            strategy=str(row.get("strategyName") or "").strip(),
            underlying=str(row.get("underlying") or "").strip().upper(),
            status=str(row.get("status") or "").strip(),
            active=row.get("isActive") is not False,
            started=_parse_time(row.get("startedUtc")),
            stopped=_parse_time(row.get("stoppedUtc")),
            reason=str(row.get("stopReason") or "").strip(),
            by=str(row.get("stoppedBy") or "").strip(),
            trades=int(_num(row.get("trades"))),
            open_positions=int(_num(row.get("openPositions"))),
            net=_num(row.get("netPnl")),
            unrealized=_num(row.get("unrealizedPnl")),
            role=(str(row["role"]).strip() or None) if row.get("role") else None,
            spot=str(row.get("spotSymbol") or ""),
        ))
    return runs


def ended_on_purpose(run: _Run) -> bool:
    """
    Whether a finished run was ended by someone or something that meant to:
    the market-hours stop at the close, the risk guard on a target, stop-loss
    or trailing stop, or a person ("Stopped by admin", even of a run whose
    runner was already gone). Everything else — the runner exiting on its own,
    failing to start, being lost across an API restart, or no reason recorded
    at all — is not.
    """
    if run.status == "Failed":
        return False
    reason = run.reason.lower()
    by = run.by.lower()
    if not reason and not by:
        return False
    if reason.startswith(_ABNORMAL_REASONS):
        return False
    if by in _DELIBERATE_BY:
        return True
    if by and by not in _MACHINE_BY:
        return True  # a person's name
    return reason.startswith(_NORMAL_REASONS)


def _clean(text: str, limit: int = 160) -> str:
    """Free text from the API made safe to show: one line, no query strings, no secrets, short."""
    text = " ".join(str(text).split())
    text = _URL_QUERY.sub(r"\1?…", text)
    for pattern in _MORE_SECRETS:
        text = pattern.sub(lambda m: m.group(1) + "…", text)
    text = redact(text)
    return text if len(text) <= limit else text[: limit - 1] + "…"


def _short_reason(reason: str) -> str:
    """The head of a stop reason, cut where the API's detail starts: "Runner exited (code 1): …" -> "Runner exited"."""
    head = reason.strip()
    for separator in (":", " (", ";", " — "):
        at = head.find(separator)
        if at > 0:
            head = head[:at]
    return _clean(head, 60) if head else "no reason was recorded"


def _cause(run: _Run) -> str:
    """A death's cause as a slug for grouping: '429-login', 'runner-exited', 'api-restarted', …"""
    reason = run.reason.lower()
    if "429" in reason and "userauth/login" in reason:
        return "429-login"
    if "429" in reason or "too many requests" in reason:
        return "429"
    if not reason:
        return "failed-to-start" if run.status == "Failed" else "no-reason"
    slug = re.sub(r"[^a-z0-9]+", "-", _short_reason(run.reason).lower()).strip("-")
    return slug[:40] or "unknown"


def _slug(text: str) -> str:
    return re.sub(r"\s+", "_", text.strip().lower())[:60] or "?"


def _hms(moment: Optional[datetime]) -> str:
    return to_ist(moment).strftime("%H:%M:%S") if moment else "?"


def _hm(moment: Optional[datetime]) -> str:
    return to_ist(moment).strftime("%H:%M") if moment else "?"


def _rupees(amount: float) -> str:
    sign = "-" if amount < 0 else ""
    return f"{sign}₹{abs(amount):,.0f}"


def _plural(n: int, one: str, many: str) -> str:
    return one if n == 1 else many


def _within(moment: Optional[datetime], hours: tuple[time, time], day: str) -> bool:
    if moment is None:
        return False
    ist = to_ist(moment)
    return ist.strftime("%Y-%m-%d") == day and hours[0] <= ist.time() < hours[1]


def _run_trading(session, now: datetime, run: _Run) -> bool:
    """Whether a run's book is still trading: it is live, or its market is open and the desk has not stopped runs."""
    if run.live:
        return True
    market_open = session.mcx_open if run.exchange == "MCX" else session.nse_open
    return bool(market_open) and to_ist(now).time() < DESK_STOPS_RUNS_AT


def _env_name(strategy: str) -> str:
    return re.sub(r"[^A-Z0-9]", "_", strategy.upper())


def _env_int(env: dict[str, str], key: str) -> Optional[int]:
    raw = (env.get(key) or "").strip()
    if not raw:
        return None
    try:
        return int(raw)
    except ValueError:
        return None


def day_loss_limit(env: dict[str, str]) -> tuple[Optional[float], str]:
    """The day-loss line and where it came from; None when the check is off."""
    raw = (env.get("SENTINEL_MAX_DAY_LOSS") or "").strip()
    if not raw:
        return DEFAULT_MAX_DAY_LOSS, "the default"
    try:
        limit = float(raw)
    except ValueError:
        return DEFAULT_MAX_DAY_LOSS, "the default"
    return (limit if limit > 0 else None), "SENTINEL_MAX_DAY_LOSS"


def _ids(value: Any) -> set[int]:
    if not isinstance(value, list):
        return set()
    return {x for x in value if isinstance(x, int) and not isinstance(x, bool)}


def _dump(f: Finding) -> dict:
    return {"rule": f.rule, "severity": f.severity.value, "title": f.title, "summary": f.summary,
            "fingerprint": f.fingerprint, "where": f.where, "evidence": list(f.evidence), "suggestion": f.suggestion}


def _load(d: Any, agent: str) -> Optional[Finding]:
    if not isinstance(d, dict):
        return None
    try:
        evidence = d.get("evidence") if isinstance(d.get("evidence"), list) else []
        return Finding(agent=agent, rule=str(d["rule"]), severity=Severity(d["severity"]), title=str(d["title"]),
                       summary=str(d.get("summary") or ""), fingerprint=str(d["fingerprint"]),
                       where=str(d.get("where") or ""), evidence=[str(e) for e in evidence],
                       suggestion=str(d.get("suggestion") or ""))
    except (KeyError, ValueError, TypeError):
        return None


def _clusters(runs: list[_Run]) -> list[list[_Run]]:
    """Deaths sorted by time, split wherever two are more than MASS_DEATH_GAP apart."""
    out: list[list[_Run]] = []
    for run in runs:
        if out and run.when - out[-1][-1].when <= MASS_DEATH_GAP:
            out[-1].append(run)
        else:
            out.append([run])
    return out


def _run_lines(runs: list[_Run], lines: int) -> list[str]:
    """Runs listed compactly by strategy: 'GhostTangentCrossings: 218 BANKNIFTY, 219 NIFTY'."""
    by_strategy: dict[str, list[str]] = {}
    for run in sorted(runs, key=lambda r: r.run_id):
        by_strategy.setdefault(run.strategy, []).append(f"{run.run_id} {run.underlying}")
    out = [f"{strategy}: {', '.join(items)}" for strategy, items in by_strategy.items()]
    if len(out) > lines:
        rest = [item for items in list(by_strategy.values())[lines - 1:] for item in items]
        out = out[: lines - 1] + [f"… and {len(rest)} more: " + ", ".join(rest[:6]) + (" …" if len(rest) > 6 else "")]
    return out


class TradingAgent(Agent):
    name = "trading"
    interval_seconds = 60

    def check(self, ctx: SentinelContext) -> list[Finding]:
        session = ctx.session()
        if not session.trading_day:
            return []

        now = ctx.now()
        day = ist_date(now)
        state = ctx.state(self.name)
        raw = state.data
        data = copy.deepcopy(raw) if isinstance(raw, dict) and raw.get("day") == day else {"day": day}

        try:
            running_body = ctx.api_get(RUNNING_PATH)
            today_body = ctx.api_get(today_path(day))
            answered = isinstance(running_body, list) and isinstance(today_body, list)
        except Exception:
            answered = False
        if not answered:
            # The health agent owns "the API is not answering". Saying "nothing" here
            # would close every open trading incident and reopen it the minute the
            # API is back — so, for a while, what was true a minute ago stands.
            return self._carry(data, now)

        running = _parse_runs(running_body)
        today = [r for r in _parse_runs(today_body) if r.started is None or ist_date(r.started) == day]

        latest: dict[tuple[str, str, str], _Run] = {}
        for run in sorted(today, key=lambda r: (r.started or datetime.min.replace(tzinfo=timezone.utc), r.run_id)):
            latest[run.key] = run
        # Live in either list: a run started between the two GETs is in the second only.
        live_keys = {r.key for r in running if r.live} | {r.key for r in today if r.live}
        plan = ctx.plan()

        deaths = self._deaths(session, now, day, today, latest, live_keys, _ids(data.get("done")))
        findings: list[Finding] = []
        findings += self._stopped_early(ctx, now, deaths, data, plan)
        findings += self._missing(ctx, session, now, day, running, today, latest, live_keys, set(deaths), data, plan)
        findings += self._duplicated(running, day)
        findings += self._overtrading(ctx, session, now, day, today, data)
        findings += self._account_loss(ctx.env, session, now, day, today)

        data["last"] = {"at": now.isoformat(), "findings": [_dump(f) for f in findings]}
        if data != raw:
            state.data = data
            state.save()
        return findings

    def _carry(self, data: dict, now: datetime) -> list[Finding]:
        """The last check's findings, when they are from today and recent enough to still be believed."""
        last = data.get("last")
        if not isinstance(last, dict) or not isinstance(last.get("findings"), list):
            return []
        at = _parse_time(last.get("at"))
        if at is None or not (timedelta(0) <= now - at <= CARRY_FOR):
            return []
        return [f for f in (_load(d, self.name) for d in last["findings"]) if f is not None]

    # ------------------------------------------------------------------
    # run-stopped-early
    # ------------------------------------------------------------------

    @staticmethod
    def _deaths(session, now: datetime, day: str, today: list[_Run], latest: dict, live_keys: set,
                done: set[int]) -> dict[int, _Run]:
        """Every run that ended in the session for a reason nobody chose and has not been started again."""
        after_desk_stop = to_ist(now).time() >= DESK_STOPS_RUNS_AT
        deaths: dict[int, _Run] = {}
        for run in today:
            if run.not_a_strategy or run.run_id in done or not run.ended or ended_on_purpose(run):
                continue
            hours, market_open = (MCX_HOURS, session.mcx_open) if run.exchange == "MCX" else (NSE_HOURS, session.nse_open)
            if not _within(run.when, hours, day):
                continue  # ended before the open or after the close: not a death in the session
            if latest.get(run.key) is not run or run.key in live_keys:
                continue  # someone has started it again
            if not market_open:
                continue  # nothing left to trade today
            if after_desk_stop and to_ist(run.when).time() < DESK_STOPS_RUNS_AT:
                continue  # the desk stops every run at 15:30: this one would be stopped by now anyway
            deaths[run.run_id] = run
        return deaths

    def _stopped_early(self, ctx: SentinelContext, now: datetime, deaths: dict[int, _Run], data: dict,
                       plan: Optional[MorningPlan]) -> list[Finding]:
        was_open = _ids(data.get("open"))
        done = _ids(data.get("done"))
        remembered = data.get("groups") if isinstance(data.get("groups"), dict) else {}

        # A mass death already reported keeps its members until each is started again.
        groups: dict[str, dict] = {}
        ever_grouped: set[int] = set()
        for fp, g in remembered.items():
            if not isinstance(fp, str) or not isinstance(g, dict):
                continue
            members = _ids(g.get("ids"))
            ever_grouped |= members
            alive = sorted(i for i in members if i in deaths)
            if not alive:
                continue
            first_run = min((deaths[i] for i in alive), key=lambda r: r.when)
            last_run = max((deaths[i] for i in alive), key=lambda r: r.when)
            size = g.get("size") if isinstance(g.get("size"), int) and not isinstance(g.get("size"), bool) else 0
            groups[fp] = {
                "account": str(g.get("account") or _slug(first_run.user)),
                "cause": str(g.get("cause") or _cause(first_run)),
                "size": max(size, len(alive)),
                "first": g["first"] if _parse_time(g.get("first")) else first_run.when.isoformat(),
                "last": g["last"] if _parse_time(g.get("last")) else last_run.when.isoformat(),
                "ids": alive,
            }

        solo = {i for i in was_open if i in deaths and i not in ever_grouped}
        fresh = sorted((r for i, r in deaths.items() if i not in was_open and i not in ever_grouped),
                       key=lambda r: (r.when, r.run_id))

        # A fresh death joins its account's open group when it is the same cause, close in time.
        pending: dict[tuple[str, str], list[_Run]] = defaultdict(list)
        for run in fresh:
            account, cause = _slug(run.user), _cause(run)
            for g in groups.values():
                if g["account"] == account and g["cause"] == cause \
                        and abs(run.when - max(deaths[i].when for i in g["ids"])) <= MASS_DEATH_GAP:
                    g["ids"] = sorted({*g["ids"], run.run_id})
                    g["size"] += 1
                    g["last"] = max(_parse_time(g["last"]), run.when).isoformat()
                    break
            else:
                pending[(account, cause)].append(run)

        for (account, cause), runs in pending.items():
            for cluster in _clusters(runs):
                if len(cluster) >= MASS_DEATH_RUNS:
                    fp = f"trading:run-stopped-early:{account}:{cause}"
                    if fp in groups:  # a second, separate mass death of the same cause today
                        fp = f"{fp}:{to_ist(cluster[0].when):%H%M}"
                    groups[fp] = {"account": account, "cause": cause, "size": len(cluster),
                                  "first": cluster[0].when.isoformat(), "last": cluster[-1].when.isoformat(),
                                  "ids": sorted(r.run_id for r in cluster)}
                elif now - cluster[-1].when < DEATH_SETTLE:
                    continue  # it may be the start of a mass death: the next check decides
                else:
                    solo |= {r.run_id for r in cluster}

        plan_keys = {(a.lower(), s.lower(), u.upper()) for a, s, u in plan.expected_runs()} if plan else set()
        findings = [self._stopped_early_finding(ctx, deaths[i]) for i in sorted(solo)]
        for fp, g in groups.items():
            findings.append(self._mass_death_finding(fp, g, [deaths[i] for i in g["ids"]], plan_keys))

        # A run reported before and not now has been dealt with (restarted, or the
        # session is over): it stays finished for the rest of the day.
        reported = solo | {i for g in groups.values() for i in g["ids"]}
        data["open"] = sorted(reported)
        data["done"] = sorted(done | (was_open - reported))
        data["groups"] = groups
        return findings

    def _stopped_early_finding(self, ctx: SentinelContext, run: _Run) -> Finding:
        when = run.when
        reason = _clean(run.reason) if run.reason else "no reason was recorded"
        evidence = [
            f"run {run.run_id}: {run.status}, started {_hms(run.started)} IST, ended {_hms(when)} IST",
            f"reason: {reason}",
            f"stopped by: {run.by or 'nobody recorded'}",
        ]
        if run.trades or run.net:
            evidence.append(f"{run.trades} trade(s) before it ended, net {_rupees(run.net)}")
        return Finding(
            agent=self.name,
            rule="run-stopped-early",
            severity=Severity.HIGH,
            title=(f"{run.strategy} on {run.underlying} for {run.user} "
                   + (f"failed to start at {_hm(when)}" if run.status == "Failed" else f"stopped unexpectedly at {_hm(when)}")),
            summary=(f"Run {run.run_id} ({run.strategy} on {run.underlying}, {run.user}) ended at {_hms(when)} IST "
                     f"while the market was open, and nobody chose to end it ({_short_reason(run.reason)}). "
                     "It is not running now, so that book is taking no trades."),
            fingerprint=f"trading:run-stopped-early:{run.run_id}",
            where=f"run {run.run_id} · {run.user} / {run.strategy} / {run.underlying}",
            evidence=evidence,
            suggestion=self._death_advice(run, self._log_hint(ctx, run.run_id)),
        )

    @staticmethod
    def _death_advice(run: _Run, log_path: str) -> str:
        reason = run.reason.lower()
        when = _hm(run.when)
        if "429" in reason and "userauth/login" in reason:
            return ("The runner could not sign in: the API's sign-in limiter answered 429 Too Many Requests, as it "
                    "did to the whole morning start on 24 Sep. Restart it from the Live runner once a minute has "
                    "passed.")
        if "429" in reason or "too many requests" in reason:
            return (f"A rate limit killed the runner (429 Too Many Requests). Read {log_path} for which service "
                    "refused it, wait out the limit, then restart it from the Live runner.")
        if run.status == "Failed" or reason.startswith("runner failed"):
            return (f"The API could not launch the runner. Read logs/api.log (or the api-until-*.log it was "
                    f"rotated to) around {when} IST for the launch error, then start it again from the Live runner.")
        if reason.startswith("api restarted") or "after api restart" in reason:
            return (f"The API restarted around {when} IST and lost this runner. Check logs/desk.log for a deploy "
                    "or restart at that time, then start the run again from the Live runner.")
        if not reason:
            return (f"The run ended without a recorded reason — the API lost track of it. Read {log_path} "
                    f"around {when} IST, then start it again from the Live runner.")
        return f"Read {log_path} for the traceback, then start it again from the Live runner."

    def _mass_death_finding(self, fp: str, g: dict, members: list[_Run], plan_keys: set) -> Finding:
        first_run = min(members, key=lambda r: (r.when, r.run_id))
        first = _parse_time(g["first"]) or first_run.when
        last = max(_parse_time(g["last"]) or first, first)
        span = _hms(first) if _hms(first) == _hms(last) else f"{_hms(first)}–{_hms(last)}"
        account, size, still = first_run.user, g["size"], len(members)
        label = _CAUSE_LABELS.get(g["cause"]) or _short_reason(first_run.reason)
        verb = "failed to start" if all(r.status == "Failed" for r in members) else "died"
        reason = _clean(first_run.reason) if first_run.reason else "no reason was recorded"
        evidence = [f"reason: {reason} (stopped by: {first_run.by or 'nobody recorded'})"]
        evidence += _run_lines(members, 5)
        unplanned = sorted((r for r in members if r.key not in plan_keys), key=lambda r: r.run_id)
        return Finding(
            agent=self.name,
            rule="run-stopped-early",
            severity=Severity.HIGH,
            title=f"{size} {account} runs {verb} at {_hm(first)}: {label}",
            summary=(f"{size} of {account}'s runs ended at {span} IST while the market was open, all with one "
                     f"cause: {label}. {still} of them {_plural(still, 'is', 'are')} still not running, so "
                     f"{_plural(still, 'that book takes', 'those books take')} no trades."),
            fingerprint=fp,
            where=f"Live runner · {account}",
            evidence=evidence[:6],
            suggestion=self._mass_death_advice(g["cause"], first, unplanned),
        )

    @staticmethod
    def _mass_death_advice(cause: str, first: datetime, unplanned: list[_Run]) -> str:
        when = _hm(first)
        if cause == "429-login":
            advice = ("The runners could not sign in: the API's sign-in limiter answered 429 Too Many Requests, as it "
                      f"did to the whole morning start on 24 Sep. Wait a minute for the limiter to clear, then {_RERUN}.")
        elif cause == "429":
            advice = (f"A rate limit killed them together (429 Too Many Requests). Read logs/api.log around {when} IST "
                      f"for which service refused them, wait it out, then {_RERUN}.")
        elif cause.startswith("api-restarted"):
            advice = (f"The API restarted around {when} IST and lost these runners. Check logs/desk.log for a deploy "
                      f"or restart at that time, then {_RERUN}.")
        elif "failed-to-start" in cause:
            advice = (f"The API could not launch these runners. Read logs/api.log (or the api-until-*.log it was "
                      f"rotated to) around {when} IST for the launch error; once it is fixed, {_RERUN}.")
        else:
            advice = (f"They ended together, so look for one cause: logs/api.log around {when} IST has each runner's "
                      f"last error (\"exited on its own\"). Once it is fixed, {_RERUN}.")
        if unplanned:
            advice += (" " + ", ".join(f"Run {r.run_id}" if i == 0 else f"run {r.run_id}" for i, r in enumerate(unplanned[:5]))
                       + (" …" if len(unplanned) > 5 else "")
                       + " — not in the morning plan — must be started again from the Live runner.")
        return advice

    @staticmethod
    def _log_hint(ctx: SentinelContext, run_id: int) -> str:
        """Where a runner's last words are: its own log when the API kept one, otherwise the API's log."""
        try:
            hits = sorted((ctx.logs_dir / "engine").glob(f"runner-{run_id}-*.log"))
        except OSError:
            hits = []
        if hits:
            return f"logs/engine/{hits[-1].name}"
        return (f"logs/api.log (search \"run {run_id} on\"; after an API restart it is in the api-until-*.log it "
                "was rotated to)")

    # ------------------------------------------------------------------
    # run-missing
    # ------------------------------------------------------------------

    def _missing(self, ctx: SentinelContext, session, now: datetime, day: str, running: list[_Run],
                 today: list[_Run], latest: dict, live_keys: set, dead_ids: set[int], data: dict,
                 plan: Optional[MorningPlan]) -> list[Finding]:
        previous = data.get("missing") if isinstance(data.get("missing"), dict) else {}
        data["missing"] = {}
        t = to_ist(now).time()
        if not (PLAN_WINDOW[0] <= t < PLAN_WINDOW[1]) or plan is None or to_ist(now).weekday() >= 5:
            return []

        mcx_seen = {r.underlying for r in [*running, *today] if r.spot.upper().startswith("MCX:")}
        orphans = {r.key: r for r in running if not r.active and r.status in ("Running", "Stopping")}

        newest: dict[str, datetime] = {}
        for r in (*running, *today):
            if r.started is not None and not r.not_a_strategy:
                account = r.user.lower()
                if account not in newest or r.started > newest[account]:
                    newest[account] = r.started

        expected: dict[str, int] = defaultdict(int)
        live_count: dict[str, int] = defaultdict(int)
        missing: dict[str, list[tuple[str, str, Optional[_Run], Optional[_Run]]]] = defaultdict(list)
        for account, strategy, underlying in plan.expected_runs():
            u = underlying.upper()
            is_mcx = u in MCX_UNDERLYINGS or u in mcx_seen
            if not (session.mcx_open if is_mcx else session.nse_open):
                continue
            expected[account] += 1
            key = (account.lower(), strategy.lower(), u)
            if key in live_keys:
                live_count[account] += 1
                continue
            last = latest.get(key)
            if last is not None and last.run_id in dead_ids:
                continue  # run-stopped-early has it, with its reason
            if last is not None and last.ended and ended_on_purpose(last):
                continue  # its day target or stop-loss, or a person, ended it
            missing[account].append((strategy, u, last, orphans.get(key)))

        findings: list[Finding] = []
        remembered: dict[str, dict] = {}
        for account, rows in missing.items():
            acct = account.lower()
            before = previous.get(acct) if isinstance(previous.get(acct), dict) else {}
            before_keys = {k for k in before.get("keys", []) if isinstance(k, str)} \
                if isinstance(before.get("keys"), list) else set()
            started = newest.get(acct)
            if started is not None and now - started < DEPLOY_QUIET:
                # Runs are still being started into this account: call nothing new missing yet.
                rows = [row for row in rows if f"{row[0].lower()}:{row[1]}" in before_keys]
                if not rows:
                    continue
            rollup = len(rows) >= 2 or before.get("mode") == "account"
            if rollup:
                findings.append(self._account_missing_finding(account, day, now, rows, live_count[account],
                                                              expected[account]))
            else:
                strategy, underlying, last, orphan = rows[0]
                findings.append(self._run_missing_finding(ctx, account, strategy, underlying, day, now, last, orphan,
                                                          live_count[account], expected[account]))
            remembered[acct] = {"mode": "account" if rollup else "key",
                                "keys": sorted(f"{s.lower()}:{u}" for s, u, _, _ in rows)}
        data["missing"] = remembered
        return findings

    def _run_missing_finding(self, ctx: SentinelContext, account: str, strategy: str, underlying: str, day: str,
                             now: datetime, last: Optional[_Run], orphan: Optional[_Run], live: int,
                             expected: int) -> Finding:
        evidence: list[str] = []
        if orphan is not None:
            evidence.append(f"run {orphan.run_id} is listed as {orphan.status} but has no runner process "
                            f"(started {_hms(orphan.started)} IST)")
            what = f"run {orphan.run_id} is listed as running but its runner is gone"
            suggestion = (f"Read {self._log_hint(ctx, orphan.run_id)} for how the runner ended, stop run "
                          f"{orphan.run_id} from the Live runner (that closes its row), then start it again.")
        elif last is not None:
            ended = f", ended {_hms(last.stopped)} IST" if last.stopped else ""
            evidence.append(f"last run today: {last.run_id}, {last.status}, started {_hms(last.started)} IST{ended}")
            if last.reason or last.by:
                evidence.append(f"reason: {_clean(last.reason) or 'none recorded'} (stopped by {last.by or 'nobody recorded'})")
            what = f"its last run today, {last.run_id}, is {last.status}"
            suggestion = (f"Read {self._log_hint(ctx, last.run_id)} for why run {last.run_id} ended, then "
                          "start it again from the Live runner.")
        else:
            evidence.append(f"no run of {strategy} on {underlying} was started today for {account}")
            what = "no run of it was started today"
            suggestion = (f"The morning job did not start it: read logs/market-open-{day}.log (it skips a strategy "
                          "missing from the catalogue and an account that is not active). Start it from the Live runner.")
        evidence.append(f"{live} of {expected} planned runs for {account} are live")
        return Finding(
            agent=self.name,
            rule="run-missing",
            severity=Severity.HIGH,
            title=f"{strategy} on {underlying} is not running for {account}",
            summary=(f"The morning plan runs {strategy} on {underlying} for {account}, but at {_hm(now)} IST it is "
                     f"not running: {what}."),
            fingerprint=f"trading:run-missing:{_slug(account)}:{strategy.lower()}:{underlying.upper()}",
            where=f"Live runner · {account} / {strategy} / {underlying}",
            evidence=evidence,
            suggestion=suggestion,
        )

    def _account_missing_finding(self, account: str, day: str, now: datetime,
                                 rows: list[tuple[str, str, Optional[_Run], Optional[_Run]]], live: int,
                                 expected: int) -> Finding:
        never = [f"{s} {u}" for s, u, last, orphan in rows if last is None and orphan is None]
        orphaned = [(s, u, orphan) for s, u, _, orphan in rows if orphan is not None]
        ended = [(s, u, last) for s, u, last, orphan in rows if last is not None and orphan is None]
        n = len(rows)
        everything = live == 0 and n == expected

        evidence: list[str] = []
        if never:
            evidence.append(f"{len(never)} of {n} were never started today: " + ", ".join(never[:8])
                            + (" …" if len(never) > 8 else ""))
        if ended:
            evidence.append("ended and not started again: " + ", ".join(
                f"{s} {u} (run {last.run_id}, {_short_reason(last.reason) if last.reason else last.status})"
                for s, u, last in ended[:4]) + (" …" if len(ended) > 4 else ""))
        if orphaned:
            evidence.append("listed as running with no runner process: " + ", ".join(
                f"{s} {u} (run {o.run_id})" for s, u, o in orphaned[:4]) + (" …" if len(orphaned) > 4 else ""))
        evidence.append(f"{live} of {expected} planned runs for {account} are live")

        if everything and len(never) == n:
            first = (f"Read logs/market-open-{day}.log: the job stops before the plan when the feed shows no fresh "
                     "prices (10 Sep, when an expired FYERS sign-in left the feed deaf) and skips an account whose "
                     f"user name is not active. Once the feed is live, {_RERUN}.")
        else:
            first = f"To start what is missing, {_RERUN}."
        if ended:
            first += (" For the runs that ended, read logs/api.log around the time they stopped first — started "
                      "into the same fault, they die again.")
        if orphaned:
            first += (" Stop " + ", ".join(f"run {o.run_id}" for _, _, o in orphaned[:5]) + " from the Live runner "
                      "first: the job counts a row listed as Running as running, and would not replace it.")

        title = (f"None of the {n} planned runs for {account} is running" if everything
                 else f"{n} of {expected} planned runs for {account} {_plural(n, 'is', 'are')} not running")
        summary = (f"At {_hm(now)} IST {n} of the {expected} runs the morning plan starts for {account} "
                   f"{_plural(n, 'is', 'are')} not running"
                   + (" — the morning job most likely never reached the plan for this account." if everything
                      and len(never) == n else "."))
        return Finding(
            agent=self.name,
            rule="run-missing",
            severity=Severity.HIGH,
            title=title,
            summary=summary,
            fingerprint=f"trading:run-missing:{_slug(account)}",
            where=f"Live runner · {account} · config/morning-plan.txt",
            evidence=evidence[:6],
            suggestion=first,
        )

    # ------------------------------------------------------------------
    # run-duplicated
    # ------------------------------------------------------------------

    def _duplicated(self, running: list[_Run], day: str) -> list[Finding]:
        groups: dict[tuple[str, str, str], list[_Run]] = defaultdict(list)
        for run in running:
            if run.not_a_strategy or run.status != "Running" or not run.active:
                continue
            groups[(run.user_id or run.user.lower(), run.strategy.lower(), run.underlying)].append(run)

        findings: list[Finding] = []
        for (user_id, _, underlying), rows in groups.items():
            if len(rows) < 2:
                continue
            rows.sort(key=lambda r: (r.started or datetime.min.replace(tzinfo=timezone.utc), r.run_id))
            keep, extra = rows[0], rows[1:]
            first = rows[0]

            def started(r: _Run) -> str:
                if r.started is None:
                    return "?"
                return _hms(r.started) + ("" if ist_date(r.started) == day else f" on {ist_date(r.started)}")

            findings.append(Finding(
                agent=self.name,
                rule="run-duplicated",
                severity=Severity.CRITICAL,
                title=f"{first.strategy} on {underlying} is running {len(rows)} times for {first.user}",
                summary=(f"{len(rows)} runs of {first.strategy} on {underlying} are running in {first.user}'s book at "
                         "once, so every signal is traded that many times over."),
                fingerprint=f"trading:run-duplicated:{user_id}:{first.strategy.lower()}:{underlying}",
                where=f"Live runner · {first.user} / {first.strategy} / {underlying}",
                evidence=[f"run {r.run_id}: started {started(r)} IST, {r.trades} trade(s), "
                          f"{r.open_positions} open position(s)" for r in rows[:6]],
                suggestion=(f"Stop {', '.join(f'run {r.run_id}' for r in extra)} from the Live runner and keep run "
                            f"{keep.run_id}. If they started together in the morning, read logs/desk.log for "
                            "market-open launched more than once (desk.sh fired 3–4 times on 24 and 26 Sep)."),
            ))
        return findings

    # ------------------------------------------------------------------
    # overtrading
    # ------------------------------------------------------------------

    def _overtrading(self, ctx: SentinelContext, session, now: datetime, day: str, today: list[_Run],
                     data: dict) -> list[Finding]:
        books: dict[tuple[str, str, str], list[_Run]] = defaultdict(list)
        for run in today:
            if not run.not_a_strategy:
                books[run.key].append(run)

        by_strategy: dict[str, list[tuple[list[_Run], int]]] = defaultdict(list)
        for key, rows in books.items():
            if any(_run_trading(session, now, r) for r in rows):
                by_strategy[key[1]].append((rows, sum(r.trades for r in rows)))

        findings: list[Finding] = []
        for strategy_key, strategy_books in by_strategy.items():
            name = strategy_books[0][0][0].strategy
            busiest = max(total for _, total in strategy_books)
            limit, source = self._trade_limit(ctx, now, day, data, name, busiest)
            if limit is None:
                continue
            over = sorted(((rows, total) for rows, total in strategy_books if total > limit), key=lambda b: -b[1])
            if not over:
                continue
            rows, total = over[0]
            first = rows[0]
            net = sum(r.net + r.unrealized for rows_, _ in over for r in rows_)
            evidence = []
            for book, book_total in over[:5]:
                ids = ", ".join(str(r.run_id) for r in book)
                evidence.append(f"{book[0].user} {book[0].underlying}: {book_total} trades "
                                f"({_plural(len(book), 'run', 'runs')} {ids}), "
                                f"net {_rupees(sum(r.net + r.unrealized for r in book))}"
                                + (", running" if any(r.live for r in book) else ", stopped"))
            evidence.append(f"limit {limit} trades a day per book ({source})")
            also = f" — {len(over)} books past {limit}" if len(over) > 1 else ""
            findings.append(Finding(
                agent=self.name,
                rule="overtrading",
                severity=Severity.MEDIUM,
                title=f"{name} made {total} trades today on {first.underlying} for {first.user}{also}",
                summary=(f"{name} has closed {total} trades today on {first.underlying} ({first.user}); "
                         f"{len(over)} {_plural(len(over), 'book is', 'books are')} past the {limit} a day this check "
                         f"allows ({source}). Net {_rupees(net)} so far across {_plural(len(over), 'it', 'them')}."),
                fingerprint=f"trading:overtrading:{_slug(strategy_key)}:{day}",
                where=f"Live runner · {name}",
                evidence=evidence[:6],
                suggestion=(f"Open the busiest run's trade list on the Live runner: entries and exits minutes apart "
                            "mean it is flipping on noise — stop it or tighten its entry. If this churn is how "
                            f"{name} trades, set SENTINEL_MAX_TRADES_{_env_name(name)} in .env to its normal day "
                            "(0 turns the check off for it)."),
            ))
        return findings

    def _trade_limit(self, ctx: SentinelContext, now: datetime, day: str, data: dict, strategy: str,
                     busiest: int) -> tuple[Optional[int], str]:
        """A strategy's trades a day per book, and where that number came from; None when the check is off."""
        own_var = f"SENTINEL_MAX_TRADES_{_env_name(strategy)}"
        own = _env_int(ctx.env, own_var)
        if own is not None:
            return (own if own > 0 else None), own_var
        fallback = _env_int(ctx.env, "SENTINEL_MAX_TRADES")
        if fallback is not None and fallback <= 0:
            return None, "SENTINEL_MAX_TRADES"
        fallback_limit, fallback_source = (fallback, "SENTINEL_MAX_TRADES") if fallback else (DEFAULT_MAX_TRADES, "the default")
        if busiest <= min(HISTORY_FLOOR, fallback_limit):
            return fallback_limit, fallback_source  # nothing is over either way: no need to ask the history
        history = self._history(ctx, now, day, data).get(strategy.lower())
        if history is None:
            return fallback_limit, fallback_source
        median, _, days = history
        limit = max(HISTORY_FLOOR, math.ceil(HISTORY_MULTIPLE * median))
        return limit, (f"{HISTORY_MULTIPLE}× its median of {median:g} a day over its last {days} trading "
                       f"{_plural(days, 'day', 'days')}")

    def _history(self, ctx: SentinelContext, now: datetime, day: str, data: dict) -> dict[str, tuple[float, int, int]]:
        """
        Per strategy: (median closed trades per book-day, book-days, days) over
        the last five trading days before today. Asked for once a day; a failed
        ask is retried after ten minutes, with the fallback limit meanwhile.
        """
        saved = data.get("history")
        if isinstance(saved, dict):
            medians = saved.get("medians")
            if isinstance(medians, dict):
                out = {}
                for strategy, value in medians.items():
                    if isinstance(value, list) and len(value) == 3 \
                            and all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in value):
                        out[str(strategy)] = (float(value[0]), int(value[1]), int(value[2]))
                return out
            tried = _parse_time(saved.get("failed"))
            if tried is not None and timedelta(0) <= now - tried < HISTORY_RETRY:
                return {}

        today = date.fromisoformat(day)
        try:
            body = ctx.api_get(history_path((today - HISTORY_LOOKBACK).isoformat(),
                                            (today - timedelta(days=1)).isoformat()))
        except Exception:
            body = None
        if not isinstance(body, list):
            data["history"] = {"failed": now.isoformat()}
            return {}

        totals: dict[tuple[str, tuple[str, str, str], str], int] = defaultdict(int)
        for run in _parse_runs(body):
            if run.not_a_strategy or run.started is None:
                continue
            run_day = ist_date(run.started)
            if run_day < day:
                totals[(run.strategy.lower(), run.key, run_day)] += run.trades
        days = set(sorted({d for _, _, d in totals})[-HISTORY_TRADING_DAYS:])
        samples: dict[str, list[int]] = defaultdict(list)
        strategy_days: dict[str, set[str]] = defaultdict(set)
        for (strategy, _, run_day), n in totals.items():
            if run_day in days:
                samples[strategy].append(n)
                strategy_days[strategy].add(run_day)
        medians = {s: [float(statistics.median(v)), len(v), len(strategy_days[s])]
                   for s, v in samples.items() if len(v) >= HISTORY_MIN_SAMPLES}
        data["history"] = {"medians": medians}
        return {s: (v[0], v[1], v[2]) for s, v in medians.items()}

    # ------------------------------------------------------------------
    # account-loss
    # ------------------------------------------------------------------

    def _account_loss(self, env: dict[str, str], session, now: datetime, day: str, today: list[_Run]) -> list[Finding]:
        limit, source = day_loss_limit(env)
        if limit is None:
            return []
        accounts: dict[str, list[_Run]] = defaultdict(list)
        for run in today:
            if not run.role:
                accounts[run.user_id or run.user.lower()].append(run)

        findings: list[Finding] = []
        for _, rows in accounts.items():
            if not any(_run_trading(session, now, r) for r in rows):
                continue  # the day is over for this account: its loss is booked, and nothing more can be done today
            booked = sum(r.net for r in rows)
            marked = sum(r.unrealized for r in rows if r.live)
            total = booked + marked
            if total >= -limit:
                continue
            user = rows[0].user
            worst = sorted(rows, key=lambda r: r.net + (r.unrealized if r.live else 0.0))[:3]
            evidence = [f"today: {_rupees(total)} ({_rupees(booked)} booked, {_rupees(marked)} open) over "
                        f"{len(rows)} run(s)"]
            evidence += [f"run {r.run_id} {r.strategy} {r.underlying}: "
                         f"{_rupees(r.net + (r.unrealized if r.live else 0.0))}"
                         + (" (running)" if r.live else "") for r in worst]
            evidence.append(f"line: {_rupees(-limit)} ({source})")
            findings.append(Finding(
                agent=self.name,
                rule="account-loss",
                severity=Severity.HIGH,
                title=f"{user} is down {_rupees(-total)} today",
                summary=(f"{user}'s runs today stand at {_rupees(total)} at {_hm(now)} IST ({_rupees(booked)} booked, "
                         f"{_rupees(marked)} on open positions), past the {_rupees(-limit)} day-loss line."),
                fingerprint=f"trading:account-loss:{_slug(user)}:{day}",
                where=f"Live runner · {user}",
                evidence=evidence,
                suggestion=("Each run's day stop-loss guards only that run; nothing stops the account as a whole. "
                            "Look at the worst runs on the Live runner and stop them if this loss is not what their "
                            "rules intend. SENTINEL_MAX_DAY_LOSS in .env moves this line."),
            ))
        return findings
