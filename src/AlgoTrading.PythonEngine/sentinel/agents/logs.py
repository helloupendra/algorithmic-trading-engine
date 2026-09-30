"""
The logs agent — reads what the desk writes about itself.

Every process on the desk narrates. The API writes ``logs/api.log`` — and
every strategy runner's and feed's output passes through it, one log entry per
line, prefixed ``[strategy:Fulcrum:NIFTY]`` (``:err`` for stderr) or
``[Dhan feed]``. The supervisor writes ``logs/desk.log``; the morning job
writes ``logs/market-open-<date>.log``; the nightly Drive archive (cron, 06:00)
writes only ``logs/archive-<date>.log``; a child whose parent went away keeps
talking into ``logs/engine/<name>-<pid>.log`` (core.safe_output). This agent
tails those files every 30 seconds and turns the lines the desk has already
learned to fear into findings.

Each signature is one of this desk's own failures:

* ``rate-limited`` — 24 Sep: runners died at boot on "429 Too Many Requests"
  from the API's own sign-in; 24–26 Sep: Telegram answered 429 to 394 of the
  desk's own alerts.
* ``runner-crashed`` / ``process-exited`` — runs or a daemon died on its own.
  A run's death is the trading agent's (it reads every run's stop reason from
  the API and groups a mass death into one incident); what the logs add is
  the cause. So a death is reported by its cause — the 429, the Python
  exception, or, when no other rule knows the cause, one ``runner-crashed``
  incident per cause — with the dead runs listed, never one incident per run.
* ``feed-reconnect-loop`` / ``feed-stalled`` — 24 Sep 11:27: the Dhan feed
  reconnected in a loop without a tick and every runner went deaf.
* ``vendor-auth`` — 10 Sep: an expired FYERS token; Dhan's 24-hour tokens.
* ``job-failed`` / ``desk-warning`` / ``desk-relaunched`` — what desk.sh, the
  morning job and the archive say went wrong; desk.sh started several times
  in seconds.
* ``not-configured`` — a setting in .env that never reached the API.
* ``order-rate-limit`` — 25 Sep: Fulcrum churning into the order limit.
* ``python-traceback`` / ``api-error`` — a crash in Python or .NET, grouped by
  what was thrown rather than by where or when.
* ``new-error`` — anything else that looks like an error and has never been
  seen before: reported once, quietly, then remembered.

Three habits keep it quiet. On its first check — and on the first check after
Sentinel was down for more than ten minutes — it starts at the end of every
file: what was written meanwhile is history, learned (its error lines count as
already seen), not reported. A long gap between two checks of one running
Sentinel is not "down" (another agent held the engine): those lines are read
as news. A problem whose lines recur more slowly than the
two-minute resolve window — runners repeat "FEED STILL STALLED" every ten
minutes, a feed waits up to five minutes between attempts — is held open that
long, so one outage is one incident rather than a string of them. And a
one-off line whose consequence lasts — "FAILED: no FYERS sign-in … nothing
was started", a daemon that exited, the archive that failed — is held open for
as long as the consequence does (the session's close, the next archive run),
or until the desk logs the line that ends it (the morning job run again, the
daemon starting, "archive to Drive: ok"): "resolved" two minutes after
"nothing was started" would be a lie.

A held finding says when its newest line was read (``observed_utc``), and a
person resolving its incident ends the hold: those lines never reopen it, only
a line read after the resolve does. On 28 Sep the operator resolved four API
errors of a deliberate Postgres restart (13:05-13:06) at 13:08, and at 13:11
their four-hour holds opened them again as new incidents.

It only reads. A log line is text from outside — a vendor message, a symbol, a
headline — and nothing in it is ever executed or followed. A line that may
carry a credential is dropped before any rule sees it.
"""
from __future__ import annotations

import hashlib
import logging
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

from sentinel.agents.base import Agent
from sentinel.clock import MCX_CLOSE, NSE_CLOSE, ist_date, to_ist
from sentinel.context import SentinelContext
from sentinel.model import Finding, Severity
from sentinel.notify import redact

log = logging.getLogger("sentinel.agents.logs")

AGENT = "logs"

#: At most this much of one file is read in one check. When further behind
#: (Sentinel was stopped, a burst of output) it jumps to the newest part:
#: history is not news.
MAX_READ_BYTES = 2 * 1024 * 1024
#: logs/engine/*.log are watched while they changed within the last day.
ENGINE_WINDOW_SECONDS = 24 * 3600
#: A file's offset is forgotten this long after the file was last listed.
FORGET_FILE_AFTER_SECONDS = 48 * 3600
#: A file first seen while Sentinel is watching (previous check this recent) is
#: new, so it is read from its start — unless it is already this big. When the
#: previous check is older than this (Sentinel was down), what every file
#: gained meanwhile is history: learned, not reported.
WATCHING_SECONDS = 600
#: A longer gap still counts as watching when this same process made the
#: previous check: Sentinel was not down, another agent held the engine. Past
#: this (a machine that slept), it is history after all.
SAME_PROCESS_GAP_SECONDS = 3600
NEW_FILE_MAX_BYTES = 1024 * 1024
#: Error signatures remembered for ``new-error``; the oldest is dropped first.
SEEN_CAP = 2000
EVIDENCE_LINES = 4
EVIDENCE_CHARS = 240

#: A hold of this value lasts until the end of the session: 15:30 IST (the
#: desk stops the NSE and BSE runs then), or 23:30 IST after it (MCX's
#: evening), and at least MIN_EVENT_HOLD_SECONDS.
UNTIL_CLOSE = -1
MIN_EVENT_HOLD_SECONDS = 3600

#: How long a finding stays open after its last line, for rules whose lines
#: recur more slowly than the engine's resolve window (4 checks = 2 minutes).
#: Rules not listed resolve two minutes after their last line: a burst is one
#: incident. A line that announces its own wait ("waiting 300s") is held for
#: that wait plus a margin. Chosen by replaying 24–26 Sep's real logs, where
#: shorter holds turned one bad day into dozens of messages.
HOLD_SECONDS = {
    # A stalled runner repeats itself every 600 s; on 25 Sep the Dhan feed
    # went silent and came back 11 times, 4–53 minutes apart after the first —
    # one flapping feed, one incident, closed after an hour without a stall.
    "feed-stalled": 3600,
    "rate-limited": 900,
    "vendor-auth": 3600,       # a dead token stays dead until someone renews it
    "order-rate-limit": 3600,  # a churning strategy hits the limit in bursts all day
    "desk-warning": 3600,
    # A recurring bug (25 Sep: alert events too long for their column, ~20 a
    # day) is one incident while it keeps recurring, not one per sighting.
    "api-error": 4 * 3600,
    "python-traceback": 4 * 3600,
    "not-configured": 4 * 3600,
    # One-off lines whose consequence lasts (see UNTIL_CLOSE). A daemon that
    # exited is held until it logs a start again, or four hours.
    "job-failed": UNTIL_CLOSE,       # unless its advice below says otherwise
    "runner-crashed": UNTIL_CLOSE,   # a dead run stays dead until someone starts it
    "process-exited": 4 * 3600,
    # 24 and 26 Sep: 3–4 launches in seconds, sometimes in two bursts a few
    # minutes apart — one morning, one incident.
    "desk-relaunched": 3600,
}
#: The archive runs once a day at 06:00 IST: its failure is true until the next run.
ARCHIVE_HOLD_SECONDS = 25 * 3600
WAIT_MARGIN_SECONDS = 90
#: With every market shut, a reconnect carries no ticks because there are none
#: to carry: this many in a row before it is called a loop at all.
CLOSED_MARKET_LOOP = 3
#: With a market open, one tickless reconnect is a hiccup; this many in a row is a loop.
OPEN_MARKET_LOOP = 2
#: A death's runs listed in a finding's summary, and remembered while it is held.
DEATHS_SHOWN = 8
DEATHS_KEPT = 40

# Lines that look like errors and are not problems. Each was found in the
# server's real logs (Sep 2026); the reason is next to it. They silence only the
# generic rules (new-error, python-traceback, api-error, desk-warning,
# not-configured) — a runner dying or a job failing is never muted.
BENIGN_LINES: tuple[re.Pattern[str], ...] = (
    # Every child (runner, feed) talks to the API on localhost:5025. While the
    # API restarts — every auto-deploy, ~20 s — they all print "connection
    # refused" / "Max retries exceeded". "The API is down" belongs to the health
    # agent, which asks the API itself.
    re.compile(r"(?i)(?:localhost|127\.0\.0\.1)(?::|'?,\s*port=)5025\b.*(?:refused|max retries exceeded|"
               r"newconnectionerror|failed to establish|remotedisconnected|connection aborted|read timed out)"),
    # core.safe_output: the API that held a child's stdout went away and the
    # child carries on into logs/engine/. Expected after every API restart.
    re.compile(r"^\[safe_output\] sys\.std(?:out|err) lost"),
    # core.safe_output: a runner's last line, "EXIT code=1 reason=uncaught
    # KeyError: 'ltp'", restates how it ended. The traceback above it and the
    # API's "exited on its own" line are what get reported; this is not news.
    re.compile(r"^EXIT code=-?\d+ reason="),
    # Two processes adding the same symbol race on live_watchlist's unique
    # index; the loser's INSERT fails but the row is there (~200 a day).
    re.compile(r"IX_live_watchlist_Symbol"),
    # With 26 runners the metrics port range runs out; the runner says so and
    # carries on without metrics.
    re.compile(r"Failed to start metrics server .*Continuing without metrics"),
    # dotnet's build summary, and compiler / NuGet / MSBuild warnings that the
    # desk's builds print into desk.log and api.log.
    re.compile(r"^\s*0 (?:Error|Warning)\(s\)\s*$"),
    re.compile(r": warning [A-Z]{2,}\d+:"),
    # vite's list of built chunks: a chunk may be called ErrorBoundary-….js.
    re.compile(r"^dist/"),
    # desk.sh's bookkeeping after a failed deploy; the FAILED line is reported.
    re.compile(r"^deploy record written: failed"),
    # The desk's first failed probe of the API. It restarts the API only after
    # three, and the health agent watches the API directly.
    re.compile(r"API health check failed \(1/\d+\)"),
    # The risk guard refusing a signal at the daily loss limit, or while the
    # kill switch is on, is the rule working (RiskManagementService). On 30 Sep
    # a daily-loss refusal was opened as a medium "API error". The runner's
    # "SIGNAL REFUSED by the API: {"error": …}" line carries the same reason.
    # The order rate limit is not here: its own signature reports the churn.
    re.compile(r"MAX DAILY LOSS EXCEEDED:|GLOBAL KILL SWITCH IS ACTIVE\."),
)

