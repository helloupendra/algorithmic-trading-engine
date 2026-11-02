"""
strategies/signal_booking.py

Booking a live strategy's signals with the API exactly once, or telling the
strategy they were not booked.

A strategy changes its own state inside `on_bar`: by the time the runner posts
an OPEN_GROUP, the strategy already believes the group is open. The runner used
to post each signal once. A ConnectionError or a timeout fell to the per-tick
catch-all and dropped the rest of the tick's signals; a refusal was re-raised.
On 24 Sep runner-214 had two OPEN_GROUPs refused with 409 "RATE LIMIT
EXCEEDED", each logged as "NOT booked. The strategy still believes this group
is open." — and it went on believing it.

Two pieces fix that:

- `SignalBooker` posts one signal and retries it when the answer is lost
  (ConnectionError, a timeout, 502/503/504) — an OPEN_GROUP for about 5 s, sent
  again without prices so the API prices it from the live quote, a CLOSE_GROUP
  for about a minute. Every attempt carries the same `clientSignalId`, and the
  API answers a known id with the row it already booked, so a retry after a
  post that did land books nothing twice.
- `run_tick` keeps a deep copy of the strategy's state from before `on_bar`,
  and when a signal of the tick is not booked (refused, or never reached the
  API in its retries) it hands back that copy: the strategy is where it was
  before the tick, and emits the signal again if it still wants it.

An OPEN_GROUP whose answer was lost is neither booked nor not booked, and it is
never reported as not booked. Until 2 Nov a timeout followed by another timeout,
or by a 503, put the strategy back; it emitted the OPEN again under a new id and
the API booked a second position that nothing in the strategy would ever exit.
Now the outcome stays unknown (`Booking.unconfirmed`, `SIGNAL UNCONFIRMED` in the
log) until the API says which it was:

- The strategy keeps its state, because believing it is open is the safe mistake:
  its later close is reduce-only. The runner does not evaluate it at all until
  the answer comes (`settle_tick`), so it cannot emit anything that would have to
  be ordered around the OPEN, and the rest of its tick waits behind it.
- The same `clientSignalId` is posted again 5 s later, then at 10, 20 and every
  30 s, with no limit on how long. Stopping would leave the same two wrong choices
  this replaces, so the only ends are an answer or the end of the run.
- A booked row settles it, however late. 400 or 409 settle it too: those come from
  the signal route after it has looked the id up, so nothing is in the book, and
  the state goes back like any refusal. Every other answer (401, 403, 429, any 5xx)
  says nothing about this id and leaves it unknown.
- If the API never comes back, the desk's close stops the run, and the stop closes
  whatever the book holds; the API refuses the OPEN itself once the market has
  closed or the run has stopped. An entry booked late is priced from the quote of
  that moment, and refused if that quote is stale.

One case cannot be put back: an OPEN_GROUP that was booked earlier in the same
tick. Restoring would have the strategy open it a second time, so the state is
kept, the tick's remaining signals are still sent, and the runner says loudly
that the strategy and the book disagree. A CLOSE_GROUP booked earlier in the
tick is no obstacle: emitted again, it closes nothing twice (reduce-only).

A CLOSE_GROUP that was booked and is emitted again is not posted again
(`ClosedGroups`). The pattern is common: a strategy rolls (closes group A,
opens group B), the close is booked, the open is refused, and the state goes
back to "A is open", so every tick asks to close A again. On 30 Sep a Fulcrum
run hit the account's daily loss limit at 12:16; its roll's OPEN was refused
until the close, and the runner posted the same CLOSE_GROUP 9,292 times in
three hours, each one a quotes read, a signal row and a dashboard event. The
API books a signal whole or not at all, so a close that was booked closed its
legs, and the same close again would close nothing.

A strategy put back emits its refused OPEN_GROUP again on the very next tick,
and a refusal rarely clears that fast: a rate limit lasts up to a minute, a
stale quote as long as the feed is stalled. Posted every tick, each one is a
request, a RiskEvent row and a log block. So after a refused OPEN_GROUP the
booker holds every OPEN of the run for a while (OPEN_REFUSAL_COOLDOWN_SECONDS)
without posting it; the strategy is still put back each tick and still asks,
and the first OPEN after the hold is posted. A CLOSE_GROUP is never held.
"""

