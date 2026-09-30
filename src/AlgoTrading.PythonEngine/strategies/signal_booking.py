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
  and when a signal of the tick is not booked (refused, or still unanswered
  when its retries run out) it hands back that copy: the strategy is where it
  was before the tick, and emits the signal again if it still wants it.

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

from strategies.base_strategy import StrategySignal
from strategies.signal_utils import signal_to_request

#: How long an OPEN_GROUP is retried. An entry is worth taking only close to
#: the price that triggered it; later, the strategy decides again.
OPEN_RETRY_SECONDS = 5.0

#: How long a CLOSE_GROUP is retried. Getting flat is worth the wait, and a
#: close repeated after it landed is reduce-only, so it changes nothing.
CLOSE_RETRY_SECONDS = 60.0

#: Each post's own timeout. The API answers in well under a second; this keeps a
#: hung one from holding the tick loop far beyond the retry budget.
POST_TIMEOUT_SECONDS = 10.0

#: Answers that mean the request was not processed, so sending it again is safe:
#: the API restarting, or a proxy in front of it.
RETRY_STATUSES = frozenset({502, 503, 504})

#: Pauses between attempts, the last one repeated.
BACKOFF_SECONDS = (0.5, 1.0, 2.0, 4.0, 5.0)

#: How long every OPEN_GROUP of the run is held, unposted, after the API refused
#: one. The runner reads OPEN_REFUSAL_COOLDOWN_SECONDS from the environment.
OPEN_REFUSAL_COOLDOWN_SECONDS = 30.0


@dataclass
class Booking:
    """What became of one signal."""

    signal_type: str
    group_id: str
    client_signal_id: str
    booked: bool
    result: Optional[Dict[str, Any]] = None
    #: The API answered and said no (a 4xx, a 500): sending it again would not help.
    refused: bool = False
    #: Not posted at all: an OPEN inside the hold that follows a refused one.
    held: bool = False
    #: On a refused OPEN: how long the run's OPENs are now held, in seconds.
    holds_opens_for: Optional[float] = None
    #: A CLOSE_GROUP already booked, emitted again: counted as booked, not posted.
    repeated: bool = False
    status: Optional[int] = None
    detail: str = ""
    attempts: int = 0

    @property
    def opens(self) -> bool:
        return self.signal_type == "OPEN_GROUP"

    def describe(self) -> str:
        """"OPEN_GROUP SMC_20260924T0557_004"."""
        return f"{self.signal_type} {self.group_id}".strip()


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
        if opening and self._opens_held_until is not None:
            left = self._opens_held_until - self._clock()
            if left > 0:
                return outcome(False, held=True, detail=f"OPENs held for another {left:.0f} s after a refusal")
            self._opens_held_until = None

        started = self._clock()
        confirming = False

        while True:
            attempts += 1
            try:
                result = self._api.create_simulation_signal(payload, timeout=self._post_timeout)
                return outcome(True, result=result)
            except requests.exceptions.HTTPError as ex:
                status = ex.response.status_code if ex.response is not None else None
                if status not in RETRY_STATUSES:
                    held_for = None
                    if opening and self._open_cooldown > 0:
                        held_for = self._open_cooldown
                        self._opens_held_until = self._clock() + held_for
                    return outcome(False, refused=True, status=status, detail=_body(ex), holds_opens_for=held_for)
                last, unsure = f"HTTP {status}", False
            except requests.exceptions.ConnectTimeout as ex:
                # Never connected, so never booked.
                last, unsure = f"{type(ex).__name__}: {ex}", False
            except (requests.exceptions.ConnectionError, requests.exceptions.Timeout) as ex:
                # The post may have landed and only its answer been lost.
                last, unsure = f"{type(ex).__name__}: {ex}", True
            except Exception as ex:  # noqa: BLE001 — anything else is a failure to book, not a crash
                return outcome(False, detail=f"{type(ex).__name__}: {ex}")

            elapsed = self._clock() - started
            if elapsed >= budget:
                # Out of time. When the last answer was lost rather than
                # refused, one more post with the same id is the only way to
                # learn whether it was booked; beyond that, stop.
                if not unsure or confirming:
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

    @property
    def booked_opens(self) -> List[Booking]:
        return [b for b in self.bookings if b.booked and b.opens]


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
    can be put back and the rest are sent as usual.
    """
    before = copy.deepcopy(state)
    signals = evaluate(state) or []
    bookings: List[Booking] = []
    restore = False
    unsent = 0

    for sig in signals:
        if restore and str(sig.signal_type or "").upper() != "CLOSE_GROUP":
            unsent += 1
            continue
        try:
            booking = post(sig)
        except Exception as ex:  # noqa: BLE001 — a signal that failed on the way is not booked
            group = (sig.metadata or {}).get("group_id", "")
            booking = Booking(str(sig.signal_type or ""), str(group), "", booked=False,
                              detail=f"{type(ex).__name__}: {ex}")
        if booking is None:
            continue
        bookings.append(booking)
        if booking.booked:
            continue

        if not restore and not any(b.booked and b.opens for b in bookings):
            restore = True

    not_booked = [b for b in bookings if not b.booked]
    if restore:
        return TickOutcome(before, signals, bookings, not_booked, restored=True, unsent=unsent)
    return TickOutcome(state, signals, bookings, not_booked)


def report(outcome: TickOutcome, log: Callable[[str], None] = print) -> None:
    """
    The lines a tick with a signal that was not booked leaves in the runner
    log. An OPEN held after a refusal was announced when the hold began, so a
    tick whose only miss is a held OPEN logs nothing: the hold is one line, not
    one per tick.
    """
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
