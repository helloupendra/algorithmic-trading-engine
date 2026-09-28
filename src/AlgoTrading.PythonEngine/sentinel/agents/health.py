"""
The health agent: is the desk itself alive?

Every 30 seconds it asks the questions an operator asks first when something
feels wrong, each one a separate rule:

* ``api-down``        the API has answered neither /health nor an authenticated
                      GET for 90 s — longer than the desk's own planned restarts
* ``api-degraded``    /health answers but authenticated GETs fail (sign-in, DB),
                      or /api/Feeds answers but the live runs cannot be listed
* ``public-down``     the API answers here but the public site does not — the
                      stale Cloudflare tunnel of 10 Sep
* ``feed-silent``     no tick has reached Redis for 90 s while a market is open —
                      the Dhan reconnect loop of 24 Sep
* ``no-feed-running`` a market is open and the API supervises no running feed
* ``memory-low`` / ``disk-low`` / ``load-high``   the machine (Linux /proc only)
* ``container-down``  the database or Redis container is not running
* ``desk-down`` / ``desk-duplicated``  the supervisor loop is missing, or doubled

The feed rules watch the desk's own schedule, not only the exchange calendar:
a market counts as open while the calendar says so AND before MARKET_CLOSE_AT
(desk.sh's close, 23:58 by default, when market-close.sh stops every feed),
and on a day NSE does not trade, or on a Saturday or Sunday — where
market-open.sh deliberately starts nothing, even if MCX keeps its evening
session or the exchange holds a special weekend session — only while someone
has started a feed on purpose.

Most rules wait for a second (or third) sighting before they speak: a single
failed probe during an API restart is ordinary, and a watchman that shouts at
every restart is soon ignored. Each streak is kept in the agent's state with
the time of its last sighting, so a streak left over from before a Sentinel
restart does not count. Memory and load, once reported, clear only well past
their line (875 MB free; 80% of the load line), so a reading hovering at the
line is one incident rather than one every few minutes.

Everything here reads: HTTP GETs, one Redis XREVRANGE, /proc, statvfs, and
``docker ps`` / ``ps`` through the context's allowlist. It proposes fixes in the
incident; it never applies one.
"""
from __future__ import annotations

import json
import logging
import os
import re
import traceback
from collections import defaultdict
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Optional

from sentinel.agents.base import Agent
from sentinel.agents.trading import RUNNING_PATH
from sentinel.clock import IST, MCX_OPEN, NSE_OPEN, to_ist
from sentinel.context import SentinelContext
from sentinel.model import Finding, Severity
from sentinel.notify import redact

log = logging.getLogger("sentinel.agents.health")

DEFAULT_API = "http://localhost:5025"
DEFAULT_PUBLIC = "https://openfno.com"
DEFAULT_CONTAINERS = "algotrading_db,algotrading_redis"

HEALTH_TIMEOUT = 5.0
PUBLIC_TIMEOUT = 8.0

# The desk restarts the API itself after three failed health checks 30 s apart,
# and its planned restarts (the 08:45 market-open, deploys) take 12–89 s — the
# 08:45 one 40–43 s on most mornings. Sentinel says nothing before the desk
# would have acted, and holds while the desk is visibly in the middle of a
# restart it began before the outage did.
API_DOWN_CHECKS = 3
API_DOWN_SECONDS = 90
API_RESTART_GRACE = timedelta(seconds=180)   # api_stop waits 16 s, api_start 120 s
DESK_LOG_TAIL = 64 * 1024

# desk.sh's own close: market-close.sh stops every feed at this IST time. 23:58,
# after MCX's latest close (23:55 while the US is on standard time); until 27 Sep
# it was 23:35, which from 1 Nov would have cut the MCX evening, and its watch
# here, twenty minutes short.
DEFAULT_MARKET_CLOSE_AT = time(23, 58)

STREAM = "market:ticks"
FEED_SILENT_SECONDS = 90       # the runners log "FEED STALLED" at the same age
STREAM_PAGE = 2000             # entries per XREVRANGE
STREAM_MAX_PAGES = 5           # never read more than 10 000 entries in one check

MEMORY_HIGH_MB = 700
MEMORY_CRITICAL_MB = 400
MEMORY_CLEAR_MB = 875          # a reported memory-low clears above this: the 700 MB line is 80% of it
DISK_MIN_FREE_BYTES = 5 * 1024 ** 3
DISK_MIN_FREE_FRACTION = 0.10
LOAD_PER_CPU = 2.0
LOAD_CLEAR_FRACTION = 0.8      # a reported load-high clears below 80% of its line

STREAK_STALE = timedelta(minutes=5)   # a streak not extended for this long starts again

# Which exchanges each session covers. NSE and BSE open and close together.
GROUPS = {"NSE": ("NSE", "BSE", "NFO", "BFO"), "MCX": ("MCX",)}
GROUP_LABEL = {"NSE": "NSE/BSE", "MCX": "MCX"}
GROUP_OPENS = {"NSE": NSE_OPEN, "MCX": MCX_OPEN}
_EXCHANGE_GROUP = {ex: g for g, exs in GROUPS.items() for ex in exs}

_REDIS_ID_MAX_SEQ = 18446744073709551615


# --------------------------------------------------------------------- probes

def _http_status(url: str, timeout: float) -> int:
    """The status code of a plain GET; raises when nothing answers."""
    import requests

    r = requests.get(url, timeout=timeout, stream=True, allow_redirects=True,
                     headers={"User-Agent": "algotrading-sentinel/health"})
    try:
        return r.status_code
    finally:
        r.close()


def _read_text(path: str) -> Optional[str]:
    try:
        return Path(path).read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None


def _read_tail(path: str, max_bytes: int = DESK_LOG_TAIL) -> Optional[str]:
    """The last ``max_bytes`` of a file, whole lines only (desk.log grows for weeks)."""
    try:
        with open(path, "rb") as fh:
            fh.seek(0, os.SEEK_END)
            size = fh.tell()
            fh.seek(max(0, size - max_bytes))
            raw = fh.read()
    except OSError:
        return None
    text = raw.decode("utf-8", errors="replace")
    if size > max_bytes:
        text = text.split("\n", 1)[-1]   # the first line was cut in half
    return text


def _disk_usage(path: str) -> Optional[tuple[int, int]]:
    """(total bytes, bytes free to an ordinary user) of the filesystem holding ``path``."""
    try:
        st = os.statvfs(path)
    except (OSError, AttributeError):
        return None
    return st.f_blocks * st.f_frsize, st.f_bavail * st.f_frsize


def _device(path: str) -> Optional[int]:
    try:
        return os.stat(path).st_dev
    except OSError:
        return None


# ------------------------------------------------------------------- helpers

def _short_error(exc: BaseException, limit: int = 180) -> str:
    text = f"{type(exc).__name__}: {exc}".replace("\n", " ")
    text = redact(text)
    return text if len(text) <= limit else text[: limit - 1] + "…"