# A line carrying one of these is dropped whole: never matched, never evidence.
# notify.redact masks what it recognises, but a log can hold a secret in a
# shape it does not know (api.log prints the Telegram URL, bot token and all).
#
# The desk's one spec of "carries a secret" (28 Sep 2026) — the same six
# patterns as notify.redact, the API's IncidentRedaction and the console's
# maskSecrets: a line is dropped exactly when the redactor would mask part of
# it (tests/test_sentinel_logs.py holds the two to each other). Until then this
# was a list of markers that dropped any line saying "password" — "the broker
# password was refused" never reached a rule — and let "DHAN_PIN=…" through.
_SECRET_PATTERNS = (
    # 1. Authorization: Bearer|Basic <value>
    re.compile(r"(?i)authorization[\"']?[ \t]*[:=][ \t]*[\"']?(?:bearer|basic)[ \t]+[^\s\"'&,;]+"),
    # 2. Bearer <12+ token characters> anywhere (a header quoted without its name)
    re.compile(r"(?i)\bbearer[ \t]+[A-Za-z0-9._~+/=-]{12,}"),
    # 3. scheme://user:password@ and scheme://:password@
    re.compile(r"(?i)\b[a-z][a-z0-9+.-]*://[^\s:/@]*:[^\s@/]+(?=@)"),
    # 4. key=value (_KEY_VALUE, below)
    # 5. a Telegram bot token, /bot<token>/ in a URL included
    re.compile(r"(?<![0-9])[0-9]{8,10}:[A-Za-z0-9_-]{30,}"),
    # 6. a JWT: eyJ….eyJ….<signature>
    re.compile(r"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"),
)
# 4. key=value / key: value / "key": "value", where a whole part of the key (underscore separated, or the end of
#    a camelCase key) names a secret — FYERS_SECRET_KEY=, POSTGRES_PASSWORD=, DHAN_PIN=, accessToken:, X-Api-Key:
#    — then an optional quote, spaces or tabs (never a newline) and ':' or '=' (not '=='). No leading word
#    boundary. Prose ("tokens", "spinning", "Skipping:") passes. The costliest of the six, so it is tried only on
#    a line that names a secret at all (_KEY_WORD): most lines do not.
_KEY_VALUE = re.compile(r"(?i)(?<![A-Za-z0-9_])[A-Za-z0-9_]{0,64}?"
                        r"(?:secret|password|passwd|pwd|token|api[_-]?key|private[_-]?key|totp|pin)"
                        r"(?:_[A-Za-z0-9]{1,32}){0,8}[\"']?[ \t]*[:=](?!=)[ \t]*"
                        r"(?:\"[^\"\r\n]+\"|'[^'\r\n]+'|[\"']?[^\s\"'&,;]+)")
_KEY_WORD = re.compile(r"(?i)secret|passw|pwd|token|api[_-]?key|private[_-]?key|totp|pin")
#: Only the start of a line is judged: nothing past the first few hundred
#: characters of a line is ever shown, and a pattern must not read a megabyte.
SECRET_SCAN_CHARS = 4000

# --- shapes of the files -----------------------------------------------------
_DOTNET_HEADER = re.compile(r"^(?P<level>trce|dbug|info|warn|fail|crit): (?P<category>[^\s\[]+)\[-?\d+\]\s*$")
_DOTNET_BODY = "      "  # the console logger indents a message by six spaces
_RUNNER_PREFIX = re.compile(r"^\[strategy:(?P<name>[^:\]]+):(?P<ul>[^:\]]+)(?P<err>:err)?\] ?(?P<text>.*)$")
_DAEMON_PREFIX = re.compile(r"^\[(?P<label>[A-Za-z][A-Za-z0-9 ._-]{1,30}?)(?P<err>:err)?\] ?(?P<text>.*)$")
_DAEMON_LABELS = ("ingestor", "chain poller", "notifier")  # and anything called "… feed"
_INNER_VENDOR = re.compile(r"^\[(?P<vendor>dhan|fyers|truedata|angel)\]", re.I)
# desk.sh and its jobs stamp each line "2026-09-28 08:45:03  text" (say() in
# scripts/lib/desk-common.sh) since 28 Sep 2026, and "08:45:03  text" before
# that; both are read, and "time" is the clock part either way.
_DESK_LINE = re.compile(r"^(?:\d{4}-\d{2}-\d{2}[ T])?(?P<time>\d{2}:\d{2}:\d{2})\s+(?P<text>.*)$")
# The API's run console ("09:15:01 | …") and, from 28 Sep, a runner's own log,
# which stamps every line the same way with a full UTC time
# ("2026-09-28T03:45:01.123Z ! …"). "!" is stderr: a traceback is assembled
# from its own stream, not from whatever stdout printed in between.
_REGISTRY_LINE = re.compile(r"^(?:\d{4}-\d{2}-\d{2}T)?\d{2}:\d{2}:\d{2}(?:\.\d+)?Z? (?P<stream>[|!]) (?P<text>.*)$")
_RUNNER_FILE = re.compile(r"^runner-(?P<run>\d+)-\d+\.log$")
_BACKTEST_FILE = re.compile(r"^backtest-(?P<run>\d+)-\d+\.log$")
_FEED_FILE = re.compile(r"^(?P<vendor>[a-z]+)-feed-\d+\.log$")
_INGESTOR_FILE = re.compile(r"^ingestor-\d+\.log$")

# --- signatures -------------------------------------------------------------
_ERRORISH = re.compile(r"(?i)error|exception|failed")
_RUNNER_EXIT = re.compile(r"(?i)\brunner exited (?:with code |\(code )(?P<code>-?\d+)")
_RUNNER_CAUSE = re.compile(r"(?i)runner exited \(code -?\d+\):\s*(?P<cause>.+?)(?:\s+\(by [^)]*\))?\s*$")
_STRATEGY_RUN = re.compile(r"Strategy \d+ \((?P<name>[^)]+)\) run (?P<run>\d+) on (?P<ul>\S+)")
_RUN_ID = re.compile(r"\brun #?(?P<run>\d+)\b")
#: "429 Client Error: Too Many Requests" (requests), and the bare forms
#: "Telegram refused a message: HTTP 429." (TelegramSender) and "status code 429".
_TOO_MANY = re.compile(r"(?i)\b429\b.*too many requests|too many requests.*\b429\b|\bHTTP 429\b"
                       r"|\bstatus(?: ?code)?:? 429\b")
_VENDOR_THROTTLE = re.compile(r"(?i)rate-limiting us")
_TELEGRAM_REFUSED = re.compile(r"(?i)\btelegram\b.*\b(?:refused|429)\b")
_URL_PATH = re.compile(r"https?://[^/\s]+(?P<path>/[^\s?\"')]*)")
#: The exception the API's risk guard refuses an order with (answered 409).
_RISK_REFUSAL = "RiskViolationException"
_ORDER_LIMIT = re.compile(r"RATE LIMIT EXCEEDED: More than (?P<n>\d+) orders placed in the last minute for run (?P<run>\d+)")
_TICKLESS = re.compile(r"(?P<n>\d+) reconnect\(s\) carried no ticks")
_HOST_LOST = re.compile(r"Connection to remote host was lost")
_WAITING = re.compile(r"\bwaiting (?P<wait>\d+)\s?s\b")
_FEED_STALL = re.compile(r"FEED (?:STILL )?STALLED")
_FEED_RECOVERED = re.compile(r"(?i)\bFEED RECOVERED\b")
_BRACKET_UL = re.compile(r"\[(?P<ul>[A-Z][A-Z0-9&_-]{1,20})\]\s*FEED")
_VENDOR_AUTH = re.compile(
    r"(?i)token (?:is )?(?:expired|invalid)|(?:invalid|expired) (?:access )?token|rejected the access token"
    r"|credential was rejected|refused the login|authentication failed|invalid_authentication"
    r"|holds the token \w+ rejected|\b401\b.*unauthori[sz]ed|unauthori[sz]ed.*\b401\b")
_LOCALHOST = re.compile(r"(?i)localhost|127\.0\.0\.1")
_FAILED_WORD = re.compile(r"\bFAILED\b")
_DESK_START = re.compile(r"^=== desk started \(pid \d+")
_NOT_CONFIGURED = re.compile(r"(?i)\bnot configured\b")
_DAEMON_EXIT = re.compile(r"^(?P<name>[A-Za-z][\w .-]*?) pid \d+ exited with code (?P<code>-?\d+)")
#: A supervised daemon's first words ("[Dhan feed] [dhan] STARTING LIVE FEED (live) — …",
#: "[ingestor] TICK FLUSHERS started [fyers] — …"): it is running again.
_DAEMON_STARTED = re.compile(r"STARTING LIVE FEED|TICK FLUSHERS started|^(?:\[\w+\]\s*)?(?i:starting)\b")
#: Exit codes of a process that was told to stop (SIGINT, SIGKILL after an
#: ignored SIGTERM, SIGTERM): not a crash.
_STOP_EXIT_CODES = {0, 130, 137, 143}
_UNHANDLED = re.compile(r"^Unhandled exception\b")
_STACK_FRAME = re.compile(r"^\s+(?:at |--- End of )")
_EXCEPTION_LINE = re.compile(
    r"^(?:---> )?(?P<type>[A-Za-z_][\w.`]*?(?:Exception|Error|Exit|Interrupt|Failure))(?:\s*\([^)]*\))?:\s*(?P<msg>.*)$")
_EXCEPTION_TYPE = re.compile(r"^(?P<type>[A-Za-z_][\w.]*)(?::|$)")
#: What can close a traceback: "KeyError: 'ltp'", "requests.exceptions.HTTPError: …",
#: "KeyboardInterrupt". Anything else (stdout interleaved into stderr in an
#: engine log, "[dhan] …") is an ordinary line and the traceback stays open.
_EXCEPTION_CLOSER = re.compile(r"^[A-Za-z_][\w.]*(?::(?:\s|$)|$)")
#: Tracebacks of a process being stopped on purpose.
_QUIET_EXCEPTIONS = re.compile(r"^(?:KeyboardInterrupt|SystemExit)\b")
_CHAIN_PHRASES = ("The above exception was the direct cause of the following exception:",
                  "During handling of the above exception, another exception occurred:")
#: EF Core logs a failed command's SQL, and a failed connection's server, in
#: entries of their own next to the one carrying the exception; the exception
#: is reported from its own entry, once.
_COMPANION_CATEGORIES = ("Microsoft.EntityFrameworkCore.Database.Command",
                         "Microsoft.EntityFrameworkCore.Database.Connection")