from __future__ import annotations

import copy
import json
import time
import uuid
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional, Tuple

import requests
from urllib3.exceptions import NewConnectionError

from strategies.base_strategy import StrategySignal
from strategies.signal_utils import signal_to_request

#: How long an OPEN_GROUP is retried. An entry is worth taking only close to the
#: price that triggered it; later, the strategy decides again. That holds only for
#: posts that certainly did not land: one that may have is asked about for as long
#: as it takes (UNCONFIRMED_RETRY_SECONDS).
OPEN_RETRY_SECONDS = 5.0

#: How long a CLOSE_GROUP is retried. Getting flat is worth the wait, and a
#: close repeated after it landed is reduce-only, so it changes nothing.
CLOSE_RETRY_SECONDS = 60.0

#: Each post's own timeout. The API answers in well under a second; this keeps a
#: hung one from holding the tick loop far beyond the retry budget.
POST_TIMEOUT_SECONDS = 10.0

#: Answers that say the API is away right now, so sending the signal again is the
#: thing to do: the API restarting, or a proxy in front of it.
RETRY_STATUSES = frozenset({502, 503, 504})

#: The one answer of those that also says this post was not processed. A 502 or a
#: 504 can come from a proxy whose upstream was already working on it.
NOT_PROCESSED_STATUS = 503

#: Refusals the signal route gives itself, after it has looked the clientSignalId
#: up (PaperTradingService.CreateSignalAsync): the id is not in the book. Any other
#: 4xx comes from in front of that lookup (sign-in, a module check, a rate limiter)
#: and says nothing about an earlier post that may have landed.
ANSWERS_ABOUT_THE_ID = frozenset({400, 409})

#: Pauses between attempts, the last one repeated.
BACKOFF_SECONDS = (0.5, 1.0, 2.0, 4.0, 5.0)

#: How long every OPEN_GROUP of the run is held, unposted, after the API refused
#: one. The runner reads OPEN_REFUSAL_COOLDOWN_SECONDS from the environment.
OPEN_REFUSAL_COOLDOWN_SECONDS = 30.0

#: Pauses between the posts that ask about an unconfirmed OPEN, the last repeated
#: until the API answers. Short at first, because the usual cause is one slow
#: moment, and never long, because the strategy waits behind it.
UNCONFIRMED_RETRY_SECONDS = (5.0, 10.0, 20.0, 30.0)

#: How often the log repeats that an OPEN is still unconfirmed.
UNCONFIRMED_LOG_SECONDS = 60.0


@dataclass
class Booking:
    """What became of one signal."""

    signal_type: str
    group_id: str
    client_signal_id: str
    booked: bool
    result: Optional[Dict[str, Any]] = None
    #: The API answered and said no: sending it again would not help.
    refused: bool = False
    #: Not posted at all: an OPEN inside the hold that follows a refused one, or
    #: while another is unconfirmed, or while its market is closed.
    held: bool = False
    #: On a refused OPEN: how long the run's OPENs are now held, in seconds.
    holds_opens_for: Optional[float] = None
    #: A CLOSE_GROUP already booked, emitted again: counted as booked, not posted.
    repeated: bool = False
    #: An OPEN_GROUP whose answer was lost: it may be in the book, and only the API
    #: can say. Never read this as not booked.
    unconfirmed: bool = False
    #: On an unconfirmed OPEN: the log has not mentioned it for a while.
    announce: bool = False
    #: On an OPEN that was unconfirmed: how long since it was first posted.
    waited_seconds: Optional[float] = None
    status: Optional[int] = None
    detail: str = ""
    attempts: int = 0

    @property
    def opens(self) -> bool:
        return self.signal_type == "OPEN_GROUP"

    def describe(self) -> str:
        """"OPEN_GROUP SMC_20260924T0557_004"."""
        return f"{self.signal_type} {self.group_id}".strip()


