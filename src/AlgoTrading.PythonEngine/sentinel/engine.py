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
"I resolved it and it came back". A finding an agent repeats from memory (a log
finding held open after its last line) carries when it was last observed, and
does not reopen an incident resolved at or after that: on 28 Sep an operator
resolved four API errors of a deliberate Postgres restart at 13:08, and their
held findings opened them again at 13:11, counting the same 13:05-13:06 lines.
The agent is told (``let_go``), so it stops holding them.

A problem that comes back within FLAP_WINDOW (30 min) of Sentinel resolving it
is the same incident, reopened rather than opened anew (store.py; not a notice,
and not an incident a person resolved). 28 Sep 13:06-13:28: the feed stalled
for ~2 min every few minutes, and each stall opened a new CRITICAL incident per
exchange group, each with its NEW and its RESOLVED message. Now a flapping
incident is messaged when it opens (at once, as ever) and when it comes back
after its RESOLVED went out ("Back again: the 2nd time since 13:06"). From then
on it is quiet: a return is messaged only when nothing has been said about it
for FLAP_WINDOW, and its RESOLVED is held back until it has stayed clear for
FLAP_WINDOW, or dropped when it comes back first. So a person is never left
believing it is over while it is not. The episode count and what was last said
live in the engine's state file, like the notices, across restarts.

An incident that opens, or escalates to high or critical, gets a context pack
(sentinel/pack.py): the last deploy, the live commit, the runs, the log lines
around it. It rides in the incident's evidence and in the message; two minutes
later the log lines that came after are added to the evidence, quietly.

An incident that opens for a problem with earlier resolved episodes (the same
fingerprint) says so, in the message and as ``history: `` evidence lines: how
many times, when last, and what a person wrote was done the last time. Read
after the incident is stored, so a failed read loses only those lines.

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
from datetime import datetime, timedelta
from typing import Any, Callable, Optional

from sentinel.agents.base import Agent
from sentinel.clock import to_ist
from sentinel.context import SentinelContext
from sentinel.model import CONTEXT_PREFIX, FLAP_WINDOW, HISTORY_PREFIX, KEPT_PREFIXES, Finding, Severity
from sentinel.notify import (Notifier, format_again, format_flapping, format_opened, format_resolved,
                             format_seen_before, format_settled)
from sentinel.pack import LOG_WINDOW, ContextPack, Pack
from sentinel.store import IncidentStore, LiveIncident, Upserted

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
#: A flapping incident's bookkeeping left untouched this long (no return, no
#: resolve) is let go: the incident is live and steady, and if it resolves its
#: RESOLVED simply goes out at once, as any incident's does.
FLAP_FORGET_AFTER = timedelta(days=1)


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
    observed = item.observed_utc
    if observed is not None and (not isinstance(observed, datetime) or observed.tzinfo is None):
        return f"{item.fingerprint!r}: observed_utc {observed!r} is not an aware datetime"
    return None


