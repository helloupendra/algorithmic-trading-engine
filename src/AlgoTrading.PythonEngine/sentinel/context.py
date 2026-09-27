"""
What an agent is handed each time it checks: the clock, the market session, a
read-only view of the API, Redis and the machine, and a place to keep its own
memory between checks.

Everything here reads. The only way out of this module is ``SentinelContext.run``,
which executes a command from a fixed allowlist of read-only tools — an agent
cannot be talked into running anything else, whatever a log line says.
"""
from __future__ import annotations

import json
import logging
import os
import shutil
import subprocess
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Optional

from sentinel.clock import Session, ask_calendar, now_utc, remember_day, session_from_answers

log = logging.getLogger("sentinel.context")

# The only programs an agent may run, and nothing that changes state. Each is
# resolved to an absolute path once per process (the first time it is run), so
# a PATH change cannot swap one in later.
READ_ONLY_TOOLS = {
    "ss", "df", "free", "uptime", "pgrep", "ps", "last", "who", "stat",
    "docker",  # only "docker ps" / "docker inspect" — enforced in run()
    "git",     # only log, status, ls-files, grep, rev-parse and diff, without the options that run or write — enforced in run()
    "fail2ban-client",  # "status" only
    "dotnet", "npm",    # only "dotnet list" and "npm audit" (never "audit fix") — enforced in run()
}

_ALLOWED_SUBCOMMANDS = {
    "docker": {"ps", "inspect"},
    "git": {"log", "status", "ls-files", "grep", "rev-parse", "diff"},
    "fail2ban-client": {"status"},
    "dotnet": {"list"},
    "npm": {"audit"},
}

# An allowed git subcommand can still be told to run a program or write a
# file: ``git grep -O<cmd>`` / ``--open-files-in-pager`` runs a pager on the
# matches, ``--output=<file>`` writes one, ``--ext-diff`` runs a diff driver,
# ``-c`` sets config. git accepts any unambiguous prefix of a long option
# (``--open``, ``--out``), and bundles short ones (``-nO<cmd>``), so both forms
# are checked. The value after ``-e`` is a pattern, not an option.
_GIT_REFUSED_LONG = ("--open-files-in-pager", "--output", "--ext-diff")
_GIT_REFUSED_SHORT = set("Oc")
# ``npm audit`` reads; ``npm audit fix`` rewrites package-lock.json and
# node_modules. Only the listing, with these flags.
_NPM_AUDIT_FLAGS = ("--json", "--omit=")


def _refusal(args: list[str]) -> Optional[str]:
    """Why these arguments are not a read-only use of an allowed tool; None when they are."""
    if not args or args[0] not in READ_ONLY_TOOLS:
        return f"not a read-only tool: {args[:1]}"
    allowed = _ALLOWED_SUBCOMMANDS.get(args[0])
    if allowed is not None and (len(args) < 2 or args[1] not in allowed):
        return f"{args[0]} {args[1:2]} is not allowed"
    if args[0] == "git":
        pattern_next = False
        for arg in args[2:]:
            if pattern_next:
                pattern_next = False
                continue
            if arg in ("-e", "--regexp"):
                pattern_next = True
                continue
            if arg.startswith("--"):
                name = arg.split("=", 1)[0]
                if len(name) > 2 and any(bad.startswith(name) or name.startswith(bad) for bad in _GIT_REFUSED_LONG):
                    return f"git option {name} can run a program or write a file"
            elif arg.startswith("-") and len(arg) > 1:
                for i, flag in enumerate(arg[1:], start=1):
                    if flag == "e":
                        pattern_next = i == len(arg) - 1   # the rest of the cluster, or else the next argument, is the pattern
                        break
                    if flag in _GIT_REFUSED_SHORT:
                        return f"git option -{flag} can run a program or change configuration"
    if args[0] == "npm" and not all(a == "--json" or a.startswith("--omit=") for a in args[2:]):
        return "only npm audit, with --json and --omit=, is allowed"
    return None


@dataclass(frozen=True)
class PlanLine:
    strategy: str
    underlyings: tuple[str, ...]
    lots: int
    # "@admin" on the line: only these accounts run it. Empty means every account.
    only_accounts: tuple[str, ...] = ()

    def applies_to(self, account: str) -> bool:
        return not self.only_accounts or account.lower() in {a.lower() for a in self.only_accounts}


@dataclass(frozen=True)
class MorningPlan:
    accounts: tuple[str, ...]
    lines: tuple[PlanLine, ...]

    def expected_runs(self) -> list[tuple[str, str, str]]:
        """(account, strategy, underlying) for every run the morning job should have started."""
        return [(a, line.strategy, u) for a in self.accounts for line in self.lines
                if line.applies_to(a) for u in line.underlyings]


