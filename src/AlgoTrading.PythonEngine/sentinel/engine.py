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
"""
from __future__ import annotations

import logging
import time
import traceback
from dataclasses import dataclass, field
from typing import Optional

from sentinel.agents.base import Agent
from sentinel.context import SentinelContext
from sentinel.model import Finding, Severity
from sentinel.notify import Notifier, format_opened, format_resolved
from sentinel.store import IncidentStore

log = logging.getLogger("sentinel.engine")


@dataclass
class _AgentTrack:
    agent: Agent
    next_due: float = 0.0
    clean: dict[str, int] = field(default_factory=dict)  # fingerprint -> consecutive clean checks


class SentinelEngine:
    def __init__(self, agents: list[Agent], store: IncidentStore, notifier: Notifier,
                 ctx: SentinelContext, monotonic=time.monotonic) -> None:
        self._tracks = [_AgentTrack(a) for a in agents]
        self._store = store
        self._notifier = notifier
        self._ctx = ctx
        self._monotonic = monotonic

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
            self._beat()
        return ran

    def run_all_once(self) -> None:
        """Every agent, once, regardless of cadence (``--once``)."""
        self._ctx.fresh_cycle()
        for track in self._tracks:
            self.run_agent(track)
        self._beat()

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
            self._notifier.send(format_opened(finding, 0))
            return

        if result.is_new or result.escalated:
            text = format_opened(finding, result.incident_id, escalated=result.escalated)
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

    def run_forever(self, tick_seconds: float = 5.0, should_stop: Optional[callable] = None) -> None:
        while not (should_stop and should_stop()):
            self.run_due()
            time.sleep(tick_seconds)