# --- normalising a line into a signature ------------------------------------
_TIMESTAMP = re.compile(
    r"^(?:\[?\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?\]?"
    r"|\d{2}:\d{2}:\d{2}(?:[.,]\d+)?)(?:\s+[|!](?=\s))?\s+")
_GUID = re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
_HEX = re.compile(r"\b0x[0-9a-fA-F]+\b|\b(?=[0-9a-f]*[a-f])(?=[0-9a-f]*\d)[0-9a-f]{7,64}\b")
_MIXED_ID = re.compile(r"\b(?=\w*[A-Za-z])(?=\w*\d)\w{10,}\b")
_NUMBER = re.compile(r"\d+(?:\.\d+)?")
_SPACES = re.compile(r"\s+")


def normalise(line: str) -> str:
    """
    A line reduced to its shape: timestamps gone, ids and hex as ``*``, numbers
    as ``#``. "runner exited with code 1" from run 215 and from run 216 is the
    same signature.
    """
    text = _TIMESTAMP.sub("", line.strip())
    text = _GUID.sub("*", text)
    text = _HEX.sub("*", text)
    text = _MIXED_ID.sub("*", text)
    text = _NUMBER.sub("#", text)
    return _SPACES.sub(" ", text).strip()[:200]


def _digest(signature: str) -> str:
    return hashlib.sha1(signature.encode("utf-8", "replace")).hexdigest()[:12]


def _may_hold_secret(line: str) -> bool:
    head = line[:SECRET_SCAN_CHARS]
    return any(p.search(head) for p in _SECRET_PATTERNS) or \
        (_KEY_WORD.search(head) is not None and _KEY_VALUE.search(head) is not None)


def _benign(line: str) -> bool:
    return any(p.search(line) for p in BENIGN_LINES)


def _trim(text: str, limit: int = EVIDENCE_CHARS) -> str:
    text = text.strip()
    return text if len(text) <= limit else text[: limit - 1] + "…"


def _short_type(exception_line: str) -> str:
    match = _EXCEPTION_LINE.match(exception_line.strip()) or _EXCEPTION_TYPE.match(exception_line.strip())
    return match.group("type").rsplit(".", 1)[-1] if match else "error"


# --- what the parsers hand the rules ----------------------------------------
@dataclass(frozen=True)
class _LogFile:
    path: Path
    label: str               # relative to logs/: "api.log", "engine/runner-215-2121057.log"
    kind: str                # api | desk | job | runner | feed | engine
    component: str
    run_id: Optional[str] = None
    vendor: Optional[str] = None
    backtest: bool = False


@dataclass(frozen=True)
class _Origin:
    """Who said a line: the file, and the process whose output it is."""

    file: str
    kind: str                # api | desk | job | runner | feed | daemon | engine
    component: str           # "Fulcrum on NIFTY", "Dhan feed", "desk.sh", "API"
    vendor: Optional[str] = None
    run_id: Optional[str] = None
    underlying: Optional[str] = None
    backtest: bool = False

    @property
    def where(self) -> str:
        return f"logs/{self.file}" + (f" · {self.component}" if self.component else "")


@dataclass
class _Trace:
    origin: _Origin
    head: str
    last_frame: str = ""
    exc: Optional[str] = None
    exc_raw: Optional[str] = None
    withheld: bool = False


class _Stream:
    """One process's output within a file, where a traceback is put back together."""

    __slots__ = ("trace", "done", "chained", "touched")

    def __init__(self) -> None:
        self.trace: Optional[_Trace] = None   # collecting frames
        self.done: Optional[_Trace] = None    # complete; a chained one may follow
        self.chained = False
        self.touched = 0


@dataclass
class _Block:
    """One entry of the .NET console logger: ``level: Category[id]`` and its body."""

    level: str
    category: str
    lines: list[str] = field(default_factory=list)
    touched: int = 0


@dataclass
class _Hit:
    rule: str
    severity: Severity
    title: str
    what: str
    where: str
    suggestion: str
    hold: float = 0.0
    lines: list[str] = field(default_factory=list)
    files: list[str] = field(default_factory=list)
    subjects: list[str] = field(default_factory=list)
    count: int = 0
    first_time: Optional[str] = None   # HH:MM IST — the log's own time when it has one
    last_time: Optional[str] = None
    key: str = ""                      # what a clearing line is matched against
    deaths: list[str] = field(default_factory=list)   # runs this cause killed: "run 215 (Fulcrum on NIFTY)"
    seq: int = 0                       # when it was last added, against a clearing line's
    loop: int = 0                      # feed-reconnect-loop: the most reconnects in a row one line reported

    def add_death(self, label: str) -> None:
        if label not in self.deaths:
            self.deaths.append(label)


@dataclass(frozen=True)
class _Death:
    """A run that died on its own, as the API tells it: "Strategy 1 (Fulcrum) run 215 on NIFTY exited on its own:
    Runner exited (code 1): <its last stderr line>"."""

    run: Optional[str]
    component: str
    code: int
    cause: str

    @property
    def label(self) -> str:
        if not self.run:
            return self.component
        return f"run {self.run}" + ("" if self.component == f"run {self.run}" else f" ({self.component})")

    def origin(self, o: _Origin) -> _Origin:
        return _Origin(o.file, "runner", self.component, run_id=self.run)

    @staticmethod
    def of(o: _Origin, text: str, code: int) -> "_Death":
        strategy = _STRATEGY_RUN.search(text)
        run_match = _RUN_ID.search(text)
        run = (strategy["run"] if strategy else None) or (run_match["run"] if run_match else None) or o.run_id
        component = f"{strategy['name']} on {strategy['ul']}" if strategy else o.component
        cause_match = _RUNNER_CAUSE.search(text)
        cause = cause_match["cause"].strip() if cause_match else ""
        return _Death(run, component, code, "" if _may_hold_secret(cause) else cause)


class _Scan:
    """What one check saw, before it becomes findings."""

    def __init__(self, seen: dict[str, None], now_hm: str = "", learn: bool = False, now_ts: float = 0.0) -> None:
        self.hits: dict[str, _Hit] = {}
        self.now_hm = now_hm
        self.now_ts = now_ts
        self.seen = seen
        self.learn = learn
        self.lost: dict[str, list[tuple[str, str]]] = {}          # vendor -> [(raw, file)]
        self.stall_events: list[tuple[str, bool]] = []            # (underlying, stalled?)
        self.desk_starts: list[tuple[str, str]] = []              # (HH:MM:SS, raw)
        #: Error lines of a job file (archive, market-open) that no rule claimed:
        #: evidence of that file's FAILED line when it has one, new errors otherwise.
        self.context: list[tuple[_Origin, str, str, Optional[str]]] = []
        #: (seq, rules, pattern): a line that ends what matched pattern under those rules.
        self.clears: list[tuple[int, frozenset[str], re.Pattern[str]]] = []
        self.last_fp: Optional[str] = None
        self._seq = 0

    def hold(self, rule: str, text: str = "", base: Optional[float] = None) -> float:
        """How long a finding stays open after this line: its rule's hold, a wait it announces, or the close."""
        hold = float(HOLD_SECONDS.get(rule, 0) if base is None else base)
        if hold == UNTIL_CLOSE:
            hold = _until_close(self.now_ts)
        wait = _WAITING.search(text)
        if wait:
            hold = max(hold, int(wait["wait"]) + WAIT_MARGIN_SECONDS)
        return hold

    def clear(self, rules: frozenset[str], pattern: re.Pattern[str]) -> None:
        if not self.learn:
            self._seq += 1
            self.clears.append((self._seq, rules, pattern))

    def add(self, fingerprint: str, rule: str, severity: Severity, title: str, what: str, where: str,
            suggestion: str, raw: Optional[str], file: str, hold: float = 0.0,
            line_time: Optional[str] = None, subject: Optional[str] = None, key: str = "") -> None:
        if self.learn:
            return
        self.last_fp = fingerprint
        self._seq += 1
        hit = self.hits.get(fingerprint)
        if hit is None:
            hit = self.hits[fingerprint] = _Hit(rule, severity, title, what, where, suggestion, key=key)
        elif severity.rank > hit.severity.rank:
            hit.severity = severity
        hit.seq = self._seq
        hit.count += 1
        hit.hold = max(hit.hold, hold)
        if raw and not _may_hold_secret(raw):
            line = _trim(raw)
            if line not in hit.lines and len(hit.lines) < EVIDENCE_LINES:
                hit.lines.append(line)
        if file not in hit.files:
            hit.files.append(file)
        if subject and subject not in hit.subjects:
            hit.subjects.append(subject)
        moment = line_time[:5] if line_time else self.now_hm
        if hit.first_time is None:
            hit.first_time = moment
        hit.last_time = moment

    def remember(self, signature: str) -> bool:
        """True when the signature is new (and now remembered)."""
        if signature in self.seen:
            return False
        self.seen[signature] = None
        while len(self.seen) > SEEN_CAP:
            del self.seen[next(iter(self.seen))]
        return True


# --- advice from the desk's own history ---------------------------------------
_LOGIN_LIMIT_ADVICE = ("24 Sep: runners died at boot because the API's sign-in limiter answered 429 to the desk's "
                       "own processes. The limiter must let local sign-ins through (fixed in d70d1c9); if this is "
                       "back, check the sign-in rate-limit policy, then start the refused runs again.")
_NOT_CONFIGURED_ADVICE = ("A key added to .env does nothing until appsettings.Local.json is regenerated "
                          "(python3 scripts/_gen_local_settings.py — desk.sh does it on every deploy) and the API "
                          "restarted; on 21 Sep a broker key sat in .env for an hour while the console said "
                          "\"not configured\".")
_FEED_SWITCH_ADVICE = ("24 Sep 11:27 looked like this and recovery was by hand: stop the Dhan feed and start FYERS "
                       "(Data → Feeds, or POST /api/Feeds/dhan/stop then /api/Feeds/fyers/start).")
_ARCHIVE_FAILED = re.compile(r"(?i)archive to Drive FAILED")
_MORNING_FAILED = re.compile(r"no FYERS sign-in|nothing was started|market-open\.sh exited non-zero"
                             r"|Dhan feed delivered no prices")
_DEPLOY_BLOCKED = re.compile(r"(?i)build FAILED|dotnet build failed|uncommitted changes|diverged|git pull failed"
                             r"|git fetch failed")