@dataclass
class _UnconfirmedOpen:
    """An OPEN_GROUP the API has not answered for, with the payload to ask with."""

    payload: Dict[str, Any]
    since: float
    attempts: int
    next_post_at: float
    logged_at: float
    pauses: int = 0

    @property
    def group_id(self) -> str:
        return str(self.payload.get("groupId") or "")


class SignalBooker:
    """
    Posts one signal at a time to POST /api/Simulator/signals through ``api``
    (anything with ``create_simulation_signal(payload, timeout=...)``), and
    retries it while its answer is lost. ``sleep``, ``clock`` and ``on_wait``
    are injected so the retry timing is tested without waiting; the runner
    passes its housekeeping as ``on_wait`` so the status line and the feed
    watchdog keep ticking through a retry.
    """

    def __init__(
        self,
        api: Any,
        run_id: int,
        *,
        sleep: Callable[[float], None] = time.sleep,
        clock: Callable[[], float] = time.monotonic,
        on_wait: Optional[Callable[[], None]] = None,
        open_retry_seconds: float = OPEN_RETRY_SECONDS,
        close_retry_seconds: float = CLOSE_RETRY_SECONDS,
        post_timeout_seconds: float = POST_TIMEOUT_SECONDS,
        open_cooldown_seconds: float = OPEN_REFUSAL_COOLDOWN_SECONDS,
        new_id: Callable[[], str] = lambda: str(uuid.uuid4()),
    ) -> None:
        self._api = api
        self._run_id = run_id
        self._sleep = sleep
        self._clock = clock
        self._on_wait = on_wait
        self._open_retry = open_retry_seconds
        self._close_retry = close_retry_seconds
        self._post_timeout = post_timeout_seconds
        self._open_cooldown = max(0.0, open_cooldown_seconds)
        self._new_id = new_id
        self._opens_held_until: Optional[float] = None
        self._unconfirmed: Optional[_UnconfirmedOpen] = None

    @property
    def has_unconfirmed(self) -> bool:
        """An OPEN_GROUP's answer is lost and the API has not said what became of it."""
        return self._unconfirmed is not None

    def _hold_opens(self) -> Optional[float]:
        """After a refused OPEN: holds the run's OPENs, and returns for how long."""
        if self._open_cooldown <= 0:
            return None
        self._opens_held_until = self._clock() + self._open_cooldown
        return self._open_cooldown

    def recheck(self) -> Optional[Booking]:
        """
        Posts the unconfirmed OPEN again, same id, when its next post is due. None
        when there is nothing to do yet; otherwise what the API said, which is still
        `unconfirmed` unless it was a booked row or a refusal about this id.
        """
        pending = self._unconfirmed
        if pending is None or self._clock() < pending.next_post_at:
            return None

        pending.attempts += 1

        def outcome(booked: bool, **kwargs: Any) -> Booking:
            return Booking(signal_type="OPEN_GROUP", group_id=pending.group_id,
                           client_signal_id=pending.payload["clientSignalId"], booked=booked,
                           attempts=pending.attempts, waited_seconds=self._clock() - pending.since, **kwargs)

        try:
            result = self._api.create_simulation_signal(pending.payload, timeout=self._post_timeout)
            self._unconfirmed = None
            return outcome(True, result=result)
        except requests.exceptions.HTTPError as ex:
            status = ex.response.status_code if ex.response is not None else None
            if status in ANSWERS_ABOUT_THE_ID:
                self._unconfirmed = None
                return outcome(False, refused=True, status=status, detail=_body(ex), holds_opens_for=self._hold_opens())
            last = f"HTTP {status}"
        except Exception as ex:  # noqa: BLE001 — anything else is still no answer
            last = type(ex).__name__

        now = self._clock()
        pending.next_post_at = now + UNCONFIRMED_RETRY_SECONDS[min(pending.pauses, len(UNCONFIRMED_RETRY_SECONDS) - 1)]
        pending.pauses += 1
        announce = now - pending.logged_at >= UNCONFIRMED_LOG_SECONDS
        if announce:
            pending.logged_at = now
        return outcome(False, unconfirmed=True, announce=announce,
                       detail=f"no answer to {pending.attempts} post(s) in {now - pending.since:.0f}s ({last})")

    def book(self, sig: StrategySignal) -> Booking:
        payload = signal_to_request(self._run_id, sig)
        payload["clientSignalId"] = self._new_id()
        signal_type = str(payload.get("signalType") or "").upper()
        opening = signal_type == "OPEN_GROUP"
        budget = self._open_retry if opening else self._close_retry

        def outcome(booked: bool, **kwargs: Any) -> Booking:
            return Booking(signal_type=signal_type, group_id=str(payload.get("groupId") or ""),
                           client_signal_id=payload["clientSignalId"], booked=booked, attempts=attempts, **kwargs)

        attempts = 0
        if opening and self._unconfirmed is not None:
            # Never reached from the runner, which stops evaluating the strategy
            # until the answer comes. If anything else asks, a second OPEN is the
            # very thing this exists to prevent.
            return outcome(False, held=True, detail=f"OPEN_GROUP {self._unconfirmed.group_id} is not confirmed yet")
        if opening and self._opens_held_until is not None:
            left = self._opens_held_until - self._clock()
            if left > 0:
                return outcome(False, held=True, detail=f"OPENs held for another {left:.0f} s after a refusal")
            self._opens_held_until = None

        started = self._clock()
        confirming = False
        # Once one post of an OPEN may have landed, the outcome stays unknown until
        # the API speaks about this id: a later 503 says nothing about that post.
        may_have_landed = False

        while True:
            attempts += 1
            try:
                result = self._api.create_simulation_signal(payload, timeout=self._post_timeout)
                return outcome(True, result=result)
            except requests.exceptions.HTTPError as ex:
                status = ex.response.status_code if ex.response is not None else None
                if _is_a_refusal(status, opening, may_have_landed):
                    held_for = self._hold_opens() if opening else None
                    return outcome(False, refused=True, status=status, detail=_body(ex), holds_opens_for=held_for)
                last, cause, unsure = f"HTTP {status}", f"HTTP {status}", _server_may_have_processed(status)
            except (requests.exceptions.ConnectionError, requests.exceptions.Timeout) as ex:
                # A post that never connected was never booked. Otherwise it may
                # have landed and only its answer been lost.
                last, cause, unsure = f"{type(ex).__name__}: {ex}", type(ex).__name__, not _never_connected(ex)
            except Exception as ex:  # noqa: BLE001 — anything else is a failure to book, not a crash
                # After a post, that includes an answer that could not be read: a
                # booked OPEN's 200 with a broken body must not read as not booked.
                if not opening or _never_sent(ex):
                    return outcome(False, detail=f"{type(ex).__name__}: {ex}")
                last, cause, unsure = f"{type(ex).__name__}: {ex}", type(ex).__name__, True

            may_have_landed = may_have_landed or unsure
            elapsed = self._clock() - started
            if elapsed >= budget:
                # Out of time. When the last answer was lost rather than
                # refused, one more post with the same id is the only way to
                # learn whether it was booked; beyond that, stop.
                if not unsure or confirming:
                    if opening and may_have_landed:
                        now = self._clock()
                        self._unconfirmed = _UnconfirmedOpen(
                            dict(payload, legs=[dict(leg, price=None) for leg in payload.get("legs") or []]),
                            since=started, attempts=attempts, logged_at=now,
                            next_post_at=now + UNCONFIRMED_RETRY_SECONDS[0], pauses=1)
                        return outcome(False, unconfirmed=True, announce=True,
                                       detail=f"no answer to {attempts} post(s) in {elapsed:.0f}s ({cause})")
                    return outcome(False, detail=f"no answer after {attempts} attempt(s) in {elapsed:.0f}s ({last})")
                confirming = True

            if opening:
                # A retried entry is priced by the API from the quote as it is
                # now, not at the price the runner saw before the retry.
                payload["legs"] = [dict(leg, price=None) for leg in payload.get("legs") or []]

            if self._on_wait is not None:
                try:
                    self._on_wait()
                except Exception:  # noqa: BLE001 — housekeeping must not cost the booking
                    pass
            pause = BACKOFF_SECONDS[min(attempts, len(BACKOFF_SECONDS)) - 1]
            self._sleep(0.0 if confirming else min(pause, max(0.0, budget - elapsed)))