def _where_raised(exc: BaseException) -> str:
    frames = traceback.extract_tb(exc.__traceback__)
    if not frames:
        return "raised at: unknown"
    last = frames[-1]
    return f"raised at {Path(last.filename).name}:{last.lineno} in {last.name}"


def _ist(moment: datetime, now: datetime) -> str:
    """'11:27:35 IST', with the date when it is not today."""
    local = to_ist(moment)
    if local.date() == to_ist(now).date():
        return local.strftime("%H:%M:%S IST")
    return local.strftime("%d %b %H:%M:%S IST")


def _duration(seconds: float) -> str:
    s = max(0, int(seconds))
    if s < 300:
        return f"{s} s"
    if s < 3600:
        return f"{s // 60} min"
    if s < 86400:
        return f"{s // 3600} h {s % 3600 // 60} min"
    return f"{s // 86400} d {s % 86400 // 3600} h"


def _parse_time(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def _id_ms(entry_id: str) -> Optional[int]:
    try:
        return int(str(entry_id).split("-", 1)[0])
    except ValueError:
        return None


def _previous_id(entry_id: str) -> Optional[str]:
    """The stream ID just before ``entry_id`` — an exclusive bound that works on any Redis version."""
    try:
        ms_text, seq_text = str(entry_id).split("-", 1)
        ms, seq = int(ms_text), int(seq_text)
    except ValueError:
        return None
    if seq > 0:
        return f"{ms}-{seq - 1}"
    if ms > 0:
        return f"{ms - 1}-{_REDIS_ID_MAX_SEQ}"
    return None


def _from_ms(ms: int) -> datetime:
    return datetime.fromtimestamp(ms / 1000.0, tz=timezone.utc)


@dataclass
class _Tick:
    at: datetime
    symbol: str
    source: str


@dataclass
class _StreamView:
    """What the newest part of market:ticks says about each exchange group."""

    newest: dict[str, _Tick]                 # group -> newest live tick seen
    scanned: int                             # entries read
    oldest_read: Optional[datetime]          # ID time of the oldest entry read
    exhausted: bool                          # the whole stream was read
    newest_any: Optional[tuple[datetime, str]]  # newest entry of any exchange (ID time, exchange)


@dataclass
class _Proc:
    pid: int
    ppid: int
    sid: Optional[int]      # None where ps has no session column (macOS)
    etime: str
    args: str


# Linux ps first (with the session id); macOS ps has no "sid" keyword, exits 1
# and prints the table without that column, so the plain form is asked next.
_PS_WITH_SID = ("ps", "-eo", "pid=,ppid=,sid=,etime=,args=")
_PS_PLAIN = ("ps", "-eo", "pid=,ppid=,etime=,args=")


def _starts_api(args: str) -> bool:
    """
    Whether this command is what api_start runs: ``nohup dotnet run --project
    src/AlgoTrading.Api`` (nohup execs into dotnet), or the API binary itself.
    ``dotnet build`` — which the desk loop runs directly during a deploy — is not.
    """
    tokens = args.split()
    if not tokens:
        return False
    exe = os.path.basename(tokens[0])
    if exe == "nohup":
        return True
    if exe == "dotnet":
        return len(tokens) > 1 and (tokens[1] == "run" or tokens[1].endswith("AlgoTrading.Api.dll"))
    return exe in ("AlgoTrading.Api", "AlgoTrading.Api.dll")


# What desk.sh / market-open.sh write around an API restart (scripts/lib/desk-common.sh:
# api_stop, api_start) — to logs/desk.log, and to logs/market-open-<date>.log.
# Stamped "2026-09-28 08:45:34  text" since 28 Sep 2026, "08:45:34  text" before: the date is optional.
_DESK_LINE = re.compile(r"^(?:(?P<date>\d{4}-\d{2}-\d{2})[ T])?(?P<h>\d{2}):(?P<m>\d{2}):(?P<s>\d{2})\s+(?P<text>.*)$")
_API_RESTARTING = ("stopping the API", "starting the API")
_API_SETTLED = ("API up", "API already healthy", "the API did not come up")


@dataclass
class _DeskRestart:
    """The desk's last word on the API, in desk.log or today's market-open / market-close log."""

    at: datetime                 # when that line was written
    line: str                    # the message, redacted
    in_progress: bool            # stopping/starting, with no "API up" (or failure) after it
    began: Optional[datetime]    # the first line of the restart still in progress
    source: str = "desk.log"     # the file it was read from


def _desk_line_time(hh: int, mm: int, ss: int, now: datetime, day: Optional[str] = None) -> Optional[datetime]:
    """
    A desk.log stamp, IST wall-clock time. A dated line says its own day; an
    undated one (before 28 Sep 2026) is today's, or yesterday's if that is in the future.
    """
    try:
        stamp = time(hh, mm, ss)
        if day:
            return datetime.combine(date.fromisoformat(day), stamp, tzinfo=IST).astimezone(timezone.utc)
    except ValueError:
        return None
    local = to_ist(now)
    moment = datetime.combine(local.date(), stamp, tzinfo=IST)
    if moment > local + timedelta(seconds=60):
        moment -= timedelta(days=1)
    return moment.astimezone(timezone.utc)


def _parse_close_at(value: Optional[str]) -> time:
    """MARKET_CLOSE_AT as desk.sh reads it: HHMM ("2358"). Anything else means the default."""
    text = (value or "").strip()
    if len(text) == 4 and text.isdigit():
        try:
            return time(int(text[:2]), int(text[2:]))
        except ValueError:
            pass
    return DEFAULT_MARKET_CLOSE_AT


def _desk_mode(args: str) -> Optional[str]:
    """
    The desk.sh mode ('' for the foreground loop, '--daemon', '--stop', ...) when
    this process is the desk script itself, else None. An editor, a ``tail`` or a
    shell whose command line merely mentions desk.sh is not the desk.
    """
    tokens = args.split()
    if not tokens:
        return None
    first = os.path.basename(tokens[0])
    if tokens[0].endswith("desk.sh"):
        rest = tokens[1:]
    elif first in ("bash", "sh", "zsh", "dash") and len(tokens) > 1 and tokens[1].endswith("desk.sh"):
        rest = tokens[2:]
    else:
        return None
    return rest[0] if rest else ""


# ---------------------------------------------------------------------- agent

class HealthAgent(Agent):
    """
    The probes are injectable so the tests never touch the network, /proc or
    the host's disks: ``http_status(url, timeout) -> int`` (raises when nothing
    answers), ``read_text(path) -> str | None``, ``read_tail(path) -> str |
    None`` (the end of a long log), ``disk_usage(path) -> (total, free) |
    None`` and ``cpu_count() -> int``. Commands go through ``ctx.run`` like
    every other agent's.
    """

    name = "health"
    interval_seconds = 30
    resolve_after = 2

    def __init__(self, http_status: Optional[Callable[[str, float], int]] = None,
                 read_text: Optional[Callable[[str], Optional[str]]] = None,
                 disk_usage: Optional[Callable[[str], Optional[tuple[int, int]]]] = None,
                 cpu_count: Optional[Callable[[], int]] = None,
                 device_of: Optional[Callable[[str], Optional[int]]] = None,
                 read_tail: Optional[Callable[[str], Optional[str]]] = None) -> None:
        self.http_status = http_status or _http_status
        self.read_text = read_text or _read_text
        self.read_tail = read_tail or _read_tail
        self.disk_usage = disk_usage or _disk_usage
        self.cpu_count = cpu_count or (lambda: os.cpu_count() or 1)
        self.device_of = device_of or _device

    # The check runs every rule group on its own: a bug in one must not blind the others.
    def check(self, ctx: SentinelContext) -> list[Finding]:
        state = ctx.state(self.name)
        now = ctx.now()
        findings: list[Finding] = []
        probe: dict[str, Any] = {}
        down_containers: set[str] = set()

        def guard(group: str, fn: Callable[[], list[Finding]]) -> None:
            try:
                findings.extend(fn())
            except Exception as exc:  # reported, not swallowed, and the next group still runs
                log.exception("health %s check failed", group)
                findings.append(Finding(
                    agent=self.name, rule="check-failed", severity=Severity.MEDIUM,
                    title=f"Sentinel could not run its {group} health check",
                    summary=f"The {group} check raised {_short_error(exc)}; its rule is blind until this is fixed.",
                    fingerprint=f"health:check-failed:{group}",
                    where="sentinel/agents/health.py",
                    evidence=[_short_error(exc), _where_raised(exc)],
                    suggestion="A bug in Sentinel, not in the desk: read logs for the traceback "
                               "(python -m sentinel --once --only health --verbose).",
                ))

        guard("api", lambda: self._api_rules(ctx, state.data, now, probe))
        guard("public", lambda: self._public_rule(ctx, state.data, now, probe))
        guard("containers", lambda: self._container_rule(ctx, state.data, now, down_containers))
        guard("feed", lambda: self._feed_rules(ctx, state.data, now, probe, down_containers))
        guard("memory", lambda: self._memory_rule(ctx, state.data, now))
        guard("disk", lambda: self._disk_rule(ctx))
        guard("load", lambda: self._load_rule(ctx, state.data, now))
        guard("desk", lambda: self._desk_rules(ctx, state.data, now))

        state.save()
        return findings

    # ------------------------------------------------------------- streaks

    @staticmethod
    def _streak(data: dict, key: str, failing: bool, now: datetime) -> tuple[int, datetime]:
        """
        Count consecutive failing checks for ``key``: (count, first failing
        moment). A passing check clears the streak; one not extended for
        STREAK_STALE (Sentinel was stopped) starts again at 1.
        """
        streaks = data.setdefault("streaks", {})
        if not failing:
            streaks.pop(key, None)
            return 0, now
        prev = streaks.get(key)
        prev_at = _parse_time(prev.get("at")) if isinstance(prev, dict) else None
        if prev_at is not None and timedelta(0) <= now - prev_at <= STREAK_STALE:
            count = int(prev.get("n", 0)) + 1
            since = _parse_time(prev.get("since")) or now
        else:
            count, since = 1, now
        streaks[key] = {"n": count, "at": now.isoformat(), "since": since.isoformat()}
        return count, since

    @staticmethod
    def _streak_so_far(data: dict, key: str, now: datetime) -> int:
        """The streak as the previous check left it (0 when there is none, or it went stale) — without extending it."""
        prev = data.get("streaks", {}).get(key)
        prev_at = _parse_time(prev.get("at")) if isinstance(prev, dict) else None
        if prev_at is None or not timedelta(0) <= now - prev_at <= STREAK_STALE:
            return 0
        try:
            return int(prev.get("n", 0))
        except (TypeError, ValueError):
            return 0

    # -------------------------------------------------------------- the API

    def _api_rules(self, ctx: SentinelContext, data: dict, now: datetime, probe: dict) -> list[Finding]:
        base = (ctx.env.get("API_BASE_URL") or DEFAULT_API).rstrip("/")
        probe["base"] = base

        try:
            code = self.http_status(f"{base}/health", HEALTH_TIMEOUT)
            probe["health_ok"] = code == 200
            probe["health_note"] = f"HTTP {code}"
        except Exception as exc:
            probe["health_ok"] = False
            probe["health_note"] = _short_error(exc)

        try:
            body = ctx.api_get("/api/Feeds")
            if isinstance(body, list):
                probe["feeds"] = [f for f in body if isinstance(f, dict)]
            else:
                probe["feeds"] = None
                probe["feeds_error"] = f"unexpected body ({type(body).__name__})"
        except Exception as exc:
            probe["feeds"] = None
            probe["feeds_error"] = _short_error(exc)

        # The strategy runs are asked for too: /api/Feeds reads no table, and on a day the runs query fails (a
        # 500, a timeout) the trading agent is blind while every probe here says the API is fine. Asked only when
        # /api/Feeds answered — otherwise the API is already down or degraded, and a hung API would cost this
        # check another 15 s timeout for nothing.
        runs_error = ""
        if probe["feeds"] is not None:
            try:
                body = ctx.api_get(RUNNING_PATH)
                if not isinstance(body, list):
                    runs_error = f"unexpected body ({type(body).__name__})"
            except Exception as exc:
                runs_error = _short_error(exc)
        probe["runs_error"] = runs_error

        health_ok = probe["health_ok"]
        auth_ok = probe["feeds"] is not None
        findings: list[Finding] = []

        down, since = self._streak(data, "api-down", not health_ok and not auth_ok, now)
        span = (now - since).total_seconds()
        if down >= API_DOWN_CHECKS and span >= API_DOWN_SECONDS:
            restart = self._desk_restart(ctx, now)
            planned = (restart is not None and restart.in_progress and restart.began is not None
                       and restart.began <= since + timedelta(seconds=2)
                       and now - restart.began <= API_RESTART_GRACE)
            if not planned:
                evidence = [f"GET {base}/health: {probe['health_note']}",
                            f"GET /api/Feeds: {probe.get('feeds_error', '')}",
                            f"failing since {_ist(since, now)} ({down} checks)"]
                if restart is not None and now - restart.at <= timedelta(minutes=30):
                    evidence.append(f"{restart.source} {_ist(restart.at, now)}: {restart.line}"
                                    + (" — no \"API up\" after it" if restart.in_progress else ""))
                findings.append(Finding(
                    agent=self.name, rule="api-down", severity=Severity.CRITICAL,
                    title="The API is not answering",
                    summary=(f"Neither GET /health nor an authenticated GET has answered for {_duration(span)} "
                             f"({down} checks in a row), since {_ist(since, now)}. Runs cannot register, the "
                             "console is dark, and no order the API would place can go out."),
                    fingerprint="health:api-down",
                    where=f"AlgoTrading.Api ({base})",
                    evidence=evidence,
                    suggestion=("The desk restarts the API by itself after three failed health checks (about "
                                "90 s), and its planned restarts take under 90 s; if this stays open the desk is "
                                "not managing it — look for a desk-down incident, run scripts/status.sh, and read "
                                "logs/api.log (each restart rotates it to logs/api-until-*.log). If the database "
                                "container is down the API will not start."),
                ))

        degraded_now = (health_ok and not auth_ok) or (auth_ok and bool(runs_error))
        degraded, since = self._streak(data, "api-degraded", degraded_now, now)
        if degraded >= 2:
            if not auth_ok:
                error = probe.get("feeds_error", "")
                said = (f"GET /health answers, but authenticated GETs have failed for {degraded} checks in a row "
                        f"since {_ist(since, now)} — the process is serving, the work behind it is not.")
                lines = [f"GET {base}/health: {probe['health_note']}", f"GET /api/Feeds: {error}"]
            else:
                error = runs_error
                said = (f"The API answers, but GET {RUNNING_PATH} — the live strategy runs — has failed for "
                        f"{degraded} checks in a row since {_ist(since, now)}. The console's Live runner and "
                        "Sentinel's trading checks read the same list, so neither can see the runs.")
                lines = [f"GET {RUNNING_PATH}: {error}", "GET /api/Feeds: answered",
                         f"GET {base}/health: {probe['health_note']}"]
            findings.append(Finding(
                agent=self.name, rule="api-degraded", severity=Severity.HIGH,
                title=("The API is up but its signed-in requests fail" if not auth_ok
                       else "The API answers but cannot list the strategy runs"),
                summary=said,
                fingerprint="health:api-degraded",
                where=f"AlgoTrading.Api ({base})",
                evidence=lines + [f"failing since {_ist(since, now)}"],
                suggestion=self._degraded_advice(error),
            ))
        return findings

    @staticmethod
    def _degraded_advice(error: str) -> str:
        if "429" in error:
            return ("The sign-in limiter refused the sign-in. On 24 Sep the same limiter killed every runner at "
                    "boot (429 on /api/UserAuth/login); it was fixed — check the limiter's settings were not "
                    "reverted and that nothing is signing in in a loop (logs/api.log).")
        if "401" in error or "403" in error:
            return ("The admin sign-in is refused: check ADMIN_USERNAME/ADMIN_PASSWORD in .env still match the "
                    "account (a changed password, or a setting added to .env but not regenerated into "
                    "appsettings.Local.json), then logs/api.log.")
        if any(code in error for code in ("500", "502", "503", "504")):
            return ("The API answers but fails behind it — most often the database. Check `docker ps` for "
                    "algotrading_db and read logs/api.log around the time above.")
        return "Read logs/api.log around the time above and check that algotrading_db is running (`docker ps`)."

    def _desk_restart(self, ctx: SentinelContext, now: datetime) -> Optional[_DeskRestart]:
        """
        The desk's last API line ("stopping the API", "starting the API", "API
        up", ...), and whether a restart is still in progress — read only when
        the API has already been down long enough to report.

        Read from logs/desk.log and from today's market-open and market-close
        logs, in time order. desk.sh copies the 08:45 run of market-open.sh into
        desk.log, but a morning job run by hand writes only to its own log: its
        planned restart of the API was invisible here, and an ordinary
        restart read as a crash (CRITICAL api-down).
        """
        day = to_ist(now).strftime("%Y-%m-%d")
        lines: list[tuple[datetime, int, int, str, str, bool]] = []
        for order, name in enumerate(("desk.log", f"market-open-{day}.log", f"market-close-{day}.log")):
            text = self.read_tail(str(ctx.logs_dir / name))
            for index, raw in enumerate((text or "").splitlines()):
                m = _DESK_LINE.match(raw.strip())
                if not m:
                    continue
                message = m["text"].strip()
                restarting = any(message.startswith(p) for p in _API_RESTARTING)
                settled = any(message.startswith(p) for p in _API_SETTLED) or \
                    message.startswith("WARN: the API did not come up")
                if not restarting and not settled:
                    continue
                at = _desk_line_time(int(m["h"]), int(m["m"]), int(m["s"]), now, m["date"])
                if at is not None:
                    lines.append((at, order, index, name, message, restarting))

        # One restart run by desk.sh is in two of these files, line for line; the order is what matters.
        last: Optional[_DeskRestart] = None
        began: Optional[datetime] = None
        for at, _, _, name, message, restarting in sorted(lines, key=lambda x: x[:3]):
            if restarting:
                # "stopping" opens a restart; the "starting" right after it belongs to the same one.
                if began is None or message.startswith("stopping the API"):
                    began = at
            else:
                began = None
            line = redact(message)
            last = _DeskRestart(at=at, line=line if len(line) <= 100 else line[:99] + "…",
                                in_progress=restarting, began=began, source=name)
        return last

    # ------------------------------------------------------- the public site

    def _public_rule(self, ctx: SentinelContext, data: dict, now: datetime, probe: dict) -> list[Finding]:
        public = (ctx.env.get("SENTINEL_PUBLIC_URL", DEFAULT_PUBLIC) or "").strip().rstrip("/")
        if public.lower() in ("", "off", "none", "0", "false"):
            self._streak(data, "public-down", False, now)
            return []
        if not probe.get("health_ok"):
            # The local API is down: api-down says so, and the tunnel cannot be judged.
            self._streak(data, "public-down", False, now)
            return []

        url = f"{public}/health"
        try:
            code = self.http_status(url, PUBLIC_TIMEOUT)
            note = f"HTTP {code}"
            failing = code != 200
        except Exception as exc:
            note = _short_error(exc)
            failing = True

        count, since = self._streak(data, "public-down", failing, now)
        if count < 2:
            return []
        return [Finding(
            agent=self.name, rule="public-down", severity=Severity.HIGH,
            title=f"{public.split('://', 1)[-1]} is not reachable from outside",
            summary=(f"The public site has not answered 200 for {count} checks since {_ist(since, now)}, while the "
                     "API answers locally — traders and the console see an error page."),
            fingerprint="health:public-down",
            where=f"Cloudflare tunnel -> {probe.get('base', DEFAULT_API)}",
            evidence=[f"GET {url}: {note}",
                      f"GET {probe.get('base', DEFAULT_API)}/health: {probe.get('health_note', '')}",
                      f"failing since {_ist(since, now)}"],
            suggestion=("On 10 Sep a stale tunnel connector made the domain answer 502 while the API was fine. "
                        "Check `systemctl status cloudflared` on the server and restart it; if the Cloudflare "
                        "dashboard shows a second connector on the algotrading tunnel (an old machine), stop that "
                        "one."),
        )]

    # ------------------------------------------------------------ containers

    def _container_rule(self, ctx: SentinelContext, data: dict, now: datetime,
                        down_out: set[str]) -> list[Finding]:
        wanted = [n.strip() for n in (ctx.env.get("SENTINEL_CONTAINERS") or DEFAULT_CONTAINERS).split(",")
                  if n.strip()]
        try:
            rc, out = ctx.run(["docker", "ps", "--format", "{{.Names}}\t{{.Status}}"])
        except PermissionError:
            return []
        if rc != 0:
            # No docker here, or no permission to ask it: nothing can be said.
            for name in wanted:
                self._streak(data, f"container:{name}", False, now)
            return []

        running: dict[str, str] = {}
        for line in out.splitlines():
            name, _, status = line.partition("\t")
            if name.strip():
                running[name.strip()] = status.strip()

        findings: list[Finding] = []
        for name in wanted:
            status = running.get(name)
            down = status is None or status.lower().startswith("restarting")
            if down:
                down_out.add(name)
            count, since = self._streak(data, f"container:{name}", down, now)
            if count < 2:
                continue
            seen = f"{name}: {status}" if status else f"{name}: not in `docker ps`"
            others = ", ".join(sorted(running)) or "none"
            findings.append(Finding(
                agent=self.name, rule="container-down", severity=Severity.CRITICAL,
                title=f"The {self._container_role(name)} container is down",
                summary=(f"{name} has not been running for {count} checks, since {_ist(since, now)}. "
                         f"{self._container_impact(name)}"),
                fingerprint=f"health:container-down:{name}",
                where=f"docker container {name}",
                evidence=[seen, f"running containers: {others}"[:300], f"down since {_ist(since, now)}"],
                suggestion=(f"Start it with `docker start {name}` (or `docker compose up -d` from the repository) "
                            f"and read `docker logs --tail 50 {name}` for why it stopped — a full disk is the "
                            "usual cause. Sentinel does not start containers itself."),
            ))
        return findings

    @staticmethod
    def _container_role(name: str) -> str:
        if "redis" in name:
            return "Redis"
        if "db" in name or "postgres" in name:
            return "database"
        return name

    @staticmethod
    def _container_impact(name: str) -> str:
        if "redis" in name:
            return "Every strategy reads its ticks from Redis: the runners are deaf until it is back."
        if "db" in name or "postgres" in name:
            return "The API, the runs' trades and Sentinel's own incident store all live in this database."
        return ""

    # --------------------------------------------------------------- the feed

    def _feed_rules(self, ctx: SentinelContext, data: dict, now: datetime, probe: dict,
                    down_containers: set[str]) -> list[Finding]:
        session = ctx.session()
        feeds = probe.get("feeds")
        running = [f for f in feeds if f.get("isRunning") is True] if feeds is not None else None

        # Watch what the desk means to run, not only what the exchange calendar says:
        # * after MARKET_CLOSE_AT market-close.sh has stopped every feed — by default
        #   after the MCX close, but a desk told to close earlier stops them sooner;
        # * on a day NSE does not trade, market-open.sh starts nothing on purpose, even
        #   when MCX keeps its evening session — unless someone started a feed by hand;
        # * nor on a Saturday or Sunday (desk.sh runs it Monday to Friday, and it stops at
        #   "Weekend"), even when the exchange calendar has a special session on one — a
        #   Budget day, a disaster-recovery drill, Muhurat trading.
        close_at = _parse_close_at(ctx.env.get("MARKET_CLOSE_AT"))
        before_close = to_ist(now).time() < close_at
        desk_trades_today = (session.trading_day and to_ist(now).weekday() < 5) or bool(running)
        watched = {group: is_open and before_close and desk_trades_today
                   for group, is_open in (("NSE", session.nse_open), ("MCX", session.mcx_open))}

        # Remember when each market was last seen unwatched: the silence clock starts
        # at the open (or at the feed started on a holiday), not at yesterday's last tick.
        closed_seen = data.setdefault("closed_seen", {})
        for group, is_watched in watched.items():
            if not is_watched:
                closed_seen[group] = now.isoformat()

        groups = [g for g, is_watched in watched.items() if is_watched]
        if not groups:
            self._streak(data, "no-feed-running", False, now)
            return []

        view: Optional[_StreamView] = None
        redis_error: Optional[str] = None
        r = ctx.redis()
        if r is not None:
            try:
                view = self._read_stream(r, now, groups)
            except Exception as exc:
                redis_error = _short_error(exc)

        if not session.from_calendar:
            calendar = "weekday rule (API calendar unavailable)"
        elif session.remembered:
            calendar = "exchange calendar as it answered earlier today; it is not answering now"
        else:
            calendar = "exchange calendar"
        open_label = ", ".join(GROUP_LABEL[g] for g in groups)

        # Silence per group, measured from the later of the newest tick and the open.
        silent: dict[str, tuple[float, str]] = {}   # group -> (age seconds, how the newest tick was seen)
        if view is not None:
            for group in groups:
                anchor = self._open_anchor(group, now, closed_seen)
                tick = view.newest.get(group)
                if tick is not None:
                    last = tick.at
                    seen = (f"newest {GROUP_LABEL[group]} tick {_ist(tick.at, now)} "
                            f"({tick.symbol or '?'} via {tick.source or '?'})")
                elif view.exhausted:
                    last = None
                    seen = f"no live {GROUP_LABEL[group]} tick anywhere in {STREAM} ({view.scanned} entries)"
                elif view.oldest_read is not None and (now - view.oldest_read).total_seconds() > FEED_SILENT_SECONDS:
                    last = view.oldest_read   # the newest tick is older than everything read
                    seen = (f"no {GROUP_LABEL[group]} tick in the newest {view.scanned} entries, "
                            f"which reach back to {_ist(view.oldest_read, now)}")
                else:
                    continue  # the stream is too busy to tell within the bound: say nothing
                reference = max(last, anchor) if last is not None else anchor
                age = (now - reference).total_seconds()
                if age > FEED_SILENT_SECONDS:
                    silent[group] = (age, seen)

        findings: list[Finding] = []

        # Twice in a row: a feed switch (stop Dhan, start FYERS) leaves a few seconds with none running.
        none_count, _ = self._streak(data, "no-feed-running", running == [], now)
        if running == []:
            if none_count < 2:
                return findings   # and no feed-silent either: the cause would be this, not the feed
            fresh = view is not None and not silent and all(g in view.newest for g in groups)
            states = [f"{f.get('key', '?')}: not running (source {f.get('source', '?')})" for f in feeds or []]
            if not states:
                states = ["/api/Feeds lists no feeds at all"]
            evidence = states[:4] + [f"open now: {open_label} ({calendar})"]
            if fresh:
                evidence.append("ticks are still arriving in Redis — from a feed the API does not supervise")
            findings.append(Finding(
                agent=self.name, rule="no-feed-running",
                severity=Severity.HIGH if fresh else Severity.CRITICAL,
                title="No market data feed is running while the market is open",
                summary=(f"{open_label} is open and /api/Feeds shows no running feed"
                         + ("; ticks still arrive, from something the API is not supervising."
                            if fresh else " — the strategies have nothing to trade on.")),
                fingerprint="health:no-feed-running",
                where="/api/Feeds",
                evidence=evidence[:6],
                suggestion=("scripts/market-open.sh starts the Dhan feed at 08:45 and falls back to FYERS — read "
                            "today's logs/market-open-YYYY-MM-DD.log for why it did not. Start one from Data → Feeds "
                            "(POST /api/Feeds/dhan/start, or /api/Feeds/fyers/start). Sentinel does not start "
                            "feeds itself."),
            ))
            return findings   # the cause is known; a feed-silent on top would say the same thing twice

        if redis_error is not None:
            if not any("redis" in name for name in down_containers):
                findings.append(Finding(
                    agent=self.name, rule="redis-unreachable", severity=Severity.HIGH,
                    title="Sentinel cannot read the tick stream in Redis",
                    summary=(f"{open_label} is open but reading {STREAM} failed, so whether ticks are flowing "
                             "cannot be checked — and the runners read the same Redis."),
                    fingerprint="health:redis-unreachable",
                    where=f"Redis {STREAM}",
                    evidence=[redis_error, f"open now: {open_label} ({calendar})"],
                    suggestion=("Check `docker ps` for algotrading_redis and REDIS_HOST/REDIS_PORT/REDIS_PASSWORD "
                                "in .env; if Redis is up, try `redis-cli XLEN market:ticks` on the server."),
                ))
            return findings

        if not silent or view is None:
            return findings

        if feeds is None:
            feed_line = f"/api/Feeds did not answer ({probe.get('feeds_error', '')}) — which feed runs is unknown"
            running_keys: list[str] = []
        else:
            running_keys = [str(f.get("key", "?")) for f in running or []]
            feed_line = "running per /api/Feeds: " + ", ".join(
                f"{f.get('key', '?')} (source {f.get('source', '?')}, pid {f.get('processId', '?')})"
                for f in running or [])
        whole_stream = ""
        if view.newest_any is not None:
            at, exchange = view.newest_any
            whole_stream = f"newest entry of any exchange: {_ist(at, now)} ({exchange or '?'})"
        flowing = [GROUP_LABEL[g] for g in groups if g not in silent]

        for group, (age, seen) in silent.items():
            label = GROUP_LABEL[group]
            evidence = [seen, feed_line]
            if whole_stream:
                evidence.append(whole_stream)
            evidence.append(f"{label} session open ({calendar})")
            if flowing:
                evidence.append(f"still flowing: {', '.join(flowing)}")
            findings.append(Finding(
                agent=self.name, rule="feed-silent", severity=Severity.CRITICAL,
                title=f"{label} ticks have stopped while the market is open",
                summary=(f"No live {label} tick has reached Redis {STREAM} for {_duration(age)} (threshold "
                         f"{FEED_SILENT_SECONDS} s) while the {label} session is open"
                         + (f"; {', '.join(flowing)} still flows, so the feed is alive but its {label} "
                            "subscription is not." if flowing else " — every strategy on it is trading blind.")),
                fingerprint=f"health:feed-silent:{group}",
                where=f"Redis {STREAM} · feed {', '.join(running_keys) or 'unknown'}",
                evidence=evidence[:6],
                suggestion=self._feed_advice(running_keys),
            ))
        return findings

    @staticmethod
    def _open_anchor(group: str, now: datetime, closed_seen: dict) -> datetime:
        """When the current session began: today's usual open, or later if the market was seen closed since."""
        today = to_ist(now).date()
        anchor = datetime.combine(today, GROUP_OPENS[group], tzinfo=IST).astimezone(timezone.utc)
        last_closed = _parse_time(closed_seen.get(group))
        if last_closed is not None and last_closed > anchor:
            anchor = last_closed
        return anchor

    @staticmethod
    def _feed_advice(running: list[str]) -> str:
        switch = ("POST /api/Feeds/dhan/stop, then POST /api/Feeds/fyers/start (Data → Feeds in the console). "
                  "Sentinel does not switch feeds itself.")
        if running == ["fyers"]:
            return ("On 10 Sep an expired FYERS token was reported \"authenticated\" and the strategies ran deaf "
                    "all morning: sign in to FYERS again and restart its feed, or switch to Dhan "
                    "(POST /api/Feeds/fyers/stop, then /api/Feeds/dhan/start). Sentinel does not switch feeds "
                    "itself.")
        if "dhan" in running:
            return ("On 24 Sep the Dhan feed looped connect → subscribe → \"Connection to remote host was lost\" "
                    "and delivered nothing from 11:27:35 (logs/engine/dhan-feed-<pid>.log shows WATCHDOG and "
                    "\"carried no ticks\" lines). What fixed it: stop Dhan and start FYERS — " + switch)
        return "Restart the running feed, or do what fixed 24 Sep: stop Dhan and start FYERS — " + switch

    def _read_stream(self, r: Any, now: datetime, groups: list[str]) -> _StreamView:
        """
        The newest live tick per exchange group, from the head of the stream,
        reading at most STREAM_PAGE × STREAM_MAX_PAGES entries. Stops as soon
        as every open group has been seen, or once it has read back past the
        silence threshold — anything older cannot change the verdict.
        """
        wanted = set(groups)
        newest: dict[str, _Tick] = {}
        scanned = 0
        oldest_read: Optional[datetime] = None
        exhausted = False
        newest_any: Optional[tuple[datetime, str]] = None
        horizon = now - timedelta(seconds=FEED_SILENT_SECONDS)
        upper = "+"

        for _ in range(STREAM_MAX_PAGES):
            entries = r.xrevrange(STREAM, max=upper, min="-", count=STREAM_PAGE)
            if not entries:
                exhausted = True
                break
            for entry_id, fields in entries:
                scanned += 1
                ms = _id_ms(entry_id)
                id_time = _from_ms(ms) if ms is not None else None
                if id_time is not None:
                    oldest_read = id_time
                fields = fields or {}
                exchange = str(fields.get("exchange") or "").upper()
                if newest_any is None and id_time is not None:
                    newest_any = (id_time, exchange)
                payload: dict = {}
                raw = fields.get("payload")
                if raw:
                    try:
                        loaded = json.loads(raw)
                        payload = loaded if isinstance(loaded, dict) else {}
                    except (TypeError, ValueError):
                        payload = {}
                if not exchange:
                    exchange = str(payload.get("exchange") or "").upper()
                symbol = str(fields.get("symbol") or payload.get("symbol") or "")
                if not exchange and ":" in symbol:
                    exchange = symbol.split(":", 1)[0].upper()
                group = _EXCHANGE_GROUP.get(exchange)
                if group not in wanted or payload.get("isReplay") is True:
                    continue
                at = _parse_time(payload.get("receivedUtc")) or id_time
                if at is None:
                    continue
                current = newest.get(group)
                if current is None or at > current.at:
                    newest[group] = _Tick(at, symbol, str(payload.get("sourceKey") or ""))
            if len(entries) < STREAM_PAGE:
                exhausted = True
                break
            if wanted.issubset(newest):
                break
            if oldest_read is not None and oldest_read < horizon:
                break
            upper = _previous_id(entries[-1][0])
            if upper is None:
                exhausted = True
                break

        return _StreamView(newest, scanned, oldest_read, exhausted, newest_any)

    # ------------------------------------------------------------ the machine

    def _top_processes(self, ctx: SentinelContext, sort: str, column: str) -> list[str]:
        """The three biggest processes by ``sort`` (Linux ps). Names only — never command lines."""
        try:
            rc, out = ctx.run(["ps", "-eo", f"pid=,{column}=,comm=", f"--sort=-{sort}"])
        except PermissionError:
            return []
        if rc != 0:
            return []
        rows = []
        for line in out.splitlines()[:3]:
            parts = line.split(None, 2)
            if len(parts) == 3:
                rows.append(parts)
        return [f"{comm} (pid {pid}) {value}" for pid, value, comm in rows]

    def _memory_rule(self, ctx: SentinelContext, data: dict, now: datetime) -> list[Finding]:
        text = self.read_text("/proc/meminfo")
        if not text:
            return []   # not Linux: nothing to read, nothing to say
        values: dict[str, int] = {}
        for line in text.splitlines():
            key, _, rest = line.partition(":")
            parts = rest.split()
            if parts and parts[0].isdigit():
                values[key.strip()] = int(parts[0])  # kB
        if "MemAvailable" not in values:
            return []
        avail_mb = values["MemAvailable"] // 1024
        total_mb = values.get("MemTotal", 0) // 1024

        # Once reported, it stays reported until memory is back above MEMORY_CLEAR_MB: a box hovering at
        # 690–710 MB would otherwise open, resolve and reopen the incident every few minutes.
        reported = self._streak_so_far(data, "memory-low", now) >= 2
        recovering = reported and MEMORY_HIGH_MB <= avail_mb < MEMORY_CLEAR_MB
        low, since = self._streak(data, "memory-low", avail_mb < MEMORY_HIGH_MB or recovering, now)
        # Critical wants two readings too: a dotnet build during a deploy can dip under 400 MB for one.
        critical, _ = self._streak(data, "memory-critical", avail_mb < MEMORY_CRITICAL_MB, now)
        if critical >= 2:
            severity = Severity.CRITICAL          # the OOM killer is close
        elif low >= 2:
            severity = Severity.HIGH              # a build can dip for a moment; twice is real
        else:
            return []

        evidence = [f"MemAvailable {avail_mb} MB of {total_mb} MB"]
        if "SwapTotal" in values:
            evidence.append(f"swap free {values.get('SwapFree', 0) // 1024} MB of {values['SwapTotal'] // 1024} MB")
        top = self._top_processes(ctx, "rss", "rss")
        if top:
            evidence.append("largest (RSS kB): " + "; ".join(top))
        evidence.append(f"low since {_ist(since, now)}")
        return [Finding(
            agent=self.name, rule="memory-low", severity=severity,
            title="The server is running out of memory",
            summary=(f"Only {avail_mb} MB of {total_mb} MB is available (warning below {MEMORY_HIGH_MB} MB, "
                     f"critical below {MEMORY_CRITICAL_MB} MB), low since {_ist(since, now)}."
                     + (f" Back above {MEMORY_HIGH_MB} MB, but this stays open until more than {MEMORY_CLEAR_MB} MB "
                        "is free, so memory hovering at the line is one incident." if recovering else "")),
            fingerprint="health:memory-low",
            where="server memory (/proc/meminfo)",
            evidence=evidence[:6],
            suggestion=("On 15 Sep a research job over two years of option bars froze this box. Stop any research "
                        "or backtest job first (the largest processes are listed) — never a live runner — and run "
                        "research on the Mac."),
        )]

    def _disk_rule(self, ctx: SentinelContext) -> list[Finding]:
        paths = ["/"]
        repo = str(ctx.repo_root)
        root_dev, repo_dev = self.device_of("/"), self.device_of(repo)
        if repo_dev is not None and root_dev is not None and repo_dev != root_dev:
            paths.append(repo)

        findings: list[Finding] = []
        for path in paths:
            usage = self.disk_usage(path)
            if not usage:
                continue
            total, free = usage
            if total <= 0:
                continue
            fraction = free / total
            if free >= DISK_MIN_FREE_BYTES and fraction >= DISK_MIN_FREE_FRACTION:
                continue
            gb = 1024 ** 3
            findings.append(Finding(
                agent=self.name, rule="disk-low", severity=Severity.HIGH,
                title=f"The disk is almost full ({path})",
                summary=(f"{path} has {free / gb:.1f} GB free of {total / gb:.0f} GB ({fraction:.0%}); the warning "
                         "is below 10% or 5 GB. The database stops accepting ticks and trades when it is full."),
                fingerprint=f"health:disk-low:{path}",
                where=f"filesystem {path}",
                evidence=[f"{path}: {free / gb:.1f} GB free of {total / gb:.0f} GB ({fraction:.0%})"],
                suggestion=("Every datum is kept by design, so do not delete data: check last night's "
                            "logs/archive-YYYY-MM-DD.log (the verified Drive archive), that TimescaleDB compression "
                            "is running, and `docker system df`; old logs/engine/*.log can go. If it is simply "
                            "full, grow the EBS volume (it went to 100 GB on 17 Sep)."),
            ))
        return findings

    def _load_rule(self, ctx: SentinelContext, data: dict, now: datetime) -> list[Finding]:
        text = self.read_text("/proc/loadavg")
        if not text:
            return []
        parts = text.split()
        try:
            load1, load5, load15 = float(parts[0]), float(parts[1]), float(parts[2])
        except (IndexError, ValueError):
            return []
        cpus = max(1, int(self.cpu_count() or 1))
        limit = LOAD_PER_CPU * cpus
        clear = LOAD_CLEAR_FRACTION * limit
        # Once reported, it stays until the load is under 80% of the line: the box runs near 100% CPU in the
        # session (config/morning-plan.txt, 27 Sep), and a 5-minute load hovering at the line would otherwise
        # resolve and reopen the incident every few minutes.
        recovering = self._streak_so_far(data, "load-high", now) >= 3 and clear < load5 <= limit
        count, since = self._streak(data, "load-high", load5 > limit or recovering, now)
        if count < 3:
            return []
        evidence = [f"load 1/5/15 min: {load1:.2f} / {load5:.2f} / {load15:.2f} on {cpus} CPU(s)",
                    f"threshold {limit:.1f} (2 × CPUs), above it since {_ist(since, now)}"
                    + (f"; clears below {clear:.1f}" if recovering else "")]
        top = self._top_processes(ctx, "pcpu", "pcpu")
        if top:
            evidence.append("busiest (%CPU): " + "; ".join(top))
        return [Finding(
            agent=self.name, rule="load-high", severity=Severity.MEDIUM,
            title="The server is overloaded",
            summary=(f"The 5-minute load has been above {limit:.1f} for {count} checks since {_ist(since, now)}; "
                     "ticks and orders queue behind whatever is using the CPU."
                     + (f" It is {load5:.2f} now: under the line, but this stays open until it is below "
                        f"{clear:.1f}, so a load hovering at the line is one incident." if recovering else "")),
            fingerprint="health:load-high",
            where="server CPU (/proc/loadavg)",
            evidence=evidence,
            suggestion=("Look at the busiest processes: a backtest or research job belongs on the Mac, not on this "
                        "server (15 Sep); a build during market hours is the desk deploying — it passes."),
        )]

    # -------------------------------------------------------------- the desk

    def _process_table(self, ctx: SentinelContext) -> Optional[list[_Proc]]:
        """Every process with its parent and (on Linux) its session; None when ps cannot be asked."""
        for args, with_sid in ((_PS_WITH_SID, True), (_PS_PLAIN, False)):
            try:
                rc, out = ctx.run(list(args))
            except PermissionError:
                return None
            if rc != 0 or not out.strip():
                continue
            procs: list[_Proc] = []
            width = 5 if with_sid else 4
            for line in out.splitlines():
                parts = line.split(None, width - 1)
                if len(parts) < width or not all(p.isdigit() for p in parts[: width - 2]):
                    continue
                if with_sid:
                    procs.append(_Proc(int(parts[0]), int(parts[1]), int(parts[2]), parts[3], parts[4]))
                else:
                    procs.append(_Proc(int(parts[0]), int(parts[1]), None, parts[2], parts[3]))
            if procs:
                return procs
        return None

    def _desk_rules(self, ctx: SentinelContext, data: dict, now: datetime) -> list[Finding]:
        procs = self._process_table(ctx)
        if not procs:
            return []

        by_pid = {p.pid: p for p in procs}
        modes = {p.pid: mode for p in procs if (mode := _desk_mode(p.args)) is not None}
        children: dict[int, list[_Proc]] = defaultdict(list)
        for p in procs:
            children[p.ppid].append(p)

        # Three kinds of process carry desk.sh's command line without being a loop:
        # * a subshell of the desk ($(...), a ( ... ) group during a build) — its parent is the desk;
        # * api_start's leftover: `( cd … && nohup dotnet run … & )` leaves a bash with the
        #   caller's argv, parented to pid 1, as the parent of `dotnet run` until the API's
        #   next restart — the desk's own restarts left one alive from 11:28 on 24 Sep to 08:45
        #   the next morning;
        # * a --daemon copy that is not its own session leader: spawn_detached calls setsid,
        #   so the real background loop always is (checked where ps reports sessions).
        loops: list[_Proc] = []
        launchers: list[_Proc] = []
        for p in procs:
            mode = modes.get(p.pid)
            if mode not in ("", "--daemon") or p.ppid in modes:
                continue
            if any(_starts_api(c.args) for c in children.get(p.pid, ())):
                launchers.append(p)
                continue
            if mode == "--daemon" and p.sid is not None and p.sid != p.pid:
                continue
            loops.append(p)

        # desk.pid is the desk's own word on which process is the loop (desk.sh writes $$
        # there, in both modes). The strict loop list above stands in when the file cannot
        # be read here, or when it names a pid that died without its trap running.
        pid_text = self._desk_pidfile(ctx)
        named = by_pid.get(int(pid_text)) if pid_text else None
        named_alive = named is not None and modes.get(named.pid) in ("", "--daemon")
        alive = named_alive or bool(loops)

        findings: list[Finding] = []

        count, since = self._streak(data, "desk-down", not alive, now)
        if count >= 2:
            evidence = [f"desk.pid names {pid_text}, which is not a running desk.sh" if pid_text
                        else "no desk.pid found",
                        f"no desk.sh loop among {len(procs)} processes"]
            evidence += [f"pid {p.pid}: carries desk.sh's command line but is the parent of `dotnet run` "
                         "(the API's launcher), not the loop" for p in launchers[:3]]
            evidence.append(f"missing since {_ist(since, now)}")
            findings.append(Finding(
                agent=self.name, rule="desk-down", severity=Severity.HIGH,
                title="The desk supervisor is not running",
                summary=(f"No scripts/desk.sh loop has been running for {count} checks, since {_ist(since, now)}: "
                         "nothing restarts a dead API, deploys from GitHub, or runs market-open.sh at 08:45."),
                fingerprint="health:desk-down",
                where="scripts/desk.sh",
                evidence=evidence[:6],
                suggestion=("Start it with scripts/desk.sh --headless and read the end of logs/desk.log for why it "
                            "stopped; scripts/status.sh shows its state. Leave any bash that is the parent of "
                            "`dotnet run` alone: it is the running API's launcher."),
            ))

        dup, since = self._streak(data, "desk-duplicated", len(loops) > 1, now)
        if dup >= 2:
            evidence = [f"pid {p.pid} up {p.etime}: desk.sh {(_desk_mode(p.args) or '').strip()}".rstrip()
                        for p in loops[:4]]
            if pid_text:
                evidence.append(f"desk.pid names {pid_text}")
            evidence.append(f"doubled since {_ist(since, now)}")
            findings.append(Finding(
                agent=self.name, rule="desk-duplicated", severity=Severity.MEDIUM,
                title="More than one desk supervisor is running",
                summary=(f"{len(loops)} desk.sh loops have been running side by side for {dup} checks, since "
                         f"{_ist(since, now)}; each one deploys and restarts the API on its own."),
                fingerprint="health:desk-duplicated",
                where="scripts/desk.sh",
                evidence=evidence[:6],
                suggestion=("On 24 and 26 Sep the desk was launched 3–4 times within seconds and only one survived. "
                            "Keep the loop whose pid is in desk.pid and stop the other listed loop with "
                            "`kill <pid>` — only a pid listed here, never a bash that is the parent of "
                            "`dotnet run` (that is the API's launcher). Sentinel does not stop processes itself."),
            ))
        return findings

    def _desk_pidfile(self, ctx: SentinelContext) -> Optional[str]:
        for folder in ctx.desk_state_dirs():
            text = self.read_text(str(folder / "desk.pid"))
            if text and text.strip().isdigit():
                return text.strip()
        return None