_API_DOWN = re.compile(r"(?i)restart FAILED|did not come up|API health check failed")
#: (pattern, advice, hold): what desk.sh, the morning job and the archive say
#: went wrong, the fix from this desk's history, and how long it stays true
#: (None: the rule's own hold).
_DESK_ADVICE: tuple[tuple[re.Pattern[str], str, Optional[float]], ...] = (
    # Re-running the morning job is advice for before the open only: it restarts the API and
    # the feeds under every live run, and from 28 Sep it refuses to after 09:15 while any is.
    (re.compile(r"no FYERS sign-in"),
     "Nothing was started this morning. Sign in to FYERS from the Broker page; before 09:15, run "
     "scripts/market-open.sh again — after the open, start the runs from Strategies → Live runner in the console.",
     UNTIL_CLOSE),
    (_ARCHIVE_FAILED,
     "Nothing was deleted. Read logs/archive-<date>.log; \"couldn't find root directory ID\" means rclone's Drive "
     "authorisation needs renewing (drive.file access is per OAuth client).",
     ARCHIVE_HOLD_SECONDS),
    (re.compile(r"(?i)build FAILED|dotnet build failed"),
     "The previous build keeps running. Read desk.log for the compiler error and push a fix; the desk redeploys "
     "from GitHub within about 24 s.",
     4 * 3600),
    (re.compile(r"(?i)restart FAILED|did not come up"),
     "The API did not come back after a restart. Read the newest lines of logs/api.log and run scripts/status.sh.",
     3600),
    (re.compile(r"Dhan token ends at"),
     "The Dhan feed stops when its token ends. Generate a new Dhan token before then, or plan the switch to FYERS "
     "(Data → Feeds).",
     UNTIL_CLOSE),
    (re.compile(r"no Dhan sign-in"),
     "FYERS is today's feed. Generate a new Dhan token before tomorrow's 08:45 start.",
     UNTIL_CLOSE),
    (re.compile(r"Dhan feed delivered no prices"),
     "The morning job switched to FYERS. Check the Dhan token and Data → Feeds.",
     UNTIL_CLOSE),
    (re.compile(r"uncommitted changes|diverged|git pull failed|git fetch failed"),
     "Auto-deploy is paused until the server's checkout is clean and on origin/main; resolve it by hand on the "
     "server.",
     4 * 3600),
    (re.compile(r"appsettings\.Local\.json"), _NOT_CONFIGURED_ADVICE, 4 * 3600),
    (re.compile(r"market-open\.sh exited non-zero"),
     "Read that day's logs/market-open-<date>.log from the top; the morning plan may be only partly started — "
     "start what is missing from Strategies → Live runner in the console.",
     UNTIL_CLOSE),
    (re.compile(r"market-close\.sh exited non-zero"),
     "Read that day's logs/market-close-<date>.log from the top: a run or feed it could not stop may still be "
     "running (scripts/status.sh).",
     UNTIL_CLOSE),
    (re.compile(r"API health check failed"),
     "The desk restarts the API after three failed checks. If this repeats, read the newest lines of logs/api.log.",
     None),
    (re.compile(r"lost permission to read the repo"),
     "The desk exited; its keepalive reopens it within 10 minutes.",
     None),
)

#: Lines of desk.log / a job's log that end what an earlier line reported:
#: (the line, the rules it ends, what the ended finding's line said).
_DESK_RULES = frozenset({"job-failed", "desk-warning"})
_CLEARING: tuple[tuple[re.Pattern[str], frozenset[str], re.Pattern[str]], ...] = (
    # The morning job was run again (by hand, after signing in): what its last
    # run said went wrong is superseded; if it fails again it says so again.
    (re.compile(r"^=== market-open:"), _DESK_RULES, _MORNING_FAILED),
    # Tomorrow's archive worked.
    (re.compile(r"^archive to Drive: ok\b"), _DESK_RULES, _ARCHIVE_FAILED),
    # A clean deploy ("deploy: 92ddca5 at 00:13 — nothing to rebuild"): the
    # checkout is pulled and the builds pass again.
    (re.compile(r"^deploy: \S+ at \d{2}:\d{2} — (?!.*FAILED)"), _DESK_RULES, _DEPLOY_BLOCKED),
    # The API answers again ("    API up" after every start).
    (re.compile(r"^API up\b"), _DESK_RULES, _API_DOWN),
)


def _desk_advice(text: str, default: str) -> tuple[str, Optional[float]]:
    for pattern, advice, hold in _DESK_ADVICE:
        if pattern.search(text):
            return advice, hold
    return default, None


def _vendor_advice(vendor: str) -> str:
    if vendor == "dhan":
        return ("A Dhan console token lasts 24 hours: generate a new one on the Broker page, then restart the Dhan "
                "feed — or switch to FYERS for the day (Data → Feeds).")
    if vendor == "fyers":
        return ("10 Sep: an expired FYERS token was taken for \"authenticated\" and strategies ran deaf all morning. "
                "Sign in to FYERS again from the Broker page (FYERS has disabled refresh), then restart the feed.")
    return "Renew this vendor's credential on the Broker page, then restart its feed."


def _api_error_advice(root: str) -> str:
    low = root.lower()
    if "too many clients" in low:
        return ("Postgres ran out of connections: every runner and daemon holds its own pool. Stop idle runs or "
                "raise max_connections in the TimescaleDB container, then restart the API.")
    if "value too long for type" in low:
        return ("A text was longer than its column and the row was not saved. Truncate it before saving or widen "
                "the column with an EF migration.")
    if "connection reset" in low or "transient failure" in low:
        return ("The database connection dropped. If it repeats, check the TimescaleDB container (docker ps) and "
                "the server's free memory.")
    return ("Find this line in logs/api.log: the category under 'fail:' names the component, and the lines below "
            "it are the stack.")


def _traceback_advice(exception: str) -> str:
    if "/api/Simulator/signals" in exception and "409" in exception:
        return ("The API refused a signal (409) — on 25 Sep that was the run's order rate limit while Fulcrum "
                "churned. Look for the API's reason (RATE LIMIT EXCEEDED, run closed) in logs/api.log.")
    return "Open the file in 'where' at the traceback: its last frame is where it failed."