LegsKey = Tuple[Tuple[str, str, int], ...]


class ClosedGroups:
    """
    The CLOSE_GROUPs this run has booked, by group, so the same close emitted
    again is answered from here instead of being posted.

    "The same" means the same group and the same legs (symbol, side, lots) as
    the strategy emitted them, before the runner resolves symbols and prices:
    a close of other legs of the group is posted as usual. A group opened again
    under the same id (booked OPEN_GROUP) forgets its close.
    """

    def __init__(self) -> None:
        self._closed: Dict[str, LegsKey] = {}
        self._repeats: Dict[str, int] = {}

    @staticmethod
    def group_of(sig: StrategySignal) -> str:
        return str((sig.metadata or {}).get("group_id") or "")

    @staticmethod
    def legs_key(sig: StrategySignal) -> LegsKey:
        legs = []
        for leg in sig.legs or []:
            try:
                lots = int(leg.get("quantity") or 0)
            except (TypeError, ValueError):
                lots = 0
            legs.append((str(leg.get("symbol") or "").upper(), str(leg.get("side") or "").upper(), lots))
        return tuple(sorted(legs))

    def repeat_of(self, sig: StrategySignal, key: Optional[LegsKey] = None) -> Optional[Booking]:
        """A booking for a close that was already booked, else None (post it)."""
        if str(sig.signal_type or "").upper() != "CLOSE_GROUP":
            return None
        group = self.group_of(sig)
        if not group or self._closed.get(group) != (key if key is not None else self.legs_key(sig)):
            return None
        self._repeats[group] = self._repeats.get(group, 0) + 1
        return Booking("CLOSE_GROUP", group, "", booked=True, repeated=True,
                       detail="booked before; the same close is not posted again")

    def booked(self, sig: StrategySignal, key: Optional[LegsKey] = None) -> None:
        """Remembers what a booked signal did to its group."""
        group = self.group_of(sig)
        if not group:
            return
        kind = str(sig.signal_type or "").upper()
        if kind == "CLOSE_GROUP":
            self._closed[group] = key if key is not None else self.legs_key(sig)
        elif kind == "OPEN_GROUP":
            self._closed.pop(group, None)
            self._repeats.pop(group, None)

    def repeats(self, group: str) -> int:
        """How many times the strategy emitted this group's booked close again."""
        return self._repeats.get(group, 0)


