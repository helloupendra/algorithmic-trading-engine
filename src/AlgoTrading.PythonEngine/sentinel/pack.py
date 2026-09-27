"""
The context pack: what changed around the moment an incident was seen.

An agent says what is wrong. The first thing a person then does is look
around it, and on this desk that has always meant the same four questions —
Sentinel's own advice keeps saying "read logs/api.log around the time above"
and "check logs/desk.log for a deploy or restart at that time":

* the last deploy (data/deploy-history.json, what the Deployments page shows),
  and, when it went out within 30 minutes of the first sighting, plainly how
  long before;
* the commit checked out now (``git log -1`` through the context's read-only
  allowlist);
* how many strategy runs were live (the trading agent's own GET);
* the error-looking lines of logs/api.log and logs/desk.log in the two minutes
  before the sighting, and what the desk did in them (a restart, a deploy) —
  and, read two minutes later, in the two minutes after it.

The engine gathers one when an incident opens and when it escalates to high or
critical, keeps it in the incident's evidence (lines prefixed ``context: ``,
model.CONTEXT_PREFIX) and puts it in the Telegram message. The message cannot
wait for what the logs say next, so that half goes to the console only, as
lines beginning "then".

Every part is best-effort: a missing file, an API that does not answer, a git
that is not there make the pack shorter, never the check fail — the incident
matters more than its context. It stays cheap: the logs are read from where
they ended two minutes ago, never whole, and a burst of incidents shares one
reading of them, one ``git log`` and one API call. It only reads, and what it
reads is redacted like any other evidence; a line that may carry a credential
is dropped whole, as the logs agent does.
"""
from __future__ import annotations

import json
import logging
import re
import time
from collections import Counter
from dataclasses import dataclass, field
from datetime import date, datetime, time as clock_time, timedelta, timezone
from pathlib import Path
from typing import Any, Callable, Optional

from sentinel.agents.logs import _DOTNET_HEADER, _ERRORISH, _STACK_FRAME, _benign, _may_hold_secret, normalise
from sentinel.agents.trading import RUNNING_PATH, _parse_runs
from sentinel.clock import IST, to_ist
from sentinel.context import SentinelContext
from sentinel.model import CONTEXT_PREFIX
from sentinel.notify import redact

log = logging.getLogger("sentinel.pack")

#: A deploy this close before the first sighting is named as the likely change.
DEPLOY_NEAR = timedelta(minutes=30)
#: Log lines this long either side of the sighting.
LOG_WINDOW = timedelta(minutes=2)
LOG_LINES = 8
LATER_LINES = 4
LINE_CHARS = 200
LOG_FILES = ("desk.log", "api.log")
#: At most this much of a file is read for one window: its newest part. api.log
#: carries every runner's output; two busy minutes of it are not all worth reading.
WINDOW_MAX_BYTES = 512 * 1024
#: With no mark old enough (Sentinel started under two minutes ago), the end of
#: the file stands in — when it changed within the window at all.
TAIL_BYTES = 64 * 1024
MARKS_KEPT = timedelta(minutes=10)
#: The live-run count is asked once per this long; after a failure, not again
#: for a while — "the API is not answering" is the health agent's to say, and a
#: hanging API must not hold every incident's message for its timeout.
RUNS_FRESH_SECONDS = 30.0
RUNS_RETRY_SECONDS = 300.0
HEAD_FRESH_SECONDS = 60.0

# "2026-09-28 08:45:03  text" since 28 Sep 2026 (say() in scripts/lib/desk-common.sh),
# "08:45:03  text" before: the date is optional.
_DESK_STAMP = re.compile(r"^(?:(?P<date>\d{4}-\d{2}-\d{2})[ T])?(?P<h>\d{2}):(?P<m>\d{2}):(?P<s>\d{2})\s+(?P<text>.*)$")
#: What desk.sh says when it changes something: a restart, a deploy, a job.
_DESK_EVENT = re.compile(r"^(?:=== |stopping the API|starting the API|API up\b|API is down|origin/main moved"
                         r"|building \S+ -> |deploy(?::| of) \S+)")


@dataclass(frozen=True)
class _Mark:
    """Where one log file ended at one moment: the start of a later window."""

    at: datetime
    inode: int
    size: int


@dataclass(frozen=True)
class _Window:
    """A stretch of one log file to read: bytes ``start`` to ``end`` of the file ``inode``."""

    path: Path
    inode: int
    start: int
    end: int


@dataclass(frozen=True)
class Pack:
    """
    One incident's context: its evidence lines, and where each log ended when
    they were read — the start of :meth:`ContextPack.later`'s window.
    """

    lines: list[str]
    at: datetime
    ends: dict[str, tuple[int, int]] = field(default_factory=dict)   # log name -> (inode, size)