# --- the agent ----------------------------------------------------------------
class LogsAgent(Agent):
    name = AGENT
    interval_seconds = 30
    resolve_after = 4  # two minutes of quiet: a burst of lines is one incident

    def __init__(self) -> None:
        # In memory only: what a file's lines were in the middle of (a .NET
        # entry, a traceback). Losing it on restart loses at most one partial
        # traceback.
        self._streams: dict[str, _Stream] = {}
        self._blocks: dict[str, Optional[_Block]] = {}
        self._check_no = 0
        self._last_check: Optional[float] = None   # when this process last checked (state's last_check, if ours)

    # -- the check -----------------------------------------------------------
    def check(self, ctx: SentinelContext) -> list[Finding]:
        self._check_no += 1
        state = ctx.state(AGENT)
        now = ctx.now()
        now_ts = now.timestamp()
        data = state.data
        files_state: dict[str, dict[str, Any]] = data.setdefault("files", {})
        seen = dict.fromkeys(data.get("seen") or [])
        last_check = data.get("last_check")
        gap = now_ts - last_check if isinstance(last_check, (int, float)) else None
        watching = gap is not None and 0 <= gap <= WATCHING_SECONDS
        if not watching and gap is not None and last_check == self._last_check \
                and 0 <= gap <= SAME_PROCESS_GAP_SECONDS:
            # This process made the previous check, so Sentinel was not down: another agent held the
            # single-threaded engine (a dependency scan could take 13 minutes before 28 Sep). What was
            # written meanwhile happened while Sentinel was running — it is news, late, not history.
            watching = True

        scan = _Scan(seen, now_hm=to_ist(now).strftime("%H:%M"), now_ts=now_ts)
        listed: set[str] = set()
        try:
            for log_file in self._log_files(ctx, now_ts):
                listed.add(log_file.label)
                try:
                    lines, history = self._new_lines(ctx, log_file, files_state, watching, now_ts)
                except OSError as exc:
                    log.debug("cannot read %s: %s", log_file.label, exc)
                    continue
                if history:
                    self._learn(log_file, history, seen)
                if lines:
                    self._parse(log_file, lines, scan)
            self._flush(scan)
            findings = self._findings(ctx, scan, data, now_ts)
        finally:
            for label in list(files_state):
                entry = files_state[label]
                if label not in listed and now_ts - float(entry.get("listed", 0)) > FORGET_FILE_AFTER_SECONDS:
                    del files_state[label]
            data["seen"] = list(seen)
            data["last_check"] = now_ts
            self._last_check = now_ts
            state.save()
        return findings

    def let_go(self, ctx: SentinelContext, fingerprints: set[str]) -> None:
        """
        A person resolved these while they were held: the hold is over. A line
        read later opens the problem afresh — counted from that line, not from
        the lines of the episode that was resolved.
        """
        state = ctx.state(AGENT)
        active = state.data.get("active")
        if not isinstance(active, dict):
            return
        dropped = [fp for fp in fingerprints if active.pop(fp, None) is not None]
        if dropped:
            log.info("no longer holding %s: resolved after its last line", ", ".join(sorted(dropped)))
            state.save()

    # -- which files, and what is new in them ---------------------------------
    def _log_files(self, ctx: SentinelContext, now_ts: float) -> list[_LogFile]:
        logs = ctx.logs_dir
        found: list[_LogFile] = []
        today = ist_date(ctx.now())  # the server's clock is IST, and so are these file names
        fixed = (("api.log", "api", "API"), ("desk.log", "desk", "desk.sh"),
                 (f"market-open-{today}.log", "job", "market-open.sh"),
                 # cron, 06:00, output to /dev/null: this file is the only place
                 # its failure is written (22–26 Sep: failed every day, unread).
                 (f"archive-{today}.log", "job", "archive-to-drive.sh"))
        for name, kind, component in fixed:
            path = logs / name
            if path.is_file():
                found.append(_LogFile(path, name, kind, component))
        engine = logs / "engine"
        try:
            candidates = sorted(engine.glob("*.log"))
        except OSError:
            candidates = []
        for path in candidates:
            if "sentinel" in path.name:  # never read our own words back
                continue
            try:
                if now_ts - path.stat().st_mtime > ENGINE_WINDOW_SECONDS:
                    continue
            except OSError:
                continue
            found.append(self._classify_engine(path))
        return found

    @staticmethod
    def _classify_engine(path: Path) -> _LogFile:
        label = f"engine/{path.name}"
        if m := _RUNNER_FILE.match(path.name):
            return _LogFile(path, label, "runner", f"run {m['run']}", run_id=m["run"])
        if m := _BACKTEST_FILE.match(path.name):
            return _LogFile(path, label, "engine", f"backtest {m['run']}", run_id=m["run"], backtest=True)
        if m := _FEED_FILE.match(path.name):
            vendor = m["vendor"]
            return _LogFile(path, label, "feed", f"{vendor.capitalize()} feed", vendor=vendor)
        if _INGESTOR_FILE.match(path.name):
            return _LogFile(path, label, "feed", "ingestor")
        return _LogFile(path, label, "engine", path.stem.rsplit("-", 1)[0])

    def _new_lines(self, ctx: SentinelContext, f: _LogFile, files_state: dict[str, dict[str, Any]],
                   watching: bool, now_ts: float) -> tuple[list[str], list[str]]:
        """
        (lines to check, lines only to learn from) — complete lines only. When
        Sentinel was not watching (its previous check is missing or older than
        WATCHING_SECONDS), everything is the second kind: a line written while
        it was down is history by now, and reporting it as news at the time of
        reading would be wrong about both the when and the whether.
        """
        st = f.path.stat()
        entry = files_state.get(f.label)
        lines: list[str] = []
        if entry is None:
            if not watching or st.st_size > NEW_FILE_MAX_BYTES:
                # First sight: start at the end. The tail is history — learned, not reported.
                history, _ = _read_lines(f.path, max(0, st.st_size - MAX_READ_BYTES), st.st_size,
                                         mid_line=st.st_size > MAX_READ_BYTES)
                files_state[f.label] = {"ino": st.st_ino, "off": st.st_size, "listed": now_ts}
                return [], history
            start = 0  # it appeared while we were watching: all of it is new
        elif entry.get("ino") != st.st_ino:
            # Rotated (api.log becomes api-until-….log on every API restart):
            # finish the old file first, then read the new one from its start.
            if f.kind == "api":
                lines += self._rotated_remainder(ctx, entry)
            start = 0
        elif st.st_size < int(entry.get("off", 0)):
            self._forget_parse_state(f.label)
            start = 0  # truncated
        else:
            start = int(entry.get("off", 0))
        fresh, end = _read_lines(f.path, start, st.st_size)
        files_state[f.label] = {"ino": st.st_ino, "off": end, "listed": now_ts}
        if not watching:
            return [], lines + fresh
        return lines + fresh, []

    @staticmethod
    def _rotated_remainder(ctx: SentinelContext, entry: dict[str, Any]) -> list[str]:
        try:
            rotated = sorted(ctx.logs_dir.glob("api-until-*.log"), key=lambda p: p.stat().st_mtime, reverse=True)
        except OSError:
            return []
        for path in rotated[:5]:
            try:
                st = path.stat()
            except OSError:
                continue
            if st.st_ino == entry.get("ino"):
                start = int(entry.get("off", 0))
                if st.st_size > start:
                    return _read_lines(path, start, st.st_size)[0]
                return []
        return []

    def _forget_parse_state(self, label: str) -> None:
        self._blocks.pop(label, None)
        for key in [k for k in self._streams if k.startswith(label + "|")]:
            del self._streams[key]

    def _learn(self, f: _LogFile, history: list[str], seen: dict[str, None]) -> None:
        """Run a file's tail through the rules only to remember its error lines as seen."""
        scan = _Scan(seen, learn=True)
        self._parse(f, history, scan)
        self._flush(scan, only=f.label, force=True)
        self._forget_parse_state(f.label)

    # -- parsing ------------------------------------------------------------
    def _parse(self, f: _LogFile, lines: list[str], scan: _Scan) -> None:
        if f.kind == "api":
            self._parse_api(f, lines, scan)
            return
        base = _Origin(f.label, f.kind, f.component, vendor=f.vendor, run_id=f.run_id, backtest=f.backtest)
        for raw in lines:
            line_time: Optional[str] = None
            text = raw
            stream = f.label + "|out"
            if f.kind in ("desk", "job"):
                m = _DESK_LINE.match(raw)
                if m:
                    line_time, text = m["time"], m["text"]
                else:
                    text = raw.strip()
            else:
                m = _REGISTRY_LINE.match(raw)
                if m:
                    text = m["text"]
                    stream = f.label + ("|err" if m["stream"] == "!" else "|out")
                elif raw[:1].isdigit():
                    text = _TIMESTAMP.sub("", raw, count=1)
            origin = base
            if f.kind == "feed" and not f.vendor:
                inner = _INNER_VENDOR.match(text.strip())
                if inner:
                    origin = _Origin(f.label, f.kind, f.component, vendor=inner["vendor"].lower())
            self._stream_line(stream, origin, text, raw, scan, line_time)

    def _parse_api(self, f: _LogFile, lines: list[str], scan: _Scan) -> None:
        for raw in lines:
            header = _DOTNET_HEADER.match(raw)
            if header:
                self._finish_block(f.label, scan)
                self._blocks[f.label] = _Block(header["level"], header["category"], touched=self._check_no)
                continue
            block = self._blocks.get(f.label)
            if raw.startswith(_DOTNET_BODY):
                text = raw[len(_DOTNET_BODY):]
                if block is None:  # its header was never read (the first check started mid-entry)
                    self._api_message(f, "info", "", text, scan)
                    continue
                block.lines.append(text)
                block.touched = self._check_no
                if block.level not in ("fail", "crit") and len(block.lines) == 1:
                    self._api_message(f, block.level, block.category, text, scan)
                continue
            # Column 0: output that did not go through the logger — build
            # warnings before start-up, or the runtime's last words.
            self._finish_block(f.label, scan)
            self._api_raw(f, raw, scan)
        # An entry with a long stack can reach the file in more than one write,
        # so the last one stays open until the next header or a quiet check.

    def _api_message(self, f: _LogFile, level: str, category: str, text: str, scan: _Scan) -> None:
        runner = _RUNNER_PREFIX.match(text)
        if runner:
            origin = _Origin(f.label, "runner", f"{runner['name']} on {runner['ul']}", underlying=runner["ul"])
            key = f"{f.label}|strategy:{runner['name']}:{runner['ul']}{runner['err'] or ''}"
            self._stream_line(key, origin, runner["text"], text, scan, None)
            return
        daemon = _DAEMON_PREFIX.match(text)
        if daemon and _is_daemon_label(daemon["label"]):
            label = daemon["label"]
            inner = _INNER_VENDOR.match(daemon["text"])
            vendor = inner["vendor"].lower() if inner else _vendor_of_label(label)
            is_feed = label.lower().endswith(" feed") or label.lower() == "ingestor"
            origin = _Origin(f.label, "feed" if is_feed else "daemon", label, vendor=vendor)
            if _DAEMON_STARTED.search(daemon["text"]):
                # It is running again: its "exited with code 1" is over.
                scan.clear(frozenset({"process-exited"}), re.compile(rf"^{re.escape(label)}$", re.I))
            self._stream_line(f"{f.label}|{label}{daemon['err'] or ''}", origin, daemon["text"], text, scan, None)
            return
        # The API's own words. Info is not an error by its own account, so only
        # the known signatures look at it; a warning may also be new.
        origin = _Origin(f.label, "api", _short_category(category) if category else "API")
        self._check_line(origin, text, text, scan, None, allow_new=(level == "warn"))

    def _api_raw(self, f: _LogFile, raw: str, scan: _Scan) -> None:
        if not raw.strip() or _STACK_FRAME.match(raw) or _may_hold_secret(raw):
            return
        origin = _Origin(f.label, "api", "API")
        if _UNHANDLED.match(raw.strip()):
            # The runtime's last words: the API process itself is going down.
            self._api_error(origin, "crit", "process", [raw.strip()], scan)
            return
        self._check_line(origin, raw.strip(), raw, scan, None, allow_new=True)

    def _finish_block(self, label: str, scan: _Scan) -> None:
        block = self._blocks.pop(label, None)
        if block is None or block.level not in ("fail", "crit"):
            return
        if block.category.startswith(_COMPANION_CATEGORIES):
            return
        self._api_error(_Origin(label, "api", _short_category(block.category)), block.level, block.category,
                        block.lines, scan)

    # -- tracebacks, per stream ------------------------------------------------
    def _stream_line(self, key: str, origin: _Origin, text: str, raw: str, scan: _Scan,
                     line_time: Optional[str]) -> None:
        stream = self._streams.get(key)
        if stream is None:
            stream = self._streams[key] = _Stream()
        stream.touched = self._check_no
        stripped = text.strip()
        secret = _may_hold_secret(raw)

        if stream.trace is not None:
            if not stripped:
                return
            if text[:1] in (" ", "\t"):
                if not secret and stripped.startswith("File "):
                    stream.trace.last_frame = stripped
                return
            if not _EXCEPTION_CLOSER.match(stripped):
                if not secret:
                    self._check_line(origin, stripped, raw, scan, line_time, allow_new=True)
                return
            trace, stream.trace = stream.trace, None
            if secret:
                trace.withheld = True
                trace.exc = _short_type(stripped)
            else:
                trace.exc, trace.exc_raw = stripped, raw
            stream.done, stream.chained = trace, False
            return

        if stripped.startswith("Traceback (most recent call last)"):
            if stream.done is not None and not stream.chained:
                self._emit_trace(stream.done, scan)
            # a chained traceback replaces its cause: the last one is what was raised
            stream.done, stream.chained = None, False
            stream.trace = _Trace(origin, raw)
            return

        if stream.done is not None:
            if not stripped:
                return
            if stripped in _CHAIN_PHRASES:
                stream.chained = True
                return
            self._emit_trace(stream.done, scan)
            stream.done, stream.chained = None, False
        if stripped in _CHAIN_PHRASES:
            return  # its traceback was already reported; the phrase is not a new error

        if secret:
            return
        self._check_line(origin, text.strip(), raw, scan, line_time, allow_new=True)

    def _flush(self, scan: _Scan, only: Optional[str] = None, force: bool = False) -> None:
        """
        Finish what is still open — a .NET entry, a traceback — once its source
        has had a quiet check (or when told to): the rest of a stack, or the
        chained traceback that replaces it, may still be on its way.
        """
        for label in [k for k in self._blocks if only is None or k == only]:
            block = self._blocks[label]
            if block is None or force or block.touched != self._check_no:
                self._finish_block(label, scan)
        for key in [k for k in self._streams if only is None or k.startswith(only + "|")]:
            stream = self._streams[key]
            if not force and stream.touched == self._check_no:
                continue
            if stream.done is not None:
                self._emit_trace(stream.done, scan)
            if stream.trace is not None:
                self._emit_trace(stream.trace, scan)  # the process stopped mid-traceback
            del self._streams[key]

    def _emit_trace(self, trace: _Trace, scan: _Scan) -> None:
        exc = trace.exc or ""
        if exc and (_benign(exc) or _QUIET_EXCEPTIONS.match(exc)):
            return
        if exc and not trace.withheld and self._match_known(trace.origin, exc, trace.exc_raw or exc, scan, None):
            return  # e.g. the 429 a runner died of: its own signature says it better
        self._exception(trace.origin, exc, trace.exc_raw or exc, scan, None, last_frame=trace.last_frame,
                        withheld=trace.withheld,
                        evidence=trace.exc_raw if (exc and not trace.withheld) else trace.head)

    # -- .NET errors ---------------------------------------------------------
    def _api_error(self, origin: _Origin, level: str, category: str, body: list[str], scan: _Scan) -> None:
        lines = [line.strip() for line in body if line.strip() and not _may_hold_secret(line)]
        if not lines:
            return
        inner = [line for line in lines if line.startswith("---> ")]
        exceptions = [line for line in lines if _EXCEPTION_LINE.match(line)]
        root = inner[-1] if inner else (exceptions[0] if exceptions else lines[0])
        message = lines[0]
        if _benign(root) or _benign(message):
            return
        if self._match_known(origin, root.removeprefix("---> "), root.removeprefix("---> "), scan, None):
            return  # e.g. the order rate limit, thrown as an exception
        root = root.removeprefix("---> ")
        if _short_type(root) == _RISK_REFUSAL:
            # An order the risk guard refused (409), from an API that still let
            # the refusal reach its exception handler: a limit working, not an
            # error. Since 30 Sep the API logs it as a warning instead.
            return
        signature = normalise(root)
        crash = category == "process"
        match = _EXCEPTION_LINE.match(root)
        detail = f"{_short_type(root)}: {match['msg']}" if match else root
        scan.add(
            fingerprint=f"{AGENT}:api-error:{_digest(signature)}",
            rule="api-error",
            severity=Severity.HIGH if (crash or level == "crit") else Severity.MEDIUM,
            title=("The API process crashed: " if crash else "API error: ") + _trim(detail, 100),
            what=(f"The API {'died with an unhandled exception' if crash else 'logged ' + level + ' in ' + category}: "
                  f"{_trim(message, 160)}" + (f" — {_trim(root, 160)}" if root != message else "") + "."),
            where=f"logs/{origin.file} · {category}",
            suggestion=_api_error_advice(root),
            raw=f"{level}: {category}" if not crash else root,
            file=origin.file,
            hold=HOLD_SECONDS["api-error"],
        )
        hit = scan.hits.get(f"{AGENT}:api-error:{_digest(signature)}")
        if hit is not None:
            for line in (message, root):
                line = _trim(line)
                if line not in hit.lines and len(hit.lines) < EVIDENCE_LINES:
                    hit.lines.append(line)

    # -- the signatures ------------------------------------------------------
    def _check_line(self, origin: _Origin, text: str, raw: str, scan: _Scan, line_time: Optional[str],
                    allow_new: bool) -> None:
        if not text or _may_hold_secret(raw) or _may_hold_secret(text):
            return
        if self._match_known(origin, text, raw, scan, line_time):
            return
        if not allow_new or not _ERRORISH.search(text) or _benign(text):
            return
        if origin.kind == "job" and not scan.learn:
            # The archive's "rclone cannot reach … Failed to create file system"
            # is why its FAILED line follows: one failure, one incident.
            scan.context.append((origin, text, raw, line_time))
            return
        self._new_error(origin, text, raw, scan, line_time)

    def _new_error(self, origin: _Origin, text: str, raw: str, scan: _Scan, line_time: Optional[str]) -> None:
        signature = normalise(text)
        if not signature:
            return
        fingerprint = f"{AGENT}:new-error:{_digest(signature)}"
        if fingerprint not in scan.hits and not scan.remember(signature):
            return  # seen before (or learned from history)
        scan.add(
            fingerprint=fingerprint,
            rule="new-error",
            severity=Severity.LOW,
            title=f"New error line from {origin.component}: {_trim(text, 90)}",
            what=f"{origin.component} wrote an error line this desk has not seen before: {_trim(text, 200)}",
            where=origin.where,
            suggestion=("First sighting, reported once. If it matters, give it a signature in "
                        "sentinel/agents/logs.py; if it is harmless, add it to BENIGN_LINES there."),
            raw=raw,
            file=origin.file,
            line_time=line_time,
        )

    def _match_known(self, o: _Origin, text: str, raw: str, scan: _Scan, t: Optional[str]) -> bool:
        """Apply the known signatures; True when one of them claimed the line."""
        exit_match = _RUNNER_EXIT.search(text)
        if not exit_match or int(exit_match["code"]) == 0 or "backtest" in text.lower():
            return self._match_signatures(o, text, raw, scan, t)

        # A run died on its own. The run is the trading agent's (one incident
        # per run, or one per mass death, from the API's stop reasons); the
        # logs report the cause, with the runs it killed.
        death = _Death.of(o, text, int(exit_match["code"]))
        scan.last_fp = None
        # The cause is the runner's own last words: judged as the runner's (a
        # vendor refusing its token is vendor-auth), not as the API's.
        if self._match_signatures(death.origin(o), text, raw, scan, t) and scan.last_fp is not None:
            scan.hits[scan.last_fp].add_death(death.label)  # e.g. the 429 from the API's own sign-in
            return True
        if death.cause and _benign(death.cause):
            return True  # e.g. the API was restarting: the health agent's to say, the run the trading agent's
        if death.cause and _EXCEPTION_LINE.match(death.cause) and not _QUIET_EXCEPTIONS.match(death.cause):
            # The traceback in the runner's stderr reports the same exception
            # under this fingerprint: the death and its traceback are one incident.
            self._exception(death.origin(o), death.cause, raw, scan, t, died=death.label)
            return True
        self._runner_crashed(o, death, raw, scan, t)
        return True

    def _match_signatures(self, o: _Origin, text: str, raw: str, scan: _Scan, t: Optional[str]) -> bool:
        if _TOO_MANY.search(text) or _VENDOR_THROTTLE.search(text):
            self._rate_limited(o, text, raw, scan, t)
            return True

        if m := _ORDER_LIMIT.search(text):
            # One churn is one incident: on 24–25 Sep one strategy's exits hit
            # the limit on four runs; the runs are listed, not an incident each.
            scan.add(
                fingerprint=f"{AGENT}:order-rate-limit",
                rule="order-rate-limit", severity=Severity.MEDIUM,
                title=f"Runs are placing more than {m['n']} orders a minute — orders refused",
                what=(f"The API refused orders: more than {m['n']} in the last minute from run {{subjects}}. "
                      f"Their books may no longer match their signals."),
                where=o.where,
                suggestion=("25 Sep: Fulcrum made 270–388 trades per run and ran into this limit. Stop the run "
                            "and review its exits before starting it again."),
                raw=raw, file=o.file, hold=HOLD_SECONDS["order-rate-limit"], line_time=t, subject=m["run"])
            return True

        if o.kind == "feed":
            vendor = o.vendor or _vendor_in_text(text) or o.component
            if m := _TICKLESS.search(text):
                wait = _WAITING.search(text)
                hold = (int(wait["wait"]) + WAIT_MARGIN_SECONDS) if wait else WAIT_MARGIN_SECONDS
                self._reconnect_loop(scan, vendor, raw, o.file, hold, t, int(m["n"]))
                return True
            if _HOST_LOST.search(text):
                scan.lost.setdefault(vendor, []).append((raw, o.file))
                return True

        if _FEED_STALL.search(text):
            m = _BRACKET_UL.search(text)
            underlying = (m["ul"] if m else None) or o.underlying or "?"
            scan.stall_events.append((underlying, True))
            scan.add(
                fingerprint=f"{AGENT}:feed-stalled",
                rule="feed-stalled", severity=Severity.HIGH,
                title="Strategy runners report no ticks while the market is open",
                what="Runners say their feed stalled — no ticks for 90 s or more on {subjects}.",
                where="strategy runners",
                suggestion=("24 Sep 11:27: the Dhan feed reconnected without delivering and every runner went "
                            "deaf. Check Data → Feeds; if the running feed has no fresh ticks, stop it and start "
                            "the other (POST /api/Feeds/dhan/stop, then /api/Feeds/fyers/start)."),
                raw=raw, file=o.file, hold=HOLD_SECONDS["feed-stalled"], line_time=t, subject=underlying)
            return True
        if _FEED_RECOVERED.search(text):
            m = _BRACKET_UL.search(text)
            scan.stall_events.append(((m["ul"] if m else None) or o.underlying or "?", False))
            return True

        if o.kind in ("feed", "runner") and _VENDOR_AUTH.search(text) \
                and not (_LOCALHOST.search(text) and "401" in text):
            vendor = o.vendor or _vendor_in_text(text) or ("broker" if o.kind == "runner" else o.component)
            scan.add(
                fingerprint=f"{AGENT}:vendor-auth:{vendor.lower()}",
                rule="vendor-auth", severity=Severity.HIGH,
                title=f"{_vendor_name(vendor)} rejected the desk's credential (token expired or invalid)",
                what=(f"{o.component} reports that {_vendor_name(vendor)} refused its token: a feed or runner on "
                      f"a dead credential sees no prices."),
                where=o.where, suggestion=_vendor_advice(vendor.lower()),
                raw=raw, file=o.file, hold=scan.hold("vendor-auth", text), line_time=t)
            return True

        if o.kind in ("desk", "job"):
            for line, rules, ends in _CLEARING:
                if line.search(text):
                    scan.clear(rules, ends)
                    return True
            if _DESK_START.match(text):
                scan.desk_starts.append((t or "", raw))
                return True
            if _FAILED_WORD.search(text):
                signature = normalise(text)
                advice, hold = _desk_advice(text, "Read the lines just before it in the file named in 'where'.")
                scan.add(
                    fingerprint=f"{AGENT}:job-failed:{_digest(signature)}",
                    rule="job-failed", severity=Severity.HIGH,
                    title=f"Desk job failed: {_trim(text.removeprefix('WARN: '), 100)}",
                    what=f"{o.component} reported a failure: {_trim(text, 200)}",
                    where=o.where, suggestion=advice,
                    raw=raw, file=o.file, hold=scan.hold("job-failed", text, hold), line_time=t, key=text)
                return True
            if text.startswith("WARN:"):
                if _benign(text):
                    return True
                signature = normalise(text)
                advice, hold = _desk_advice(text, "Read the lines around it in the file named in 'where'.")
                scan.add(
                    fingerprint=f"{AGENT}:desk-warning:{_digest(signature)}",
                    rule="desk-warning", severity=Severity.MEDIUM,
                    title=f"Desk warning: {_trim(text.removeprefix('WARN:').strip(), 100)}",
                    what=f"{o.component} warned: {_trim(text.removeprefix('WARN:').strip(), 200)}",
                    where=o.where, suggestion=advice,
                    raw=raw, file=o.file, hold=scan.hold("desk-warning", text, hold), line_time=t, key=text)
                return True

        if _NOT_CONFIGURED.search(text) and not _benign(text):
            signature = normalise(text)
            scan.add(
                fingerprint=f"{AGENT}:not-configured:{_digest(signature)}",
                rule="not-configured", severity=Severity.MEDIUM,
                title=f"Something is not configured: {_trim(text, 100)}",
                what=f"{o.component} says a setting is missing: {_trim(text, 200)}",
                where=o.where, suggestion=_NOT_CONFIGURED_ADVICE,
                raw=raw, file=o.file, hold=HOLD_SECONDS["not-configured"], line_time=t)
            return True

        if o.kind == "api" and (m := _DAEMON_EXIT.match(text)) and int(m["code"]) not in _STOP_EXIT_CODES:
            name = m["name"].strip()
            scan.add(
                fingerprint=f"{AGENT}:process-exited:{normalise(name).lower().replace(' ', '-')[:60]}",
                rule="process-exited", severity=Severity.HIGH,
                title=f"{name} exited with code {m['code']}",
                what=f"The API's supervisor saw {name} exit with code {m['code']} without being asked to stop.",
                where=o.where,
                suggestion=("Check Data → Feeds (GET /api/Feeds) that it is running again, and read its last "
                            "lines in logs/api.log."),
                raw=raw, file=o.file, hold=scan.hold("process-exited"), line_time=t, key=name)
            return True

        return False

    def _runner_crashed(self, o: _Origin, death: "_Death", raw: str, scan: _Scan, t: Optional[str]) -> None:
        """Runs that died of a cause no other rule knows: one incident per cause, the runs listed."""
        detail = death.cause or f"exit code {death.code}"
        signature = normalise(death.cause) if death.cause else f"exit code {death.code}"
        if death.code in (137, -9):
            advice = ("Exit code 137 is a kill (SIGKILL) from outside the desk — on this server that is usually the "
                      "kernel's out-of-memory killer: check free -m and dmesg for 'Out of memory', and what else "
                      "was running (research jobs belong on the Mac).")
        else:
            advice = ("Read a dead run's last lines (Strategies → Live → the run's log, or logs/api.log around the "
                      "time) for why it exited. The trading agent reports each dead run until it is started again.")
        scan.add(
            fingerprint=f"{AGENT}:runner-crashed:{_digest(signature)}",
            rule="runner-crashed", severity=Severity.HIGH,
            title=f"Strategy runners died on their own: {_trim(detail, 100)}",
            what=(f"A strategy runner exited on its own with code {death.code}"
                  + (f": {_trim(death.cause, 200)}" if death.cause else ", leaving no reason in its output")
                  + ". The API squares off a dead run's positions; the run stays stopped until someone starts it."),
            where=o.where, suggestion=advice,
            raw=raw, file=o.file, hold=scan.hold("runner-crashed"), line_time=t)
        scan.hits[f"{AGENT}:runner-crashed:{_digest(signature)}"].add_death(death.label)

    def _exception(self, origin: _Origin, exc: str, raw: str, scan: _Scan, t: Optional[str],
                   last_frame: str = "", withheld: bool = False, evidence: Optional[str] = None,
                   died: Optional[str] = None) -> None:
        """A Python exception, grouped by what was raised: from its traceback, or from a run it killed."""
        signature = normalise(exc) if exc else "(no exception line) " + normalise(last_frame)
        kind = _short_type(exc) if exc else "unfinished traceback"
        fingerprint = f"{AGENT}:python-traceback:{_digest(signature)}"
        scan.add(
            fingerprint=fingerprint,
            rule="python-traceback",
            severity=Severity.HIGH if died else (Severity.LOW if origin.backtest else Severity.MEDIUM),
            title=f"Python error in {origin.component}: {kind}",
            what=(f"{origin.component} raised {_trim(exc, 160) if exc and not withheld else kind}"
                  + (f" ({_trim(last_frame, 120)})" if last_frame else "") + "."),
            where=origin.where,
            suggestion=_traceback_advice(exc),
            raw=evidence if evidence is not None else raw,
            file=origin.file,
            hold=scan.hold("python-traceback"),
            line_time=t,
        )
        hit = scan.hits.get(fingerprint)
        if hit is None:
            return
        if died:
            hit.add_death(died)
        if last_frame and not withheld:
            frame = _trim(last_frame)
            if frame not in hit.lines and len(hit.lines) < EVIDENCE_LINES:
                hit.lines.append(frame)

    def _rate_limited(self, o: _Origin, text: str, raw: str, scan: _Scan, t: Optional[str]) -> None:
        url = _URL_PATH.search(text)
        target = url["path"].rstrip(".,;:") if url else (o.vendor or _vendor_in_text(text) or o.component)
        hold = scan.hold("rate-limited", text)
        if _TELEGRAM_REFUSED.search(text):
            # 24–26 Sep: 394 of the desk's own alerts refused, in bursts all
            # day, until someone changes how alerts are sent: one incident a
            # session, not one per burst.
            target, hold = "telegram", max(hold, _until_close(scan.now_ts))
            title = "Telegram is refusing the desk's alerts (429 Too Many Requests) — alerts are being lost"
            what = ("The API's Telegram sender got 429 Too Many Requests: those alerts were dropped, not delayed. "
                    "Sentinel's messages go through the same bot and chat.")
            advice = ("Telegram allows about one message a second and twenty a minute into one chat. The candle-"
                      "pattern alerts come in bursts (the API logs \"not sent — more than N messages in 5 minutes\") "
                      "and are the likely source: space them or batch them into one message per candle.")
        elif "/userauth/login" in target.lower():
            title = "The API's sign-in limiter is refusing the desk's own processes (429)"
            what = ("Processes on the desk got 429 Too Many Requests from the API's sign-in. A runner that cannot "
                    "sign in dies at start-up (24 Sep).")
            advice = _LOGIN_LIMIT_ADVICE
        else:
            who = _vendor_name(target) if not url else target
            title = f"Rate limited (429 Too Many Requests) by {who}"
            what = f"{o.component} is being rate-limited by {who}."
            if target.lower() == "dhan":
                advice = ("Dhan refuses an IP that connects too often. Stop the Dhan feed and switch to FYERS "
                          "(Data → Feeds); reconnecting now only extends the block.")
            else:
                advice = ("Back off: the account is being limited. Check that only one process uses the token (a "
                          "second connection on one account gets refused) and wait out the window.")
        scan.add(
            fingerprint=f"{AGENT}:rate-limited:{target.lower()[:120]}",
            rule="rate-limited", severity=Severity.HIGH, title=title, what=what, where=o.where,
            suggestion=advice, raw=raw, file=o.file, hold=hold, line_time=t)

    @staticmethod
    def _reconnect_loop(scan: _Scan, vendor: str, raw: str, file: str, hold: float, t: Optional[str],
                        in_a_row: int) -> None:
        scan.add(
            fingerprint=f"{AGENT}:feed-reconnect-loop:{vendor.lower()}",
            rule="feed-reconnect-loop", severity=Severity.CRITICAL,
            title=f"The {_vendor_name(vendor)} feed is reconnecting in a loop without delivering ticks",
            what=(f"The {_vendor_name(vendor)} feed keeps connecting and losing the connection, and its "
                  f"reconnects carry no ticks: strategies see no prices."),
            where=f"logs/{file} · {_vendor_name(vendor)} feed",
            suggestion=(_FEED_SWITCH_ADVICE + " A sixth Dhan websocket on one account drops the first — check "
                        "nothing else is connected with the Dhan token." if vendor.lower() == "dhan" else
                        "Stop this feed and start the backup from Data → Feeds; check the vendor's status and "
                        "the credential."),
            raw=raw, file=file, hold=hold, line_time=t)
        hit = scan.hits.get(f"{AGENT}:feed-reconnect-loop:{vendor.lower()}")
        if hit is not None:
            hit.loop = max(hit.loop, in_a_row)

    # -- from what was seen to findings -----------------------------------------
    def _findings(self, ctx: SentinelContext, scan: _Scan, data: dict[str, Any], now_ts: float) -> list[Finding]:
        for vendor, lost in scan.lost.items():
            if len(lost) >= 3:
                for raw, file in lost:
                    self._reconnect_loop(scan, vendor, raw, file, WAIT_MARGIN_SECONDS, None, len(lost))
        self._desk_relaunches(ctx, scan, data)
        self._job_context(scan)

        # A feed reconnecting in a loop is critical while a market is open — from the second
        # tickless reconnect in a row. One is a vendor hiccup that the next attempt usually
        # clears; it is not reported at all, because a MEDIUM would still open an incident and
        # send a message, and a real outage does not need it: the health agent pages
        # feed-silent after 90 s without ticks, and a loop's second line follows within about
        # half a minute (core/live/reconnect_policy.py: a 5 s wait, then the next attempt
        # failing the same way). With every market shut, "carried no ticks" is the
        # market's silence, not the feed's: a drop or two is a vendor closing its day and says
        # nothing. A longer run still does — on 16 Sep yesterday's Dhan feed, left running on
        # a dead token, reconnected every 20 s for an hour until Dhan blocked the account — so
        # that stays, as medium.
        loops = [fp for fp, hit in scan.hits.items() if hit.rule == "feed-reconnect-loop"]
        try:
            open_now = not loops or ctx.session().any_open
        except Exception:
            open_now = True
        for fingerprint in loops:
            hit = scan.hits[fingerprint]
            if hit.loop < (OPEN_MARKET_LOOP if open_now else CLOSED_MARKET_LOOP):
                del scan.hits[fingerprint]
            elif not open_now:
                hit.severity = Severity.MEDIUM
                hit.what += " (No market is open right now.)"

        # A recovery is good news, but a feed that stalls and recovers every
        # half hour is still one problem: the incident closes an hour after
        # the last stall, whatever came in between.
        recovered = [u for u, is_stalled in scan.stall_events if not is_stalled]
        stall_hit = scan.hits.get(f"{AGENT}:feed-stalled")
        if stall_hit is not None and recovered and scan.stall_events[-1][1] is False:
            stall_hit.what += f" They reported the feed recovered ({', '.join(dict.fromkeys(recovered))})."
        data.pop("stalled", None)

        active: dict[str, dict[str, Any]] = data.setdefault("active", {})
        # A line that ends a problem — the morning job run again, the daemon
        # starting, "archive to Drive: ok" — lets go of what it ends, unless the
        # problem was reported again after it; the engine then resolves it
        # after its quiet checks.
        for seq, rules, ends in scan.clears:
            for fingerprint in [fp for fp, h in scan.hits.items()
                                if h.rule in rules and h.seq < seq and ends.search(h.key)]:
                del scan.hits[fingerprint]
            for fingerprint in [fp for fp, e in active.items() if fp not in scan.hits
                                and _entry_rule(e) in rules and ends.search(str(e.get("key", "")))]:
                del active[fingerprint]

        now_hm = to_ist(ctx.now()).strftime("%H:%M")
        findings: dict[str, Finding] = {}
        for fingerprint, hit in scan.hits.items():
            previous = active.get(fingerprint)
            # What it is about (runs, underlyings) accumulates while it is held.
            before = previous.get("subjects") if previous and isinstance(previous.get("subjects"), list) else []
            subjects = list(dict.fromkeys([*map(str, before), *hit.subjects]))[-DEATHS_KEPT:]
            what = hit.what.replace("{subjects}", ", ".join(subjects) or "?")
            count = hit.count + (int(previous["count"]) if previous else 0)
            since = previous["since"] if previous else (hit.first_time or now_hm)
            last = hit.last_time or now_hm
            earlier = previous.get("deaths") if previous and isinstance(previous.get("deaths"), list) else []
            deaths = list(dict.fromkeys([*map(str, earlier), *hit.deaths]))[-DEATHS_KEPT:]
            if deaths:
                what = what.rstrip() + _deaths_sentence(deaths)
            finding = Finding(
                agent=AGENT, rule=hit.rule, severity=hit.severity, title=_trim(hit.title, 160),
                summary=_summary(what, count, since, last), fingerprint=fingerprint, where=hit.where,
                evidence=_evidence(hit.lines, hit.files), suggestion=hit.suggestion,
                extra={"lines": hit.count, **({"runs": deaths} if deaths else {})})
            findings[fingerprint] = finding
            if hit.hold > 0:
                # "last_ts": when its newest line was read. api.log's lines carry no time of their own, and
                # a line is read within a check (30 s) of being written: what a resolve is weighed against.
                active[fingerprint] = {"until": now_ts + hit.hold, "since": since, "last": last, "count": count,
                                       "what": what, "key": hit.key[:240], "deaths": deaths, "subjects": subjects,
                                       "last_ts": now_ts, "finding": _dump(finding)}

        for fingerprint in list(active):
            entry = active[fingerprint]
            if fingerprint in findings:
                continue
            if float(entry.get("until", 0)) <= now_ts:
                del active[fingerprint]
                continue
            try:
                findings[fingerprint] = _load(fingerprint, entry)
            except (KeyError, TypeError, ValueError):
                del active[fingerprint]
        return list(findings.values())

    def _job_context(self, scan: _Scan) -> None:
        """A job file's unclaimed error lines: the reason for its FAILED line when it has one, new errors if not."""
        for origin, text, raw, t in scan.context:
            failed = next((h for h in scan.hits.values() if h.rule == "job-failed" and origin.file in h.files), None)
            if failed is None:
                self._new_error(origin, text, raw, scan, t)
                continue
            scan.remember(normalise(text))
            if "Before it:" not in failed.what:
                failed.what = f"{failed.what.rstrip()} Before it: {_trim(text, 200)}"
            line = _trim(raw)
            if line not in failed.lines and len(failed.lines) < EVIDENCE_LINES:
                failed.lines.append(line)

    @staticmethod
    def _desk_relaunches(ctx: SentinelContext, scan: _Scan, data: dict[str, Any]) -> None:
        """desk.sh started twice or more within a minute (24 and 26 Sep mornings: 3–4 launches, one survives)."""
        if not scan.desk_starts:
            return
        today = ist_date(ctx.now())
        previous = data.get("desk_start")
        starts: list[tuple[int, str]] = []
        if isinstance(previous, dict) and previous.get("day") == today:
            starts.append((int(previous["sec"]), str(previous.get("raw", ""))))
        for hms, raw in scan.desk_starts:
            try:
                h, m, s = (int(x) for x in hms.split(":"))
            except ValueError:
                continue
            starts.append((h * 3600 + m * 60 + s, raw))
        if not starts:
            return
        starts.sort()
        close = [raw for i, (sec, raw) in enumerate(starts)
                 if (i > 0 and sec - starts[i - 1][0] <= 60) or (i + 1 < len(starts) and starts[i + 1][0] - sec <= 60)]
        if len(close) >= 2:
            for raw in close:
                scan.add(
                    fingerprint=f"{AGENT}:desk-relaunched",
                    rule="desk-relaunched", severity=Severity.MEDIUM,
                    title="desk.sh was launched several times within a minute",
                    what=("desk.sh started more than once within a minute. Only one survives, but each launch "
                          "re-runs the infra checks and they race on the pidfile (24 and 26 Sep mornings)."),
                    where="logs/desk.log · desk.sh",
                    suggestion=("Check that exactly one thing starts the desk (the keepalive, install-desk.sh, a "
                                "login shell) and that it checks the pidfile before starting."),
                    raw=raw, file="desk.log", hold=scan.hold("desk-relaunched"),
                    line_time=_desk_hms(raw))
        last_sec, last_raw = starts[-1]
        data["desk_start"] = {"day": today, "sec": last_sec, "raw": last_raw[:240]}


