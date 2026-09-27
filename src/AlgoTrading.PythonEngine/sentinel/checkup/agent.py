"""
The checkup as a Sentinel agent: every minute it looks whether a person asked
for a checkup from the console, or a scheduled one is due, and runs it.

It opens no incidents — its report is the output, kept in ``desk_checkups`` and
sent to the system channel. A checkup that crashes outside the checks (a bug
here) raises, and the engine opens an incident about this agent as it does for
any other; the slot is marked done before it runs, so a crash is not repeated
every minute.
"""
from __future__ import annotations

import logging
import socket
from datetime import datetime, timedelta
from typing import Optional

from sentinel.agents.base import Agent
from sentinel.checkup.checks import Inputs, run_checks
from sentinel.checkup.model import Report, Verdict
from sentinel.checkup.reads import DeskReads
from sentinel.checkup.report import format_report
from sentinel.checkup.slots import ON_REQUEST, Slot, due
from sentinel.checkup.store import CheckupStore, MemoryCheckupStore, PostgresCheckupStore
from sentinel.clock import IST, ist_date, to_ist
from sentinel.context import AgentState, SentinelContext
from sentinel.model import Finding
from sentinel.notify import Notifier, notifier_from_env
from sentinel.store import dsn_from_env

log = logging.getLogger("sentinel.checkup")

#: A report Telegram did not take is tried again on the next checks, for this long.
RESEND_FOR = timedelta(hours=2)
#: At most this many reports wait to be sent; the console has every one.
UNSENT_MAX = 4
#: A scheduled checkup that has been waiting for the API runs anyway this long before its window ends.
LAST_CHANCE = timedelta(minutes=15)


def _window_end(now_utc: datetime, slot: Slot) -> datetime:
    return datetime.combine(to_ist(now_utc).date(), slot.until, tzinfo=IST)


def _api_answering(ctx: SentinelContext) -> bool:
    """The calendar answered just now: the API is up (the session is read from it every round)."""
    session = ctx.session()
    return session.from_calendar and not session.remembered


class CheckupAgent(Agent):
    name = "checkup"
    interval_seconds = 60

    def __init__(self, store: Optional[CheckupStore] = None, reads: Optional[DeskReads] = None,
                 notifier: Optional[Notifier] = None, host: Optional[str] = None) -> None:
        self._store = store
        self._reads = reads
        self._notifier = notifier
        self._host = host or socket.gethostname()
        self._wired = False
        self._said_waiting = ""

    def _wire(self, ctx: SentinelContext) -> None:
        """What was not handed in — the store, the reads, the notifier — from the .env, once, on the first check."""
        if self._wired:
            return
        self._wired = True
        dsn = dsn_from_env(ctx.env)
        if self._store is None:
            self._store = PostgresCheckupStore(dsn) if dsn and not ctx.dry_run else MemoryCheckupStore()
        if self._reads is None and dsn:
            self._reads = DeskReads(dsn)   # read-only, so a dry run reads the real desk too
        if self._notifier is None:
            self._notifier = notifier_from_env(ctx.env, ctx.dry_run)

    def check(self, ctx: SentinelContext) -> list[Finding]:
        self._wire(ctx)
        state = ctx.state(self.name)
        self._resend(ctx, state)

        # A person asked from the console first: they are looking at the page.
        try:
            requested = self._store.claim_request(ctx.now(), self._host)
        except Exception as exc:
            log.warning("could not look for a requested checkup: %s: %s", type(exc).__name__, exc)
            requested = None
        if requested is not None:
            self.run(ctx, ON_REQUEST, requested, state)

        done = state.data.setdefault("done", {})
        slot = due(ctx.now(), ctx.session().trading_day, done)
        if slot is None:
            return []
        # A deploy restarts the API and Sentinel together, and brings the
        # table a new checkup is kept in: a checkup run in that minute would
        # report the API down, or go unrecorded. It waits — up to a quarter
        # of an hour before its window ends, after which a report that says
        # "the API is down" is the truth and goes out.
        last_chance = ctx.now() >= _window_end(ctx.now(), slot) - LAST_CHANCE
        if not _api_answering(ctx) and not last_chance:
            self._waiting(slot, "the API is not answering")
            return []
        try:
            checkup_id = self._store.start(slot.name, ctx.now(), self._host)
        except Exception as exc:
            log.warning("could not record the %s checkup: %s: %s", slot.name, type(exc).__name__, exc)
            checkup_id = None
        if checkup_id is None and not last_chance:
            self._waiting(slot, "it cannot be recorded yet")
            return []
        done[slot.name] = ist_date(ctx.now())   # before it runs: a checkup that crashes is not rerun every minute
        state.save()
        self._said_waiting = ""
        self.run(ctx, slot, checkup_id, state)
        return []

    def _waiting(self, slot: Slot, why: str) -> None:
        if self._said_waiting != slot.name:
            log.info("the %s checkup waits: %s", slot.name, why)
            self._said_waiting = slot.name

    def run(self, ctx: SentinelContext, slot: Slot, checkup_id: Optional[int],
            state: Optional[AgentState] = None) -> Report:
        """One checkup: every check of the slot, kept, and sent when the slot sends."""
        report = Report(slot.name, started_utc=ctx.now(), host=self._host)
        try:
            report.items = run_checks(Inputs(ctx, self._reads, slot.name), slot.checks)
        except Exception as exc:
            if checkup_id is not None:
                self._quietly(self._store.fail, checkup_id, f"{type(exc).__name__}: {exc}", ctx.now())
            raise
        report.completed_utc = ctx.now()
        headline = report.headline(slot.when)
        log.info("checkup %s%s: %s", slot.name, f" #{checkup_id}" if checkup_id else "", headline)
        if checkup_id is not None:
            self._quietly(self._store.finish, checkup_id, report, headline)
        if slot.telegram and not (slot.quiet_when_ok and report.verdict is Verdict.OK):
            text = format_report(report, slot, ctx.env.get("SENTINEL_PUBLIC_URL", "https://openfno.com"))
            self._send(ctx, state, text, checkup_id)
        return report

    def _send(self, ctx: SentinelContext, state: Optional[AgentState], text: str, checkup_id: Optional[int]) -> None:
        if self._notifier.send(text):
            if checkup_id is not None:
                self._quietly(self._store.mark_notified, checkup_id, ctx.now())
            return
        if self._notifier.refused or state is None:
            return   # Telegram refused this text as such: another try cannot work; the console has it
        unsent = state.data.setdefault("unsent", [])
        unsent.append({"id": checkup_id, "text": text, "at": ctx.now().isoformat()})
        del unsent[:-UNSENT_MAX]
        state.save()

    def _resend(self, ctx: SentinelContext, state: AgentState) -> None:
        unsent = state.data.get("unsent") or []
        if not unsent:
            return
        waiting = []
        for message in unsent:
            try:
                at = datetime.fromisoformat(message["at"])
            except (KeyError, TypeError, ValueError):
                continue
            if ctx.now() - at > RESEND_FOR:
                continue
            if waiting or not self._notifier.send(message["text"]):
                waiting.append(message)   # in order: one failure holds the rest back until the next check
            elif message.get("id") is not None:
                self._quietly(self._store.mark_notified, message["id"], ctx.now())
        state.data["unsent"] = waiting
        state.save()

    @staticmethod
    def _quietly(write, *args) -> None:
        """A write to the checkups table that fails is logged: the report still goes out."""
        try:
            write(*args)
        except Exception as exc:
            log.warning("could not keep the checkup: %s: %s", type(exc).__name__, exc)