@dataclass
class TickOutcome:
    """One tick's signals and what became of them."""

    #: The strategy state to carry into the next tick: the one `on_bar` left,
    #: or the copy from before it when a signal was not booked.
    state: Dict[str, Any]
    signals: List[StrategySignal] = field(default_factory=list)
    bookings: List[Booking] = field(default_factory=list)
    #: The signals that were not booked, in order.
    not_booked: List[Booking] = field(default_factory=list)
    restored: bool = False
    #: Signals of the tick left unsent because the state they came from was put back.
    unsent: int = 0
    #: The tick stopped at an OPEN_GROUP whose answer was lost: its last booking.
    #: `state` is what the strategy believes, the signals after it wait in
    #: `remaining`, and the tick is finished by `settle_tick`.
    unconfirmed: bool = False
    #: The OPEN this tick stopped at, once the API has said it is in the book.
    confirmed: Optional[Booking] = None
    #: What the last `settle_tick` heard: None when no post was due.
    last_answer: Optional[Booking] = None
    #: Sent nothing yet: the signals still to go, in order.
    remaining: List[StrategySignal] = field(default_factory=list)
    #: The state from before `on_bar` and the one it left; `state` is one of them.
    before: Optional[Dict[str, Any]] = None
    after: Optional[Dict[str, Any]] = None

    @property
    def booked_opens(self) -> List[Booking]:
        return [b for b in self.bookings if b.booked and b.opens]