# --- small helpers --------------------------------------------------------------
def _desk_hms(raw: str) -> Optional[str]:
    """The HH:MM:SS of a desk.log line, dated or not; None for a line without a stamp."""
    m = _DESK_LINE.match(raw)
    return m["time"] if m else None


def _read_lines(path: Path, start: int, size: int, mid_line: bool = False) -> tuple[list[str], int]:
    """
    Complete lines between ``start`` and ``size`` (at most MAX_READ_BYTES of
    them, the newest), and the offset just past the last one. A trailing line
    without its newline is left for the next check.
    """
    if size - start > MAX_READ_BYTES:
        start, mid_line = size - MAX_READ_BYTES, True
    if size <= start:
        return [], start
    with open(path, "rb") as fh:
        fh.seek(start)
        data = fh.read(size - start)
    if mid_line and start > 0:
        newline = data.find(b"\n")
        if newline < 0:
            return [], start + len(data)
        data, start = data[newline + 1:], start + newline + 1
    cut = data.rfind(b"\n")
    if cut < 0:
        if len(data) < MAX_READ_BYTES:
            return [], start
        consumed = len(data)  # one enormous line: take it rather than stall behind it
    else:
        consumed = cut + 1
    text = data[:consumed].decode("utf-8", "replace")
    lines = text.split("\n")
    if text.endswith("\n"):
        lines.pop()
    return [line.rstrip("\r") for line in lines], start + consumed


