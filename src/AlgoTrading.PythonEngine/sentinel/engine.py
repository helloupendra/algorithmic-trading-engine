"""
Turning findings into incidents.

Each agent runs on its own cadence. What it returns is upserted by fingerprint:
a new fingerprint opens an incident and sends a message; one already open is
updated quietly, unless its severity went up, which is worth a second message.
An open incident an agent stops reporting is counted clean; after
``resolve_after`` clean checks in a row it is resolved, with a message.

Not everything is worth a message. A low finding is kept for the console only
(its escalation to medium or above is sent). A notice — a one-off event such as
a privileged change, marked ``extra={"notice": True}`` — is sent when it opens
and closes on the next check without a "resolved" message, since nothing was
ever open.

A person can also resolve an incident from the console. If the condition is
still there, the next check opens a fresh one — which is the honest answer to
"I resolved it and it came back".

An incident that opens, or escalates to high or critical, gets a context pack
(sentinel/pack.py): the last deploy, the live commit, the runs, the log lines
around it. It rides in the incident's evidence and in the message; two minutes
later the log lines that came after are added to the evidence, quietly.

Messages go through an outbox. One that Telegram does not take (unreachable, a
429) stays there and is tried again with a growing pause, in order, a few per
round, without holding up the checks; one delivered late says so. On start,
live incidents whose message never went out (NotifiedUtc empty) join it.

When the database does not take a finding, Telegram is still told — once per
problem, again only if it gets worse, and not at all for a problem the
database already holds at that severity — not on every sighting.
"""
from __future__ import annotations

import logging
import time
import traceback
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, Callable, Optional

from sentinel.agents.base import Agent
from sentinel.clock import to_ist
from sentinel.context import SentinelContext
from sentinel.model import CONTEXT_PREFIX, Finding, Severity
from sentinel.notify import Notifier, format_opened, format_resolved
from sentinel.pack import LOG_WINDOW, ContextPack, Pack
from sentinel.store import IncidentStore

log = logging.getLogger("sentinel.engine")

#: At most this many messages go out in one round: after an outage the backlog
#: drains at about one a second without holding a round up for long.
SEND_PER_ROUND = 5
#: After a failed send the outbox waits this long, doubling on each failure up
#: to RETRY_MAX_SECONDS, before it tries again.
RETRY_FIRST_SECONDS = 15.0
RETRY_MAX_SECONDS = 300.0
#: Telegram's own retry_after (a 429) is honoured up to this long.
SERVICE_WAIT_MAX_SECONDS = 900.0
#: At most this many messages wait; past it the mildest, oldest one is dropped
#: (the incident itself is still in the console).
OUTBOX_MAX = 50
#: A message delivered this long after it was due says when it was due.
LATE_AFTER_SECONDS = 90.0
#: On start, at most this many live incidents with no message sent are sent now.
UNSENT_ON_START = 20
#: A round that fails again the same way is logged at most this often.
ROUND_ERROR_LOG_SECONDS = 600.0


@dataclass
class _AgentTrack:
    agent: Agent
    next_due: float = 0.0
    clean: dict[str, int] = field(default_factory=dict)  # fingerprint -> consecutive clean checks


@dataclass
class _Message:
    """One message waiting in the outbox."""

    text: str
    severity: Severity
    due: datetime                        # when it should have gone out, for "sent late"
    incident_id: Optional[int] = None    # an incident's opening message: marked notified once delivered


@dataclass
class _Unstored:
    """A problem the database did not take, and what a person was told about it."""

    agent: str
    severity: Severity                   # the severity messaged (a low one is messaged nothing)
    title: str
    notice: bool = False
    clean: int = 0


# Outbox keys. A stored incident's messages are keyed by its number, so an
# escalation replaces its own waiting opening and a problem that comes back
# (a new number) never overwrites the last one's; one the database did not
# take has only its fingerprint.
def _open_key(incident_id: int) -> str:
    return f"open:#{incident_id}"


def _unstored_key(fingerprint: str) -> str:
    return f"open-unstored:{fingerprint}"


