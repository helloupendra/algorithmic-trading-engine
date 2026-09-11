"""
Buffering and posting ticks to the API, for any vendor's feed.

Lifted out of the FYERS streamer when TrueData arrived, unchanged: every number
below was measured against this desk, and a second feed re-deriving them by
guesswork would be a worse feed.
"""

import collections
import threading
import time

#: How long a tick may sit waiting for company. This is the latency the batching
#: itself adds, so it is deliberately far inside the "under a second" the desk
#: asked for.
TICK_FLUSH_INTERVAL = 0.15

#: Flush early once this many are waiting, so a burst does not wait out the timer.
#: Must not exceed the API's MaxTickBatch (500).
#:
#: Kept small on purpose. Measured on 2026-09-07 against an idle API, one tick
#: costs ~25ms to store and a batch of 100 costs ~2.6s — i.e. 26ms per tick, so
#: batching buys nothing per tick; the cost is the per-tick database work, not
#: the HTTP request around it. A big batch therefore just makes one post block
#: for seconds while prices queue behind it.
TICK_BATCH_MAX = 50

#: How many posts may be in flight at once.
#:
#: This is what actually sets throughput: ~25ms per tick means one poster clears
#: about 40 ticks/second, and the FYERS feed produces 39 across its 26 symbols —
#: dead level, so any extra load tips it into a backlog that never drains.
TICK_FLUSH_WORKERS = 6

#: The ceiling on unsent ticks. At 39/s this is over two minutes of feed, so it
#: is only reached if the API is genuinely unavailable rather than merely slow.
TICK_BUFFER_LIMIT = 5000

#: How often to say something when we are shedding.
TICK_DROP_LOG_EVERY = 500


class TickPump:
    """
    A bounded buffer in front of the API, drained in batches by its own threads.

    Posting happens off the socket thread, so a slow API cannot stall the feed.
    What sits in front of it is flushed in BATCHES, not one price at a time: one
    request carrying forty ticks pays for one round-trip, one model binding, one
    DI scope and one auth pass instead of forty.

    The failure this replaced is worth remembering. With a pool posting a tick
    each, the gap between the exchange stamp and the row landing grew from 0.9s
    at 13:35 IST on 2026-09-07 to 60s by 13:40, and the old bounded queue then
    discarded the NEWEST prices — which is how a spike is missed entirely: run
    #30's target needed 127.38 and the highest price ever stored for its leg was
    126.55.

    One instance per feed process. Two vendors in one process would share a
    buffer and could not be told apart in a backlog message, so they each get
    their own.
    """

    def __init__(self, post_batch, label="feed", limit=TICK_BUFFER_LIMIT):
        """
        post_batch: callable taking a list of tick payloads and sending them.
                    Supplied by the caller so this class needs no HTTP session
                    and can be tested without one.
        """
        self._post_batch = post_batch
        self._label = label
        self._limit = limit
        self._buffer: "collections.deque[dict]" = collections.deque()
        self._lock = threading.Lock()
        self._dropped = 0
        self._started = False

    # ------------------------------------------------------------------ state

    def depth(self) -> int:
        """Ticks buffered and not yet posted. Reported in the heartbeat."""
        with self._lock:
            return len(self._buffer)

    def dropped_total(self) -> int:
        return self._dropped

    # ------------------------------------------------------------------ input

    def offer(self, payload: dict) -> None:
        """
        Buffer one tick, and refuse to buffer without limit.

        When the buffer is full the OLDEST tick is discarded, not the newest.
        A short flush window makes that the right way round: what is waiting is
        at most a couple of minutes of prices, and if any of it has to go, the
        stale end is the part worth losing — the desk needs the current price,
        not a complete history of a stall.
        """
        with self._lock:
            if len(self._buffer) >= self._limit:
                self._buffer.popleft()
                self._dropped += 1
                if self._dropped % TICK_DROP_LOG_EVERY == 1:
                    print(f"TICK BACKLOG [{self._label}]: {len(self._buffer)} buffered, "
                          f"shedding the oldest. {self._dropped} dropped so far — "
                          f"the API is not keeping up.", flush=True)
            self._buffer.append(payload)

    # ----------------------------------------------------------------- output

    def drain(self, limit: int) -> list[dict]:
        with self._lock:
            count = min(limit, len(self._buffer))
            return [self._buffer.popleft() for _ in range(count)]

    def flush_loop(self, stop_event: threading.Event | None = None) -> None:
        """
        Post whatever has accumulated, forever.

        Posting synchronously here is what keeps this self-correcting: while a
        slow request is in flight the buffer keeps filling, so the next batch is
        simply larger and the cost per tick falls exactly when pressure rises.
        """
        while stop_event is None or not stop_event.is_set():
            batch = self.drain(TICK_BATCH_MAX)
            if batch:
                self._post_batch(batch)
                # A full batch means more is already waiting; go straight round
                # again instead of sleeping on a queue that is behind.
                if len(batch) >= TICK_BATCH_MAX:
                    continue
            if stop_event is not None:
                stop_event.wait(TICK_FLUSH_INTERVAL)
            else:
                time.sleep(TICK_FLUSH_INTERVAL)

    def start(self, workers: int = TICK_FLUSH_WORKERS) -> None:
        """
        Start the flushers once, whoever asks.

        Several of them, not one: storing a tick costs about 25ms, so a single
        poster tops out near 40 ticks/second — the exact rate a feed produces.
        Posting concurrently is what provides the headroom; the batching only
        reduces the number of requests needed to use it.
        """
        with self._lock:
            if self._started:
                return
            self._started = True
        for index in range(workers):
            threading.Thread(
                target=self.flush_loop, name=f"tick-flusher-{self._label}-{index}", daemon=True
            ).start()
        print(f"TICK FLUSHERS started [{self._label}] — {workers} posters, batches of up to "
              f"{TICK_BATCH_MAX} every {TICK_FLUSH_INTERVAL * 1000:.0f}ms.", flush=True)