def _is_daemon_label(label: str) -> bool:
    low = label.lower()
    return low in _DAEMON_LABELS or low.endswith(" feed")


def _vendor_of_label(label: str) -> Optional[str]:
    low = label.lower()
    for vendor in ("dhan", "fyers", "truedata", "angel"):
        if vendor in low:
            return vendor
    return "fyers" if low == "ingestor" else None


def _vendor_in_text(text: str) -> Optional[str]:
    low = text.lower()
    for vendor in ("dhan", "fyers", "truedata", "angel"):
        if vendor in low:
            return vendor
    return None


def _vendor_name(vendor: str) -> str:
    return {"dhan": "Dhan", "fyers": "FYERS", "truedata": "TrueData", "angel": "Angel One"}.get(vendor.lower(), vendor)


def _short_category(category: str) -> str:
    return category.rsplit(".", 1)[-1] if category else "API"


def _until_close(now_ts: float) -> float:
    """Seconds until 15:30 IST (the desk stops NSE and BSE runs), or 23:30 after it; at least MIN_EVENT_HOLD_SECONDS."""
    now = to_ist(datetime.fromtimestamp(now_ts, timezone.utc))
    for close in (NSE_CLOSE, MCX_CLOSE):
        end = now.replace(hour=close.hour, minute=close.minute, second=0, microsecond=0)
        if now < end:
            return max(float(MIN_EVENT_HOLD_SECONDS), (end - now).total_seconds())
    return float(MIN_EVENT_HOLD_SECONDS)