def _malformed(item: Any) -> Optional[str]:
    """Why ``item`` cannot be recorded as a finding; None when it can."""
    if not isinstance(item, Finding):
        return f"{type(item).__name__} instead of a Finding: {repr(item)[:120]}"
    if not isinstance(item.severity, Severity):
        return f"{item.fingerprint!r}: severity {item.severity!r} is not a Severity"
    for name in ("agent", "rule", "title", "summary", "where", "suggestion"):
        if not isinstance(getattr(item, name), str):
            return f"{item.fingerprint!r}: {name} is {type(getattr(item, name)).__name__}, not text"
    if not isinstance(item.evidence, (list, tuple)) or not all(isinstance(e, str) for e in item.evidence):
        return f"{item.fingerprint!r}: evidence is not a list of text"
    if not isinstance(item.extra, dict):
        return f"{item.fingerprint!r}: extra is {type(item.extra).__name__}, not a dict"
    return None


def _crash_finding(agent: Agent, summary: str, kind: str, evidence: list[str], suggestion: str) -> Finding:
    return Finding(
        agent=agent.name,
        rule="agent-crashed",
        severity=Severity.MEDIUM,
        title=f"Sentinel's {agent.name} agent failed to run its check",
        summary=summary,
        fingerprint=f"{agent.name}:agent-crashed:{kind}",
        where=f"sentinel/agents/{agent.name}.py",
        evidence=evidence,
        suggestion=suggestion,
    )


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
        self._outbox: dict[str, _Message] = {}   # in the order they are to go out
        self._retry_at = 0.0
        self._send_failures = 0
        self._send_budget = SEND_PER_ROUND   # what is left of this round's sends
        self._unstored: dict[str, _Unstored] = {}
        # agent -> {fingerprint: (incident id, severity)} of its live incidents, as last read or written.
        self._live: dict[str, dict[str, tuple[int, Severity]]] = {}
        self._unsent_loaded = False
        self._round_error: tuple[str, float] = ("", 0.0)

    # ------------------------------------------------------------------ rounds

    def run_due(self) -> int:
        """Run every agent whose turn has come. Returns how many ran."""
        now = self._monotonic()
        ran = 0
        self._ctx.fresh_cycle()
        self._send_budget = SEND_PER_ROUND
        self._load_unsent()
        for track in self._tracks:
            if now < track.next_due:
                continue
            track.next_due = now + max(5, track.agent.interval_seconds)
            self._run_guarded(track)
            ran += 1
        if ran:
            self._mark()
            self._add_later_lines()
            self._beat()
        self._deliver()
        return ran

    def run_all_once(self) -> None:
        """Every agent, once, regardless of cadence (``--once``)."""
        self._ctx.fresh_cycle()
        self._send_budget = SEND_PER_ROUND
        self._load_unsent()
        for track in self._tracks:
            self._run_guarded(track)
        self._mark()
        self._beat()
        self._deliver()

    def run_forever(self, tick_seconds: float = 5.0, should_stop: Optional[Callable[[], bool]] = None,
                    sleep: Callable[[float], None] = time.sleep) -> None:
        """Run agents as they fall due until ``should_stop``, which is asked only between rounds."""
        while not (should_stop and should_stop()):
            try:
                self.run_due()
            except Exception as exc:
                # A watchman that dies of its own bug is the silence this exists
                # to prevent. A round that fails before its end writes no
                # heartbeat, so if every round fails the console still says
                # Sentinel has gone quiet.
                self._log_round_error(exc)
            sleep(tick_seconds)

    def _log_round_error(self, exc: Exception) -> None:
        what = f"{type(exc).__name__}: {exc}"
        now = self._monotonic()
        last, at = self._round_error
        if what != last or now - at >= ROUND_ERROR_LOG_SECONDS:
            self._round_error = (what, now)
            log.exception("a round of checks failed; carrying on with the next")

    def _run_guarded(self, track: _AgentTrack) -> None:
        try:
            self.run_agent(track)
        except Exception:
            log.exception("recording what the %s agent found failed; the other agents still run",
                          track.agent.name)
        self._deliver()   # a message goes out as soon as its agent is done, not after every agent

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

    # ------------------------------------------------------------------ one agent

    def _check(self, agent: Agent) -> tuple[list[Finding], bool]:
        """
        What the agent found, and whether its check was whole. A check that
        raised, or returned something that is not a list of findings, is
        reported as agent-crashed and is not whole: what it did not get to say
        is not evidence that a problem has gone.
        """
        try:
            raw = agent.check(self._ctx)
            if raw is None:
                raise TypeError("check() returned None instead of a list of findings")
            items = list(raw)
        except Exception as exc:  # a broken watcher is itself something to report
            return [_crash_finding(
                agent, f"{type(exc).__name__}: {exc}", type(exc).__name__,
                traceback.format_exc().strip().splitlines()[-6:],
                "Read the traceback; the agent's other checks did not run this cycle.",
            )], False

        good: list[Finding] = []
        bad: list[str] = []
        for item in items:
            why = _malformed(item)
            if why is None:
                good.append(item)
            else:
                bad.append(why)
        if not bad:
            return good, True
        good.append(_crash_finding(
            agent, f"check() returned {len(bad)} result(s) that are not findings Sentinel can record: {bad[0]}",
            "MalformedFinding", bad[:6],
            "A bug in the agent. Its well-formed findings were recorded; nothing it reports is resolved "
            "until a check comes back clean.",
        ))
        return good, False

    def run_agent(self, track: _AgentTrack) -> None:
        agent = track.agent
        started = time.monotonic()
        findings, whole = self._check(agent)
        log.debug("%s: %d finding(s) in %.1fs", agent.name, len(findings), time.monotonic() - started)

        seen: set[str] = set()
        for finding in findings:
            if finding.fingerprint in seen:
                continue  # one sighting per check, however many times an agent repeats itself
            seen.add(finding.fingerprint)
            self._record(finding)
            track.clean.pop(finding.fingerprint, None)

        if not whole:
            # The agent's other incidents are neither gone nor a clean check
            # nearer to resolving: resolving them now would close problems
            # that are still happening, with a message saying they are over.
            return
        self._forget_unstored(track, seen)
        self._resolve_quiet(track, seen)

    # ------------------------------------------------------------------ recording

    def _record(self, finding: Finding) -> None:
        now = self._ctx.now()
        fingerprint = finding.fingerprint
        notice = bool(finding.extra.get("notice"))
        try:
            result = self._store.upsert(finding, now)
        except Exception as exc:
            self._unstorable(finding, notice, now, exc)
            return

        self._live.setdefault(finding.agent, {})[fingerprint] = (result.incident_id, result.severity)
        told = self._unstored.pop(fingerprint, None)
        if told is not None and told.severity is Severity.LOW:
            told = None   # a low one was messaged nothing
        if notice:
            self._remember_notice(fingerprint, finding.agent)
        if not (result.is_new or result.escalated):
            return

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
        if result.severity is Severity.LOW:
            return   # the console's, not a message: see the module docstring
        context = pack.lines if pack else None

        if told is not None and told.severity.rank >= result.severity.rank:
            # A person was told while the database was not taking writes; now
            # it has a number. Not a second message: the waiting one (if it has
            # not gone yet) carries the number, or the incident is marked sent.
            waiting = self._outbox.get(_unstored_key(fingerprint))
            if waiting is not None:
                waiting.text = format_opened(finding, result.incident_id, escalated=result.escalated,
                                             context=context)
                waiting.incident_id = result.incident_id
            else:
                self._mark_notified(result.incident_id)
            return

        text = format_opened(finding, result.incident_id, escalated=result.escalated or told is not None,
                             context=context)
        self._queue(_open_key(result.incident_id), text, result.severity, now, incident_id=result.incident_id)

    def _unstorable(self, finding: Finding, notice: bool, now: datetime, exc: Exception) -> None:
        """
        The database did not take a finding. Telegram hears of the problem
        once, and again only if it gets worse — not on every sighting, which
        during an outage would be several messages a minute until Telegram
        refused them. A problem already live in the database at this severity
        was messaged when it opened, and is not messaged again.
        """
        fingerprint = finding.fingerprint
        told = self._unstored.get(fingerprint)
        if told is not None:
            told.clean = 0
        known = self._live.get(finding.agent, {}).get(fingerprint)
        before = [s for s in (told.severity if told else None, known[1] if known else None) if s is not None]
        worst = max(before, key=lambda s: s.rank) if before else None
        if worst is not None and finding.severity.rank <= worst.rank:
            log.debug("still could not store %s: %s", fingerprint, exc)
            return

        log.error("could not store %s: %s", fingerprint, exc)
        if told is None:
            self._unstored[fingerprint] = _Unstored(finding.agent, finding.severity, finding.title, notice)
        else:
            told.severity, told.title = finding.severity, finding.title
        if finding.severity is Severity.LOW:
            return
        # Still tell someone: a problem nobody hears about is the failure this exists to prevent.
        pack = self._context(now, now)
        escalated = worst is not None and worst is not Severity.LOW
        text = format_opened(finding, known[0] if known else 0, escalated=escalated,
                             context=pack.lines if pack else None)
        self._queue(_unstored_key(fingerprint), text, finding.severity, now)

    def _forget_unstored(self, track: _AgentTrack, seen: set[str]) -> None:
        """Problems that were never stored and have stopped: counted clean like any incident, then let go."""
        for fingerprint, told in list(self._unstored.items()):
            if told.agent != track.agent.name or fingerprint in seen:
                continue
            told.clean += 1
            if told.clean < max(1, track.agent.resolve_after):
                continue
            del self._unstored[fingerprint]
            if fingerprint in self._live.get(told.agent, {}):
                continue   # a stored incident: resolved from the database, once it answers, with its number
            if told.notice or told.severity is Severity.LOW:
                continue
            self._queue(f"resolved-unstored:{fingerprint}", format_resolved(told.title, 0, told.severity),
                        told.severity, self._ctx.now())

    def _resolve_quiet(self, track: _AgentTrack, seen: set[str]) -> None:
        name = track.agent.name
        try:
            live = self._store.live_for_agent(name)
        except Exception as exc:
            log.warning("could not list open incidents for %s: %s", name, exc)
            return
        self._live[name] = {i.fingerprint: (i.incident_id, i.severity) for i in live}
        live_now = {i.fingerprint for i in live}
        track.clean = {fp: n for fp, n in track.clean.items() if fp in live_now}
        self._forget_notices(name, live_now | seen)

        for incident in live:
            if incident.fingerprint in seen:
                continue
            count = track.clean.get(incident.fingerprint, 0) + 1
            if count < max(1, track.agent.resolve_after):
                track.clean[incident.fingerprint] = count
                continue
            track.clean.pop(incident.fingerprint, None)
            try:
                changed = self._store.resolve(incident.incident_id, self._ctx.now())
            except Exception as exc:
                log.warning("could not resolve #%s: %s", incident.incident_id, exc)
                continue
            self._live[name].pop(incident.fingerprint, None)
            notice = self._forget_notice(incident.fingerprint)
            if not changed:
                # Someone resolved it from the console between the listing and
                # now: it is over already, and was said to be over there.
                log.info("#%s was no longer live when its checks came back clean; nothing to announce",
                         incident.incident_id)
                continue
            if notice or incident.severity is Severity.LOW:
                log.info("resolved #%s without a message (%s)", incident.incident_id,
                         "a notice" if notice else "low")
                continue
            self._queue(f"resolved:#{incident.incident_id}",
                        format_resolved(incident.title, incident.incident_id, incident.severity),
                        incident.severity, self._ctx.now())

    # ------------------------------------------------------------------ notices

    def _notices(self) -> dict[str, str]:
        """
        Fingerprint -> agent of the live notices. Kept in a file, not only in
        memory: Sentinel restarts on every deploy of its own code, and a notice
        still live across a restart must close as quietly as one that is not.
        """
        state = self._ctx.state("engine")
        notices = state.data.get("notices")
        if not isinstance(notices, dict):
            notices = state.data["notices"] = {}
        return notices

    def _remember_notice(self, fingerprint: str, agent: str) -> None:
        notices = self._notices()
        if notices.get(fingerprint) != agent:
            notices[fingerprint] = agent
            self._ctx.state("engine").save()

    def _forget_notice(self, fingerprint: str) -> bool:
        notices = self._notices()
        if fingerprint not in notices:
            return False
        del notices[fingerprint]
        self._ctx.state("engine").save()
        return True

    def _forget_notices(self, agent: str, keep: set[str]) -> None:
        """Notices of this agent no longer live (a person resolved them from the console)."""
        notices = self._notices()
        gone = [fp for fp, owner in notices.items() if owner == agent and fp not in keep]
        for fp in gone:
            del notices[fp]
        if gone:
            self._ctx.state("engine").save()

    # ------------------------------------------------------------------ delivery

    def _queue(self, key: str, text: str, severity: Severity, due: datetime,
               incident_id: Optional[int] = None) -> None:
        """
        A message to send. A newer message for the same key (an escalation of
        an opening that has not gone yet) replaces the waiting one in its place.
        """
        waiting = self._outbox.get(key)
        if waiting is not None:
            waiting.text, waiting.severity, waiting.due = text, severity, due
            waiting.incident_id = incident_id if incident_id is not None else waiting.incident_id
            return
        if len(self._outbox) >= OUTBOX_MAX:
            drop = min(self._outbox, key=lambda k: self._outbox[k].severity.rank)   # the mildest, oldest first
            log.error("%d messages are waiting; dropped %s (its incident is still in the console)",
                      len(self._outbox), drop)
            del self._outbox[drop]
        self._outbox[key] = _Message(text, severity, due, incident_id)

    def _deliver(self) -> None:
        """
        Send what is waiting, in order, a few at a time. The first failure
        ends the attempt: whatever made it fail (no network, a 429) holds for
        the rest too, and a round must not sit through a timeout per message.
        """
        if not self._outbox or self._send_budget <= 0:
            return
        now = self._monotonic()
        if now < self._retry_at:
            return
        while self._outbox and self._send_budget > 0:
            self._send_budget -= 1
            key, message = next(iter(self._outbox.items()))
            try:
                delivered = self._notifier.send(message.text + self._late(message.due))
            except Exception as exc:
                log.warning("the notifier failed: %s: %s", type(exc).__name__, exc)
                delivered = False
            if delivered:
                del self._outbox[key]
                self._send_failures = 0
                if message.incident_id is not None:
                    self._mark_notified(message.incident_id)
                continue
            if getattr(self._notifier, "refused", False):
                del self._outbox[key]
                log.error("Telegram refused the message for %s as such; it is not sent again", key)
                continue
            self._send_failures += 1
            wait = min(RETRY_MAX_SECONDS, RETRY_FIRST_SECONDS * 2 ** min(self._send_failures - 1, 10))
            asked = getattr(self._notifier, "retry_after", None)
            if isinstance(asked, (int, float)) and asked > 0:
                wait = max(wait, min(float(asked), SERVICE_WAIT_MAX_SECONDS))
            self._retry_at = now + wait
            log.warning("a message was not delivered; %d waiting, next try in %.0f s", len(self._outbox), wait)
            return

    def _late(self, due: datetime) -> str:
        """A line for a message going out well after it was due, so it is not read as happening now."""
        now = self._ctx.now()
        if (now - due).total_seconds() < LATE_AFTER_SECONDS:
            return ""
        when = to_ist(due)
        stamp = when.strftime("%H:%M") if when.date() == to_ist(now).date() else when.strftime("%d %b %H:%M")
        return f"\n\n(Sent late: this was due at {stamp} IST and could not be delivered then.)"

    def _mark_notified(self, incident_id: int) -> None:
        try:
            self._store.mark_notified(incident_id, self._ctx.now())
        except Exception as exc:
            log.debug("could not mark #%s as sent: %s", incident_id, exc)

    def _load_unsent(self) -> None:
        """
        Once, at start: live incidents whose message never went out — a send
        that failed before this process started — go into the outbox. A store
        that cannot be asked is asked again next round.
        """
        if self._unsent_loaded:
            return
        try:
            rows = self._store.unnotified_live(UNSENT_ON_START)
        except Exception as exc:
            log.warning("could not look for incidents whose message never went out: %s", exc)
            return
        self._unsent_loaded = True
        for row in rows:
            if row.severity is Severity.LOW:
                continue
            try:
                finding = Finding(agent=row.agent, rule=row.rule, severity=row.severity, title=row.title,
                                  summary=row.summary, fingerprint=row.fingerprint, where=row.where,
                                  evidence=[e for e in row.evidence if not e.startswith(CONTEXT_PREFIX)],
                                  suggestion=row.suggestion)
            except ValueError as exc:
                log.warning("could not rebuild the message of #%s: %s", row.incident_id, exc)
                continue
            context = [e for e in row.evidence if e.startswith(CONTEXT_PREFIX)]
            self._queue(_open_key(row.incident_id), format_opened(finding, row.incident_id, context=context or None),
                        row.severity, row.first_seen_utc or self._ctx.now(), incident_id=row.incident_id)
        if rows:
            log.info("%d live incident(s) had no message sent; sending them now", len(rows))
