"""
Turning findings into incidents.

Each agent runs on its own cadence. What it returns is upserted by fingerprint:
a new fingerprint opens an incident and sends a message; one already open is
updated quietly, unless its severity went up, which is worth a second message.
An open incident an agent stops reporting is counted clean; after
``resolve_after`` clean checks in a row it is resolved, with a message.

A person can also resolve an incident from the console. If the condition is
still there, the next check opens a fresh one — which is the honest answer to
"I resolved it and it came back".

An incident that opens, or escalates to high or critical, gets a context pack
(sentinel/pack.py): the last deploy, the live commit, the runs, the log lines
around it. It rides in the incident's evidence and in the message; two minutes
later the log lines that came after are added to the evidence, quietly.
"""
from __future__ import annotations

import logging
import time
import traceback
from dataclasses import dataclass, field
from datetime import datetime
from typing import Callable, Optional

from sentinel.agents.base import Agent
from sentinel.context import SentinelContext
from sentinel.model import Finding, Severity
from sentinel.notify import Notifier, format_opened, format_resolved
from sentinel.pack import LOG_WINDOW, ContextPack, Pack
from sentinel.store import IncidentStore

log = logging.getLogger("sentinel.engine")


@dataclass
class _AgentTrack:
    agent: Agent
    next_due: float = 0.0
    clean: dict[str, int] = field(default_factory=dict)  # fingerprint -> consecutive clean checks


class SentinelEngine:
    def __init__(self, agents: list[Agent], store: IncidentStore, notifier: Notifier,
                 ctx: SentinelContext, monotonic=time.monotonic, pack: Optional[ContextPack] = None) -> None:
        self._tracks = [_AgentTrack(a) for a in agents]
        self._store = store
        self._notifier = notifier
        self._ctx = ctx
        self._monotonic = monotonic
        self._pack = pack   # None: incidents carry no context (tests that are not about it)
        self._later: dict[int, Pack] = {}   # incident id -> its pack, waiting for the lines after it

    def run_due(self) -> int:
        """Run every agent whose turn has come. Returns how many ran."""
        now = self._monotonic()
        ran = 0
        self._ctx.fresh_cycle()
        for track in self._tracks:
            if now < track.next_due:
                continue
            track.next_due = now + max(5, track.agent.interval_seconds)
            self.run_agent(track)
            ran += 1
        if ran:
            self._mark()
            self._add_later_lines()
            self._beat()
        return ran

    def run_all_once(self) -> None:
        """Every agent, once, regardless of cadence (``--once``)."""
        self._ctx.fresh_cycle()
        for track in self._tracks:
            self.run_agent(track)
        self._mark()
        self._beat()

    def _mark(self) -> None:
        """Let the context pack note where the logs end, for the windows of later incidents."""
        if self._pack is None:
            return
        try:
            self._pack.mark()
        except Exception as exc:
            log.debug("context pack could not mark the logs: %s", exc)

    def _context(self, first_seen: datetime, now: datetime) -> Optional[Pack]:
        """The context pack for an incident, or none: an incident never waits on, or fails for, its context."""
        if self._pack is None:
            return None
        try:
            return self._pack.gather(first_seen, now)
        except Exception as exc:
            log.warning("could not gather the context pack: %s", exc)
            return None

    def _add_later_lines(self) -> None:
        """Two minutes after a pack, what the logs said next: into the incident's evidence, without a message."""
        if self._pack is None or not self._later:
            return
        now = self._ctx.now()
        for incident_id, pack in list(self._later.items()):
            if now - pack.at < LOG_WINDOW:
                continue
            del self._later[incident_id]
            try:
                lines = self._pack.later(pack)
                if lines:
                    self._store.attach_context(incident_id, pack.lines + lines)
            except Exception as exc:
                log.warning("could not add the later log lines of #%s: %s", incident_id, exc)

    def _beat(self) -> None:
        """Tell the console a round finished — a silent list then means quiet, not stopped."""
        try:
            self._store.heartbeat(self._ctx.now())
        except Exception as exc:
            log.warning("could not record the heartbeat: %s", exc)

    def run_agent(self, track: _AgentTrack) -> None:
        agent = track.agent
        started = time.monotonic()
        try:
            findings = agent.check(self._ctx)
        except Exception as exc:  # a broken watcher is itself something to report
            findings = [Finding(
                agent=agent.name,
                rule="agent-crashed",
                severity=Severity.MEDIUM,
                title=f"Sentinel's {agent.name} agent failed to run its check",
                summary=f"{type(exc).__name__}: {exc}",
                fingerprint=f"{agent.name}:agent-crashed:{type(exc).__name__}",
                where=f"sentinel/agents/{agent.name}.py",
                evidence=traceback.format_exc().strip().splitlines()[-6:],
                suggestion="Read the traceback; the agent's other checks did not run this cycle.",
            )]
        log.debug("%s: %d finding(s) in %.1fs", agent.name, len(findings), time.monotonic() - started)

        seen: set[str] = set()
        for finding in findings:
            if finding.fingerprint in seen:
                continue  # one sighting per check, however many times an agent repeats itself
            seen.add(finding.fingerprint)
            self._record(finding)
            track.clean.pop(finding.fingerprint, None)

        self._resolve_quiet(track, seen)

    def _record(self, finding: Finding) -> None:
        now = self._ctx.now()
        try:
            result = self._store.upsert(finding, now)
        except Exception as exc:
            log.error("could not store %s: %s", finding.fingerprint, exc)
            # Still tell someone: a problem nobody hears about is the failure this exists to prevent.
            pack = self._context(now, now)
            self._notifier.send(format_opened(finding, 0, context=pack.lines if pack else None))
            return

        if result.is_new or result.escalated:
            pack = None
            # A low incident turning medium is worth a message, not a second look around.
            if result.is_new or result.severity.rank >= Severity.HIGH.rank:
                pack = self._context(result.first_seen_utc or now, now)
                if pack is not None and pack.lines:
                    try:
                        self._store.attach_context(result.incident_id, pack.lines)
                        self._later[result.incident_id] = pack   # a newer pack replaces a waiting one
                    except Exception as exc:
                        log.warning("could not keep the context of #%s: %s", result.incident_id, exc)
            text = format_opened(finding, result.incident_id, escalated=result.escalated,
                                 context=pack.lines if pack else None)
            if self._notifier.send(text):
                try:
                    self._store.mark_notified(result.incident_id, now)
                except Exception:
                    pass

    def _resolve_quiet(self, track: _AgentTrack, seen: set[str]) -> None:
        try:
            live = self._store.live_for_agent(track.agent.name)
        except Exception as exc:
            log.warning("could not list open incidents for %s: %s", track.agent.name, exc)
            return
        for incident in live:
            if incident.fingerprint in seen:
                continue
            count = track.clean.get(incident.fingerprint, 0) + 1
            if count < max(1, track.agent.resolve_after):
                track.clean[incident.fingerprint] = count
                continue
            track.clean.pop(incident.fingerprint, None)
            try:
                self._store.resolve(incident.incident_id, self._ctx.now())
            except Exception as exc:
                log.warning("could not resolve #%s: %s", incident.incident_id, exc)
                continue
            self._notifier.send(format_resolved(incident.title, incident.incident_id, incident.severity))

    def run_forever(self, tick_seconds: float = 5.0, should_stop: Optional[Callable[[], bool]] = None,
                    sleep: Callable[[float], None] = time.sleep) -> None:
        """Run agents as they fall due until ``should_stop``, which is asked only between rounds."""
        while not (should_stop and should_stop()):
            self.run_due()
            sleep(tick_seconds)