def _deaths_sentence(deaths: list[str]) -> str:
    shown = ", ".join(deaths[:DEATHS_SHOWN]) + (f" and {len(deaths) - DEATHS_SHOWN} more" if len(deaths) > DEATHS_SHOWN
                                                else "")
    return f" It killed {len(deaths)} run{'' if len(deaths) == 1 else 's'}: {shown}."


def _entry_rule(entry: Any) -> str:
    try:
        return str(entry["finding"]["rule"])
    except (KeyError, TypeError):
        return ""


def _summary(what: str, count: int, first: str, last: str) -> str:
    if count == 1 or first == last:
        return f"{what.rstrip()} {count} matching line(s) at {first} IST."
    return f"{what.rstrip()} {count} matching lines from {first} to {last} IST."


def _evidence(lines: list[str], files: list[str]) -> list[str]:
    evidence = [redact(line) for line in lines[:EVIDENCE_LINES]]
    evidence.append("in " + ", ".join(f"logs/{f}" for f in files[:3]) + (" …" if len(files) > 3 else ""))
    return evidence


def _dump(finding: Finding) -> dict[str, Any]:
    return {"rule": finding.rule, "severity": finding.severity.value, "title": finding.title,
            "where": finding.where, "evidence": list(finding.evidence), "suggestion": finding.suggestion}


def _load(fingerprint: str, entry: dict[str, Any]) -> Finding:
    """
    A held finding, reported again between sightings so its incident stays
    open — as observed when its newest line was read, so that an incident
    resolved since is not reopened by it. A hold from before 28 Sep has no
    "last_ts" and counts as observed now, as it did then.
    """
    stored = entry["finding"]
    last_ts = entry.get("last_ts")
    observed = datetime.fromtimestamp(float(last_ts), timezone.utc) if isinstance(last_ts, (int, float)) else None
    return Finding(
        agent=AGENT, rule=stored["rule"], severity=Severity(stored["severity"]), title=stored["title"],
        summary=(f"{str(entry['what']).rstrip()} {int(entry['count'])} matching line(s) since {entry['since']} IST, "
                 f"the last at {entry['last']} IST; held open while it may recur."),
        fingerprint=fingerprint, where=stored["where"], evidence=list(stored["evidence"]),
        suggestion=stored["suggestion"], observed_utc=observed)


__all__ = ["LogsAgent", "normalise", "BENIGN_LINES"]