def _note_not_booked(tick: TickOutcome) -> None:
    """One signal was not booked: the state goes back, unless an OPEN of the tick is already in the book."""
    if not tick.restored and not any(b.booked and b.opens for b in tick.bookings):
        tick.restored = True


def _send_rest(tick: TickOutcome, post: Callable[[StrategySignal], Optional[Booking]]) -> TickOutcome:
    """
    Sends the tick's remaining signals in order, and stops at an OPEN_GROUP whose
    answer is lost: what comes after it may depend on it.
    """
    while tick.remaining:
        sig = tick.remaining.pop(0)
        if tick.restored and str(sig.signal_type or "").upper() != "CLOSE_GROUP":
            tick.unsent += 1
            continue
        try:
            booking = post(sig)
        except Exception as ex:  # noqa: BLE001 — a signal that failed on the way is not booked
            group = (sig.metadata or {}).get("group_id", "")
            booking = Booking(str(sig.signal_type or ""), str(group), "", booked=False,
                              detail=f"{type(ex).__name__}: {ex}")
        if booking is None:
            continue
        tick.bookings.append(booking)
        if booking.unconfirmed:
            tick.unconfirmed = True
            return tick
        if not booking.booked:
            _note_not_booked(tick)

    tick.unconfirmed = False
    tick.not_booked = [b for b in tick.bookings if not b.booked]
    tick.state = tick.before if tick.restored else tick.after
    return tick


def run_tick(
    state: Dict[str, Any],
    evaluate: Callable[[Dict[str, Any]], List[StrategySignal]],
    post: Callable[[StrategySignal], Optional[Booking]],
) -> TickOutcome:
    """
    One tick: ``evaluate`` is the strategy's ``on_bar`` (it changes ``state``),
    ``post`` sends one signal to the book and returns what became of it, or
    None for a signal that is not for the book (filtered out, or not a group
    signal). Signals are sent in order. Once one is not booked, the state they
    were emitted from is about to be put back and the tick's later OPENs (and
    other signals) are not sent — they come back with it; its later CLOSE_GROUPs
    still are, since getting flat never waits and a close emitted again closes
    nothing twice. When an OPEN_GROUP of this tick was already booked, nothing
    can be put back and the rest are sent as usual. An OPEN_GROUP whose answer
    is lost leaves the tick `unconfirmed`, to be finished by ``settle_tick``.
    """
    before = copy.deepcopy(state)
    signals = evaluate(state) or []
    tick = TickOutcome(state, signals, remaining=list(signals), before=before, after=state)
    return _send_rest(tick, post)


def settle_tick(
    tick: TickOutcome,
    recheck: Callable[[], Optional[Booking]],
    post: Callable[[StrategySignal], Optional[Booking]],
) -> TickOutcome:
    """
    Asks about the OPEN_GROUP an `unconfirmed` tick stopped at (``recheck``:
    SignalBooker.recheck). Still no answer, and the tick stays as it is. Booked, and
    the rest of the tick goes out in order. Refused, and the tick ends as if it
    had been refused the first time: the state goes back and the later OPENs are
    dropped with it. The strategy is not evaluated in between, so the state from
    before its `on_bar` is still the right one to go back to.
    """
    answer = recheck()
    tick.last_answer = answer
    if answer is None:
        return tick
    tick.bookings[-1] = answer
    if answer.unconfirmed:
        return tick
    if answer.booked:
        tick.confirmed = answer
    else:
        _note_not_booked(tick)
    return _send_rest(tick, post)