def _parse_time(value: Any) -> Optional[datetime]:
    """An ISO time from the engine's state file; None for anything else, or one without a zone."""
    if not isinstance(value, str):
        return None
    try:
        moment = datetime.fromisoformat(value)
    except ValueError:
        return None
    return moment if moment.tzinfo is not None else None


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
            self._settle_flaps()
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
        self._settle_flaps()
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
        stale: set[str] = set()
        for finding in findings:
            if finding.fingerprint in seen:
                continue  # one sighting per check, however many times an agent repeats itself
            seen.add(finding.fingerprint)
            if self._record(finding):
                stale.add(finding.fingerprint)
            track.clean.pop(finding.fingerprint, None)
        if stale:
            try:
                agent.let_go(self._ctx, stale)
            except Exception as exc:
                log.warning("the %s agent could not let go of %d resolved finding(s): %s", agent.name, len(stale), exc)

        if not whole:
            # The agent's other incidents are neither gone nor a clean check
            # nearer to resolving: resolving them now would close problems
            # that are still happening, with a message saying they are over.
            return
        self._forget_unstored(track, seen)
        self._resolve_quiet(track, seen)

    # ------------------------------------------------------------------ recording

    def _record(self, finding: Finding) -> bool:
        """Store a finding and say what needs saying. True when it was old news (see Finding.observed_utc)."""
        now = self._ctx.now()
        fingerprint = finding.fingerprint
        notice = bool(finding.extra.get("notice"))
        try:
            result = self._store.upsert(finding, now)
        except Exception as exc:
            self._unstorable(finding, notice, now, exc)
            return False

        if result.stale:
            # Resolved at or after everything behind this finding was observed:
            # nothing was written, and there is nothing to say.
            self._unstored.pop(fingerprint, None)
            log.debug("%s: #%s was resolved after this was last observed; not reopened", fingerprint,
                      result.incident_id)
            return True

        self._live.setdefault(finding.agent, {})[fingerprint] = (result.incident_id, result.severity)
        told = self._unstored.pop(fingerprint, None)
        if told is not None and told.severity is Severity.LOW:
            told = None   # a low one was messaged nothing
        if notice:
            self._remember_notice(fingerprint, finding.agent)
        if result.reopened:
            self._reopened(finding, result, now, told)
            return False
        if not (result.is_new or result.escalated):
            return False

        history = self._seen_before(fingerprint, result.incident_id) if result.is_new else []
        pack = None
        # A low incident turning medium is worth a message, not a second look around.
        if result.is_new or result.severity.rank >= Severity.HIGH.rank:
            pack = self._context(result.first_seen_utc or now, now)
        added = history + (pack.lines if pack is not None else [])
        if added:
            try:
                self._store.attach_context(result.incident_id, added)
                if pack is not None and pack.lines:
                    self._later[result.incident_id] = pack   # a newer pack replaces a waiting one
            except Exception as exc:
                log.warning("could not keep the context of #%s: %s", result.incident_id, exc)
        if result.severity is Severity.LOW:
            return False   # the console's, not a message: see the module docstring
        context = pack.lines if pack else None

        if told is not None and told.severity.rank >= result.severity.rank:
            # A person was told while the database was not taking writes; now
            # it has a number. Not a second message: the waiting one (if it has
            # not gone yet) carries the number, or the incident is marked sent.
            waiting = self._outbox.get(_unstored_key(fingerprint))
            if waiting is not None:
                waiting.text = format_opened(finding, result.incident_id, escalated=result.escalated,
                                             context=context, history=history)
                waiting.incident_id = result.incident_id
            else:
                self._mark_notified(result.incident_id)
            return False

        text = format_opened(finding, result.incident_id, escalated=result.escalated or told is not None,
                             context=context, history=history)
        self._queue(_open_key(result.incident_id), text, result.severity, now, incident_id=result.incident_id)
        return False

    def _seen_before(self, fingerprint: str, incident_id: int) -> list[str]:
        """
        Whether the problem just opened has happened before, as evidence lines;
        none when it has not, or when the store cannot say. Asked after the
        incident is stored and never inside its insert: a failed read costs
        this line, never the incident or its message.
        """
        try:
            earlier = self._store.earlier_episodes(fingerprint, incident_id)
        except Exception as exc:
            log.warning("could not read the earlier episodes of %s: %s", fingerprint, exc)
            return []
        if earlier is None:
            return []
        return format_seen_before(earlier.count, earlier.last_seen_utc, earlier.last_resolution)

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
        self._forget_flaps(name, {i.incident_id for i in live})

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
            if self._hold_resolved(incident):
                continue
            if notice or incident.severity is Severity.LOW:
                log.info("resolved #%s without a message (%s)", incident.incident_id,
                         "a notice" if notice else "low")
                continue
            self._queue(f"resolved:#{incident.incident_id}",
                        format_resolved(incident.title, incident.incident_id, incident.severity),
                        incident.severity, self._ctx.now())

    # ------------------------------------------------------------------ flapping

    def _flaps(self) -> dict[str, dict]:
        """
        Incident number -> its flapping so far: how many episodes, since when,
        what the last message about it said ("open" or "resolved") and when,
        and when Sentinel resolved it while its RESOLVED is held back. Kept in
        the engine's state file, like the notices: Sentinel restarts on every
        deploy of its own code, and one in the middle of a flapping feed must
        not start the count, or the quiet, again.
        """
        state = self._ctx.state("engine")
        flaps = state.data.get("flapping")
        if not isinstance(flaps, dict):
            flaps = state.data["flapping"] = {}
        return flaps

    def _save_flaps(self) -> None:
        self._ctx.state("engine").save()

    def _reopened(self, finding: Finding, result: Upserted, now: datetime, told: Optional[_Unstored]) -> None:
        """
        A problem Sentinel resolved less than FLAP_WINDOW ago is back, and its
        incident is live again. Said at once when the last word a person had
        about it was "resolved" (a real outage must never wait behind a flap),
        when it got worse, or when nothing has been said about it for
        FLAP_WINDOW. Otherwise it comes back quietly: they were told it is
        back, and no RESOLVED has gone out since.
        """
        flaps = self._flaps()
        key = str(result.incident_id)
        entry = flaps.get(key)
        if not isinstance(entry, dict):
            # Its first return. A RESOLVED is held back only once an incident
            # has come back, so the one before this went out: the last word a
            # person had was "resolved".
            entry = {"episodes": 1, "since": (result.first_seen_utc or now).isoformat(), "told": "resolved",
                     "told_at": None}
        entry["episodes"] = int(entry.get("episodes") or 1) + 1
        entry.update(agent=finding.agent, fingerprint=finding.fingerprint, title=finding.title,
                     severity=result.severity.value, resolved_at=None, touched=now.isoformat())
        flaps[key] = entry
        episodes = entry["episodes"]
        since = _parse_time(entry.get("since")) or result.first_seen_utc or now
        try:
            self._store.attach_context(result.incident_id, [format_flapping(episodes, since, now)])
        except Exception as exc:
            log.warning("could not note the flapping of #%s: %s", result.incident_id, exc)

        told_at = _parse_time(entry.get("told_at"))
        said_lately = entry.get("told") == "open" and told_at is not None and now - told_at < FLAP_WINDOW
        if result.severity is Severity.LOW or (said_lately and not result.escalated):
            self._save_flaps()
            log.info("#%s is back (episode %d); not messaged: said to be back at %s and not resolved since",
                     result.incident_id, episodes, entry.get("told_at"))
            return
        entry["told"], entry["told_at"] = "open", now.isoformat()
        self._save_flaps()
        if told is not None and told.severity.rank >= result.severity.rank:
            # A person was told while the database was not taking writes: that was the message.
            waiting = self._outbox.get(_unstored_key(finding.fingerprint))
            if waiting is not None:
                waiting.incident_id = result.incident_id
            else:
                self._mark_notified(result.incident_id)
            return

        pack = self._context(now, now)   # around this return, not around the first episode
        if pack is not None and pack.lines:
            try:
                self._store.attach_context(result.incident_id, pack.lines)
                self._later[result.incident_id] = pack
            except Exception as exc:
                log.warning("could not keep the context of #%s: %s", result.incident_id, exc)
        text = format_opened(finding, result.incident_id, escalated=result.escalated,
                             context=pack.lines if pack else None, again=format_again(episodes, since, now))
        self._queue(_open_key(result.incident_id), text, result.severity, now, incident_id=result.incident_id)

    def _hold_resolved(self, incident: LiveIncident) -> bool:
        """
        Sentinel has just resolved ``incident``. True when it has flapped: its
        RESOLVED waits until it has stayed clear for FLAP_WINDOW
        (:meth:`_settle_flaps`) and is dropped if it comes back first —
        "resolved" and "back again" minutes apart, over and over, was 28 Sep.
        """
        entry = self._flaps().get(str(incident.incident_id))
        if not isinstance(entry, dict):
            return False
        now = self._ctx.now()
        entry.update(resolved_at=now.isoformat(), touched=now.isoformat(), title=incident.title,
                     severity=incident.severity.value)
        self._save_flaps()
        log.info("resolved #%s (episode %s of a flapping problem); its RESOLVED waits until it stays clear",
                 incident.incident_id, entry.get("episodes"))
        return True

    def _settle_flaps(self) -> None:
        """A flapping incident clear for FLAP_WINDOW gets its RESOLVED now, saying how often it happened."""
        flaps = self._flaps()
        if not flaps:
            return
        now = self._ctx.now()
        changed = False
        for key, entry in list(flaps.items()):
            entry = entry if isinstance(entry, dict) else {}
            resolved_at = _parse_time(entry.get("resolved_at"))
            if resolved_at is None:
                touched = _parse_time(entry.get("touched"))
                if touched is None or now - touched > FLAP_FORGET_AFTER:
                    del flaps[key]
                    changed = True
                continue
            if now - resolved_at < FLAP_WINDOW:
                continue
            del flaps[key]
            changed = True
            try:
                severity = Severity(entry.get("severity"))
                incident_id = int(key)
            except (TypeError, ValueError):
                continue
            if entry.get("told") != "open" or severity is Severity.LOW:
                continue   # nobody was told it was back, so nobody is waiting to hear it is over
            note = format_settled(int(entry.get("episodes") or 2), _parse_time(entry.get("since")), resolved_at, now)
            self._queue(f"resolved:#{incident_id}",
                        format_resolved(str(entry.get("title") or ""), incident_id, severity, note=note),
                        severity, now)
        if changed:
            self._save_flaps()

    def _forget_flaps(self, agent: str, live_ids: set[int]) -> None:
        """
        The flapping of this agent's incidents that are no longer live and
        that Sentinel did not resolve: a person resolved them from the console,
        and if the problem comes back it is a new incident.
        """
        flaps = self._flaps()
        gone = [key for key, entry in flaps.items()
                if isinstance(entry, dict) and entry.get("agent") == agent and not entry.get("resolved_at")
                and key.isdigit() and int(key) not in live_ids]
        for key in gone:
            del flaps[key]
        if gone:
            self._save_flaps()

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
                                  evidence=[e for e in row.evidence if not e.startswith(KEPT_PREFIXES)],
                                  suggestion=row.suggestion)
            except ValueError as exc:
                log.warning("could not rebuild the message of #%s: %s", row.incident_id, exc)
                continue
            context = [e for e in row.evidence if e.startswith(CONTEXT_PREFIX)]
            history = [e for e in row.evidence if e.startswith(HISTORY_PREFIX)]
            self._queue(_open_key(row.incident_id),
                        format_opened(finding, row.incident_id, context=context or None, history=history),
                        row.severity, row.first_seen_utc or self._ctx.now(), incident_id=row.incident_id)
        if rows:
            log.info("%d live incident(s) had no message sent; sending them now", len(rows))