def _share(desk: "_Lines", api: "_Lines", n: int) -> list[str]:
    """The newest ``n`` lines, half from each file; a quiet one leaves its half to the other."""
    take_desk = min(len(desk), max(n // 2, n - len(api)))
    return ([f"desk.log {line}" for line in desk.newest(take_desk)]
            + [f"api.log: {line}" for line in api.newest(n - take_desk)])


def _parse_time(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value.strip():
        return None
    try:
        moment = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def _minutes(span: timedelta) -> str:
    seconds = int(abs(span.total_seconds()))
    return "under a minute" if seconds < 60 else f"{seconds // 60} min"


def _when(moment: datetime, now: datetime) -> str:
    local = to_ist(moment)
    return local.strftime("%H:%M IST") if local.date() == to_ist(now).date() else local.strftime("%d %b %H:%M IST")


def _trim(text: str, limit: int = LINE_CHARS) -> str:
    text = " ".join(text.split())
    return text if len(text) <= limit else text[: limit - 1] + "…"


def _read_range(path: Path, start: int, end: int) -> list[str]:
    """Complete lines between two offsets; a line cut by ``start`` is dropped."""
    if end <= start:
        return []
    with open(path, "rb") as fh:
        fh.seek(max(0, start - 1))
        data = fh.read(end - max(0, start - 1))
    if start > 0:
        # The byte before the window says whether it opens on a line of its own.
        head, data = data[:1], data[1:]
        if head != b"\n":
            cut = data.find(b"\n")
            data = data[cut + 1:] if cut >= 0 else b""
    return data.decode("utf-8", "replace").splitlines()


def _desk_time(hh: int, mm: int, ss: int, now: datetime, day: Optional[str] = None) -> Optional[datetime]:
    """
    A desk.log stamp, IST wall-clock time. A dated line says its own day; an
    undated one (before 28 Sep 2026) is today's, or yesterday's if that is in the future.
    """
    try:
        stamp = clock_time(hh, mm, ss)
        if day:
            return datetime.combine(date.fromisoformat(day), stamp, tzinfo=IST).astimezone(timezone.utc)
    except ValueError:
        return None
    local = to_ist(now)
    moment = datetime.combine(local.date(), stamp, tzinfo=IST)
    if moment > local + timedelta(seconds=60):
        moment -= timedelta(days=1)
    return moment.astimezone(timezone.utc)


class _Lines:
    """Error-looking lines, one per signature: a line that repeats is counted, and moves to the newest place."""

    def __init__(self) -> None:
        self._by_signature: dict[str, list] = {}

    def add(self, text: str) -> None:
        if not text or _may_hold_secret(text) or _benign(text):
            return
        signature = normalise(text)
        entry = self._by_signature.pop(signature, None)
        count = entry[1] + 1 if entry else 1
        self._by_signature[signature] = [text, count]

    def newest(self, n: int) -> list[str]:
        rows = list(self._by_signature.values())[-n:] if n > 0 else []
        return [text if count == 1 else f"{text} (×{count})" for text, count in rows]

    def __len__(self) -> int:
        return len(self._by_signature)


class ContextPack:
    """
    Gathers the pack for one incident. The engine calls :meth:`mark` after
    every round, so a later pack knows where each log ended two minutes before
    it — api.log's lines carry no time of their own (the .NET console logger
    writes ``fail: Category[id]`` and nothing else), so its window is measured
    in bytes.
    """

    def __init__(self, ctx: SentinelContext, monotonic: Callable[[], float] = time.monotonic) -> None:
        self._ctx = ctx
        self._monotonic = monotonic
        self._marks: dict[str, list[_Mark]] = {}
        # This round's log lines and where the logs ended: every incident of a round shares them.
        self._logs: Optional[tuple[list[str], dict[str, tuple[int, int]]]] = None
        self._runs: Optional[tuple[float, Optional[str]]] = None   # (asked at, the line, or None on a failure)
        self._head: Optional[tuple[float, Optional[str]]] = None

    def mark(self) -> None:
        """Note where each log ends now. Called once a round; also starts the next round's pack afresh."""
        self._logs = None
        now = self._ctx.now()
        for name in LOG_FILES:
            try:
                st = (self._ctx.logs_dir / name).stat()
            except OSError:
                continue
            marks = self._marks.setdefault(name, [])
            marks.append(_Mark(now, st.st_ino, st.st_size))
            while marks and now - marks[0].at > MARKS_KEPT:
                marks.pop(0)

    def gather(self, first_seen: datetime, at: datetime) -> Pack:
        """The pack for an incident first seen at ``first_seen`` and sighted now, at ``at``."""
        lines: list[str] = []
        ends: dict[str, tuple[int, int]] = {}
        for name, part in (("deploy", lambda: self._deploy(first_seen, at)), ("head", self._head_line),
                           ("runs", self._live_runs), ("logs", lambda: self._log_lines(at, ends))):
            try:
                lines += part()
            except Exception as exc:  # a shorter pack, never a lost incident
                log.debug("context pack: %s left out: %s", name, exc)
        return Pack([CONTEXT_PREFIX + _trim(redact(line)) for line in lines if line], at, ends)

    def later(self, pack: Pack) -> list[str]:
        """
        The log lines of the two minutes after ``pack`` was gathered, read once
        they are written: what the API said as it came back, what the desk did
        next. Evidence lines beginning "then"; none when the logs cannot say.
        """
        desk, api = _Lines(), _Lines()
        try:
            for name, read, out in (("desk.log", self._desk_lines, desk), ("api.log", self._api_lines, api)):
                if name in pack.ends:
                    window = self._since(name, *pack.ends[name])
                    if window is not None:
                        # The bytes start where the pack's own read ended; only the end is a time.
                        read(window, pack.at - LOG_WINDOW, pack.at + LOG_WINDOW, out)
        except Exception as exc:
            log.debug("context pack: the lines after %s left out: %s", pack.at, exc)
            return []
        return [CONTEXT_PREFIX + _trim(redact(f"then {line}")) for line in _share(desk, api, LATER_LINES)]

    # -- the last deploy -----------------------------------------------------------
    def _deploy(self, first_seen: datetime, at: datetime) -> list[str]:
        try:
            history = json.loads((self._ctx.repo_root / "data" / "deploy-history.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []
        record = history[0] if isinstance(history, list) and history else history
        if not isinstance(record, dict):
            return []
        finished = _parse_time(record.get("finishedUtc"))
        started = _parse_time(record.get("startedUtc")) or finished
        finished = finished or started
        commit = str(record.get("toCommit") or "").strip()[:12] or "?"
        outcome = str(record.get("outcome") or "").strip() or "no outcome recorded"
        commits = [c for c in record.get("commits") or [] if isinstance(c, str) and c.strip()]
        subject = commits[0].strip().split(" ", 1)[1] if commits and " " in commits[0].strip() else ""
        if subject and len(commits) > 1:
            subject += f" (+{len(commits) - 1} more)"
        # A deploy that did not go out is better told by why ("Deferred: 12 live runs", "API build FAILED").
        detail = subject if outcome == "ok" and subject else str(record.get("summary") or "").strip()
        what = f"{commit}, {outcome}" + (f": {detail}" if detail else "")
        if finished is None:
            return [f"last deploy {what} (no time recorded)"]

        if finished <= first_seen and first_seen - finished <= DEPLOY_NEAR:
            return [f"deployed {_minutes(first_seen - finished)} before this was first seen: {what} "
                    f"(finished {_when(finished, at)})"]
        if started <= first_seen < finished:
            return [f"a deploy was going out when this was first seen: {what} "
                    f"({_when(started, at)}–{_when(finished, at)})"]
        if first_seen < started and finished <= at:
            return [f"deployed {_minutes(started - first_seen)} after this was first seen: {what} "
                    f"(finished {_when(finished, at)})"]
        return [f"last deploy {_when(finished, at)}: {what}"]

    # -- the commit checked out ----------------------------------------------------
    def _head_line(self) -> list[str]:
        now = self._monotonic()
        if self._head is not None and now - self._head[0] < HEAD_FRESH_SECONDS:
            return [self._head[1]] if self._head[1] else []
        line: Optional[str] = None
        try:
            rc, out = self._ctx.run(["git", "log", "-1", "--format=%h %s"], timeout=5.0)
            if rc == 0 and out.strip():
                line = f"checked out: {out.strip().splitlines()[0]}"
        except Exception as exc:   # PermissionError would be a bug here; either way, no line
            log.debug("context pack: git log failed: %s", exc)
        self._head = (now, line)
        return [line] if line else []

    # -- the runs --------------------------------------------------------------------
    def _live_runs(self) -> list[str]:
        now = self._monotonic()
        if self._runs is not None:
            asked, line = self._runs
            if now - asked < (RUNS_FRESH_SECONDS if line else RUNS_RETRY_SECONDS):
                return [line] if line else []
        line: Optional[str] = None
        try:
            body = self._ctx.api_get(RUNNING_PATH)
        except Exception as exc:
            log.debug("context pack: live runs not read: %s", exc)
            body = None
        if isinstance(body, list):
            runs = [r for r in _parse_runs(body) if r.live and not r.not_a_strategy]
            accounts = Counter(r.user for r in runs)
            line = (f"{len(runs)} strategy {'run' if len(runs) == 1 else 'runs'} live at "
                    f"{to_ist(self._ctx.now()):%H:%M} IST"
                    + (" (" + ", ".join(f"{a} {n}" for a, n in sorted(accounts.items())) + ")" if runs else ""))
        self._runs = (now, line)
        return [line] if line else []

    # -- the logs --------------------------------------------------------------------
    def _log_lines(self, at: datetime, ends: dict[str, tuple[int, int]]) -> list[str]:
        if self._logs is None:
            desk, api = _Lines(), _Lines()
            where: dict[str, tuple[int, int]] = {}
            for name, read, out in (("desk.log", self._desk_lines, desk), ("api.log", self._api_lines, api)):
                window = self._before(name, at)
                if window is not None:
                    where[name] = (window.inode, window.end)
                    read(window, at - LOG_WINDOW, at, out)
            self._logs = (_share(desk, api, LOG_LINES), where)
        ends.update(self._logs[1])
        return list(self._logs[0])

    def _before(self, name: str, at: datetime) -> Optional[_Window]:
        """What ``name`` gained since LOG_WINDOW before ``at`` (maybe nothing); None when there is no such file."""
        path = self._ctx.logs_dir / name
        try:
            st = path.stat()
        except OSError:
            return None
        since = at - LOG_WINDOW
        start: Optional[int] = None
        for mark in reversed(self._marks.get(name, [])):
            if mark.at <= since:
                # A different file (api.log is rotated on every API restart) began after that mark.
                start = mark.size if mark.inode == st.st_ino and mark.size <= st.st_size else 0
                break
        if start is None:
            changed = datetime.fromtimestamp(st.st_mtime, timezone.utc) >= since
            start = max(0, st.st_size - TAIL_BYTES) if changed else st.st_size
        return _Window(path, st.st_ino, max(start, st.st_size - WINDOW_MAX_BYTES), st.st_size)

    def _since(self, name: str, inode: int, size: int) -> Optional[_Window]:
        """What ``name`` gained since it was ``size`` bytes long — all of it, when it has been rotated since."""
        path = self._ctx.logs_dir / name
        try:
            st = path.stat()
        except OSError:
            return None
        start = size if st.st_ino == inode and size <= st.st_size else 0
        return _Window(path, st.st_ino, max(start, st.st_size - WINDOW_MAX_BYTES), st.st_size)

    def _desk_lines(self, window: _Window, low: datetime, high: datetime, out: _Lines) -> None:
        now = self._ctx.now()
        stamp: Optional[datetime] = None
        for raw in _read_range(window.path, window.start, window.end):
            m = _DESK_STAMP.match(raw.strip())
            if m:
                stamp = _desk_time(int(m["h"]), int(m["m"]), int(m["s"]), now, m["date"])
                text, hms = m["text"].strip(), f"{m['h']}:{m['m']}:{m['s']}"
            else:
                text, hms = raw.strip(), None   # build output: it belongs to the last stamped line
            if stamp is None or not (low <= stamp <= high):
                continue
            if _DESK_EVENT.match(text) or text.startswith("WARN:") or _ERRORISH.search(text):
                out.add(f"{hms or to_ist(stamp).strftime('%H:%M:%S')} {text}")

    def _api_lines(self, window: _Window, low: datetime, high: datetime, out: _Lines) -> None:
        # The bytes are the window: api.log's lines have no time to hold to ``low`` and ``high``.
        failing: Optional[str] = None   # "fail Query": the entry whose message comes next
        in_failure = False              # the rest of a fail entry is its exception and stack
        for raw in _read_range(window.path, window.start, window.end):
            header = _DOTNET_HEADER.match(raw)
            if header:
                level = header["level"]
                failing = f"{level} {header['category'].rsplit('.', 1)[-1]}" if level in ("fail", "crit") else None
                in_failure = False
                continue
            body = raw.startswith("      ")
            text = raw.strip()
            if not text:
                continue
            if body and failing is not None:
                out.add(f"{failing} — {text}")
                failing, in_failure = None, True
                continue
            if body and in_failure:
                continue
            if not body:
                in_failure = False
            if _STACK_FRAME.match(raw):
                continue
            if _ERRORISH.search(text):
                out.add(text)


__all__ = ["ContextPack", "Pack", "CONTEXT_PREFIX", "DEPLOY_NEAR", "LOG_WINDOW"]