def report(outcome: TickOutcome, log: Callable[[str], None] = print) -> None:
    """
    The lines a tick with a signal that was not booked leaves in the runner
    log. An OPEN held after a refusal was announced when the hold began, so a
    tick whose only miss is a held OPEN logs nothing: the hold is one line, not
    one per tick. An unconfirmed OPEN is not a miss, and says so about once a minute.
    """
    if outcome.unconfirmed:
        pending = outcome.bookings[-1]
        if pending.announce:
            log(f"SIGNAL UNCONFIRMED: {pending.describe()} — {pending.detail}. It may be in the book; only the API "
                "can say. The strategy keeps its state and is not evaluated, and the same id is posted again until "
                "the API answers.")
        return

    if outcome.confirmed is not None:
        log(f"SIGNAL CONFIRMED: {outcome.confirmed.describe()} is in the book; the API answered after "
            f"{outcome.confirmed.waited_seconds or 0:.0f} s and {outcome.confirmed.attempts} post(s) of the same id.")

    for failed in outcome.not_booked:
        if failed.held:
            continue
        if failed.refused:
            # The API's own reason, on the line the desk's log watcher reads.
            log(f"SIGNAL REFUSED by the API: {failed.detail}")
            why = f"refused by the API (HTTP {failed.status})" if failed.status else "refused by the API"
        else:
            why = f"not booked: {failed.detail}" if failed.detail else "not booked"
        log(f"SIGNAL NOT BOOKED: {failed.describe()} — {why}.")
        if failed.holds_opens_for:
            log(f"OPEN held for {failed.holds_opens_for:.0f} s after refusal: {_reason(failed.detail)}")

    if not outcome.not_booked:
        return
    if outcome.restored and all(b.held for b in outcome.not_booked):
        return

    if outcome.restored:
        log("  The strategy's state is back to where it was before this tick; it will emit the signal "
            "again if it still wants it.")
        if outcome.unsent:
            log(f"  {outcome.unsent} later signal(s) of this tick were not sent; they come back with it.")
    else:
        opened = ", ".join(b.describe() for b in outcome.booked_opens)
        missing = ", ".join(b.describe() for b in outcome.not_booked)
        log(f"  The strategy's state was NOT put back: {opened} was booked earlier in this tick, and putting "
            f"the state back would open it a second time. The strategy believes {missing} happened; "
            "the book does not.")


def _is_a_refusal(status: Optional[int], opening: bool, may_have_landed: bool) -> bool:
    """
    Does this answer settle the signal as not booked? A close keeps its old rule:
    emitted again it closes nothing twice, so anything but "the API is away" will do.
    An OPEN needs a 4xx: a 500 can come after a commit. Once a post may have landed,
    only the route's own refusals count, since they come after it looks the id up.
    """
    if not opening:
        return status not in RETRY_STATUSES
    if status in ANSWERS_ABOUT_THE_ID:
        return True
    return status is not None and 400 <= status < 500 and not may_have_landed


def _server_may_have_processed(status: Optional[int]) -> bool:
    """A status that was not a refusal: only a 503 and the other 4xx say this post was not processed."""
    if status is None:
        return True
    return status >= 500 and status != NOT_PROCESSED_STATUS


def _never_connected(ex: BaseException) -> bool:
    """The connection itself failed (refused, no route, a connect timeout), so nothing was sent."""
    if isinstance(ex, requests.exceptions.ConnectTimeout):
        return True
    cause = ex.args[0] if ex.args else None
    return isinstance(getattr(cause, "reason", None), NewConnectionError)


def _never_sent(ex: BaseException) -> bool:
    """The body could not be written, so nothing went out. An answer's broken JSON is the opposite case."""
    if isinstance(ex, requests.exceptions.JSONDecodeError):
        return False
    return isinstance(ex, (requests.exceptions.InvalidJSONError, TypeError))


def _reason(detail: str) -> str:
    """The API's own sentence from a refusal body ({"error": ...} or {"message": ...}), else the body."""
    try:
        body = json.loads(detail)
    except (TypeError, ValueError):
        return detail
    if isinstance(body, dict):
        return str(body.get("error") or body.get("message") or detail)
    return detail


def _body(ex: requests.exceptions.HTTPError) -> str:
    response = ex.response
    if response is None:
        return str(ex)
    text = getattr(response, "text", "") or ""
    return text.strip()[:500] or f"HTTP {response.status_code}"