def load_plan(repo_root: Path) -> Optional[MorningPlan]:
    """
    The morning plan from config/morning-plan.txt — the same file
    scripts/market-open.sh deploys from, so what is checked is what was asked.
    """
    path = repo_root / "config" / "morning-plan.txt"
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None
    accounts: tuple[str, ...] = ()
    lines: list[PlanLine] = []
    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        if line.lower().startswith("accounts:"):
            accounts = tuple(a for a in line.split(":", 1)[1].split() if a)
            continue
        parts = line.split()
        if len(parts) < 2:
            continue
        lots = int(parts[2]) if len(parts) > 2 and parts[2].isdigit() else 2
        only: tuple[str, ...] = ()
        for field in parts[3:]:
            if field.startswith("@"):
                only = tuple(a for a in field[1:].split(",") if a)
        lines.append(PlanLine(parts[0], tuple(u for u in parts[1].split(",") if u), lots, only))
    return MorningPlan(accounts, tuple(lines)) if accounts and lines else None


class AgentState:
    """A small JSON document an agent keeps between checks (offsets, what it has seen)."""

    def __init__(self, path: Path) -> None:
        self._path = path
        try:
            self.data: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            self.data = {}

    def save(self) -> None:
        try:
            self._path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self._path.with_suffix(".tmp")
            tmp.write_text(json.dumps(self.data, ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, self._path)
        except OSError as exc:
            log.warning("could not save state %s: %s", self._path.name, exc)


@dataclass
class SentinelContext:
    repo_root: Path
    env: dict[str, str]
    api_get: Callable[[str], Any]
    redis_factory: Callable[[], Any]
    clock: Callable[[], datetime] = now_utc
    # Where agents keep their memory between checks; logs/sentinel/ when None.
    # A dry run gets a copy of its own (sentinel/__main__.py): the log offsets
    # and events it consumes would otherwise be lost to the service.
    state_root: Optional[Path] = None
    # --dry-run: an agent that keeps or sends anything of its own (the
    # checkup) keeps it in memory and logs it instead.
    dry_run: bool = False
    _session: Optional[Session] = None
    _calendar: Optional[AgentState] = None
    _redis: Any = None
    _states: dict[str, AgentState] = field(default_factory=dict)
    _executables: dict[str, Optional[str]] = field(default_factory=dict)

    @property
    def logs_dir(self) -> Path:
        return self.repo_root / "logs"

    @property
    def state_dir(self) -> Path:
        return self.state_root if self.state_root is not None else self.logs_dir / "sentinel"

    def now(self) -> datetime:
        return self.clock()

    def session(self) -> Session:
        """
        The market session at this moment, from the API's calendar when it
        answers, and from what it said earlier today when it does not.
        """
        if self._session is None:
            now = self.now()
            answers = ask_calendar(self.api_get)
            kept = self._calendar_state()
            if answers:
                today = remember_day(now, answers, kept.data)
                if today != kept.data:
                    kept.data = today
                    kept.save()
            self._session = session_from_answers(now, answers, kept.data)
        return self._session

    def _calendar_state(self) -> AgentState:
        if self._calendar is None:
            self._calendar = AgentState(self.state_dir / "calendar.json")
        return self._calendar

    def fresh_cycle(self) -> None:
        """Forget what was cached for the previous check."""
        self._session = None

    def redis(self):
        if self._redis is None:
            self._redis = self.redis_factory()
        return self._redis

    def plan(self) -> Optional[MorningPlan]:
        return load_plan(self.repo_root)

    def state(self, agent: str) -> AgentState:
        if agent not in self._states:
            self._states[agent] = AgentState(self.state_dir / f"state-{agent}.json")
        return self._states[agent]

    def run(self, args: list[str], timeout: float = 20.0, cwd: Optional[Path] = None) -> tuple[int, str]:
        """
        Run one read-only tool and return (exit code, stdout). Refuses anything
        off the allowlist rather than trying to be clever about it. Output is
        decoded leniently: a file name or a log line that is not UTF-8 is a
        replacement character, not an exception out of a read-only helper.
        """
        refusal = _refusal(list(args))
        if refusal is not None:
            raise PermissionError(refusal)
        if args[0] not in self._executables:
            self._executables[args[0]] = shutil.which(args[0])
        exe = self._executables[args[0]]
        if exe is None:
            return 127, ""
        try:
            done = subprocess.run([exe, *args[1:]], capture_output=True, text=True, encoding="utf-8",
                                  errors="replace", timeout=timeout, cwd=str(cwd or self.repo_root), check=False)
            return done.returncode, done.stdout
        except subprocess.TimeoutExpired:
            return 124, ""
        except OSError:
            return 126, ""
