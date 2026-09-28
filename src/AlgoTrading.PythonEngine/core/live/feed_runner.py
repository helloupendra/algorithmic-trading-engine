"""
Everything a live feed does that is not the vendor's socket — written once.

Every rule in here was paid for by an incident on the FYERS feed and is kept
word for word; the difference is that a vendor now gets all of it by being a
`VendorFeed`, instead of by someone remembering to copy it. What it owns:

  * one process per vendor account (a Redis lock);
  * every tick to the API (batched) AND to the Redis stream the strategies read;
  * greeks for option ticks, from whichever vendor sent them;
  * what a snapshot means, and what a replay means, for storage;
  * the watchlist: the Redis signal, a periodic reconcile, in-place add/remove,
    and proof that an incremental subscribe actually took — together with any
    symbols the feed carries of its own (VendorFeed.extra_symbols);
  * the heartbeat, with an honest status;
  * the watchdogs: socket down, connected but silent, credential refused,
    login refused — each cause also told to the System channel, once per ten
    minutes (core/live/watchdog_alerts.py) — and, for a feed that can say how
    its socket is doing, no frame at all and bytes piling up unread.
"""

import collections
import os
import sys
import threading
import time
import traceback
from datetime import datetime, timezone

from core.api_client import build_session
from core.config import (API_BASE_URL, ENABLE_MOCK_TICKS, HEARTBEAT_SECONDS,
                         VERIFY_SSL, WATCHLIST_REFRESH_SECONDS)
from core.heartbeat import run_forever
from core.live.greeks_enricher import GreeksEnricher
from core.live.tick_pump import TickPump
from core.live.reconnect_policy import describe, reconnect_delay
from core.live.vendor_feed import FeedEvent
from core.live import watchdog_alerts
from core.live.watchdog_alerts import WatchdogAlerts
from messaging.redis_publisher import normalize_tick


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _env_number(name: str, default, cast=float):
    """A number from the environment (.env is loaded by core.config), or the default when unset or not a number."""
    raw = os.getenv(name, "").strip()
    if not raw:
        return default
    try:
        return cast(raw)
    except ValueError:
        print(f"[feed] {name}={raw!r} is not a number — using {default}.", flush=True)
        return default


def _percentile(values, fraction):
    """The value `fraction` of the way up the sorted values; None for none."""
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(len(ordered) * fraction))]


def feed_silent_for(now: float, connected: bool, connected_since, last_message) -> float | None:
    """
    Seconds since the last message — or since the connect when no message has
    arrived at all. None when the socket is down (that is the disconnect
    watchdog's business, not a stall).
    """
    if not connected:
        return None
    base = last_message if last_message is not None else connected_since
    if base is None:
        return None
    return now - base


def _shown(names, limit=8) -> str:
    """A long symbol list, cut to what a log line can carry."""
    return ", ".join(names[:limit]) + (f" (+{len(names) - limit} more)" if len(names) > limit else "")


class FeedRunner:
    """Drives one VendorFeed against this platform."""

    #: Restart the connection when a disconnect persists this long. A vendor's
    #: own reconnect keeps retrying with the credential it was built with —
    #: after a daily token expiry that loops forever — so the outer restart is
    #: what picks up a fresh one.
    DISCONNECT_RESTART_SECONDS = 20

    #: Connected, symbols subscribed, session open, and nothing for this long:
    #: the line is dead however healthy it looks.
    STALL_AFTER_SECONDS = 120

    #: Symbols added to the watchlist are subscribed on the LIVE socket rather
    #: than by rebuilding it — a rolling straddle rolls its strike several times
    #: a day, and each rebuild blacked out every other symbol. If NONE of a batch
    #: of new symbols ticks within this window of open market, the incremental
    #: subscribe did not take and the connection is rebuilt as before.
    SUBSCRIBE_PROOF_SECONDS = 90

    #: How long to wait before trying again after a refusal reconnecting cannot
    #: fix (another session holds the account). Retrying every second would only
    #: repeat the refusal and fill the log.
    REFUSED_BACKOFF_SECONDS = 60

    #: How long a batch the API did not store keeps showing as the heartbeat's
    #: error once the feed has nothing else to say. Long enough to be seen on
    #: the Data overview; short enough that a blip at 09:20 is not an error at 15:00.
    STORE_FAILURE_NOTE_SECONDS = 600

    #: After the strategy stream cannot be written, how long it is left alone.
    #: Every pass would otherwise wait out its own timeout on a Redis that is
    #: not answering; the ticks still reach the API meanwhile, and are counted
    #: as not published.
    STREAM_PAUSE_SECONDS = 5

    #: Consecutive once-a-second samples of the socket's unread bytes at or
    #: above FEED_BACKLOG_ALERT_KB before FALLING BEHIND is said, and below it
    #: before the note is cleared.
    BACKLOG_ALERT_SAMPLES = 10
    BACKLOG_CLEAR_SAMPLES = 10

    #: How often the read-path stats line is printed, for a feed that has one.
    STATS_EVERY_SECONDS = 60

    def __init__(self, feed, http=None, publisher=None, fixed_symbols=None,
                 api_base_url: str | None = None, verify_ssl: bool | None = None,
                 watchlist_refresh_seconds: float = WATCHLIST_REFRESH_SECONDS,
                 heartbeat_seconds: float = HEARTBEAT_SECONDS,
                 market_open=None, frame_silence_seconds: float | None = None,
                 backlog_alert_kb: float | None = None, stream_batch: bool | None = None):
        self._feed = feed
        self._http = http or build_session()
        self._publisher = publisher
        self._api = (api_base_url or API_BASE_URL).rstrip("/")
        self._verify = VERIFY_SSL if verify_ssl is None else verify_ssl
        self._watchlist_refresh = watchlist_refresh_seconds
        self._heartbeat_seconds = heartbeat_seconds
        self._market_open_override = market_open

        self.source_name = feed.source_name or f"python-{feed.key}-feed"
        self.lock_key = feed.lock_key or f"feed:{feed.key}:lock"

        # An explicit list replaces the recording list — for a vendor whose
        # symbol limit is below what the list carries, where the contracts a
        # strategy trades must be the ones subscribed.
        self._fixed_symbols = sorted(set(fixed_symbols)) if fixed_symbols else None

        self.pump = TickPump(self._post_batch, label=feed.key)
        self.enricher = GreeksEnricher()

        # What the vendor actually took, and what it declined (no name, over the
        # limit). Declined symbols are not offered again every few seconds; a
        # reconnect forgets them, since a raised limit or new mapping may change
        # the answer.
        self.subscribed: set[str] = set()
        self.declined: set[str] = set()

        # {symbol: (subscribed_at_walltime, monotonic deadline)}. The wall time
        # keeps the proof honest: last_real_tick keeps a symbol's last tick
        # forever, so a symbol removed and later re-added would otherwise be
        # confirmed by data that arrived before this subscribe.
        self.pending_subscriptions: dict[str, tuple[float, float]] = {}
        self.last_real_tick: dict[str, float] = {}

        # Connection health, all time.monotonic().
        self.socket_connected = False
        self.disconnected_since = None
        self.connected_at = None
        self.last_message = None

        # The credential the vendor refused, and when. The connection manager
        # restarts on it, but only with a DIFFERENT credential — reconnecting with
        # the same dead token every second would hammer the vendor and still be
        # deaf.
        self.current_credential = None
        self.rejected_credential = None
        self.credential_rejected_at = None

        # A refusal reconnecting cannot fix.
        self.refused_detail: str | None = None

        # The feed's own additions to the watchlist (VendorFeed.extra_symbols):
        # the last list it gave, kept so one failed ask does not unsubscribe
        # everything on it, and how many of it the symbol limit left out, so
        # that is said when it changes rather than every few seconds.
        self._last_extra: list[str] = []
        self._extra_left_out = 0

        self.restart_required = False
        self.last_watchlist_refresh_utc = None
        #: Ticks accepted since the process started; the reconnect backoff
        #: uses it to tell a working connection from a looping one.
        self.ticks_accepted = 0
        self.last_error = ""
        self._publish_errors = 0

        # What never reached the tables, counted since the process started and
        # sent in every heartbeat. A failed batch used to be one printed line:
        # the strategies still heard those ticks on the stream, but the quote
        # table the runners price legs from did not, and nothing counted how
        # often. The flushers are six threads, hence the lock.
        self._counts_lock = threading.Lock()
        self.ticks_not_stored = 0
        self.ticks_rejected = 0
        #: (time.monotonic(), what went wrong) of the last batch not stored.
        self.last_store_failure: tuple[float, str] | None = None
        self._market_open_cache = {"value": None, "checked_at": 0.0}
        self._stop = threading.Event()

        # Each watchdog restart's cause, to the desk's System channel: at most
        # once per cause per ten minutes (core/live/watchdog_alerts.py).
        self.watchdog_alerts = WatchdogAlerts(feed.key, publisher)

        # --- the read path's switches (all read once, at start) ------------
        #: No frame at all — not even the vendor's ping — for this long in open
        #: session, with symbols subscribed: the line is dead. 0 turns it off.
        self._frame_silence_seconds = float(
            frame_silence_seconds if frame_silence_seconds is not None
            else _env_number("FEED_FRAME_SILENCE_SECONDS", 45.0))
        #: Unread bytes on the socket at which this process is falling behind.
        self._backlog_alert_bytes = int(1024 * float(
            backlog_alert_kb if backlog_alert_kb is not None else _env_number("FEED_BACKLOG_ALERT_KB", 128.0)))
        #: One pipelined write per batch of ticks (publish_ticks) rather than
        #: one XADD per tick. FEED_STREAM_BATCH=0 goes back to one at a time.
        self._stream_batch = bool(stream_batch if stream_batch is not None
                                  else _env_number("FEED_STREAM_BATCH", 1, int))

        # The strategy stream: paused until this monotonic time after a
        # failure, the last failure's words, and how long each write took.
        self._stream_paused_until = 0.0
        self._last_publish_error = ""
        self._stream_batches = 0
        self._stream_batch_ms: collections.deque = collections.deque(maxlen=6000)

        # The socket's unread bytes, sampled once a second by the watch loop:
        # the samples since the last stats line, the current run above and
        # below the alert line, and the note the heartbeat carries meanwhile.
        self._backlog_samples: list[int] = []
        self._backlog_high = 0
        self._backlog_low = 0
        self._backlog_episode_start = None
        self._behind_note = ""

        # The last stats line's baseline: (monotonic time, feed.stats(), stream batches).
        self._stats_base = None

    # ================================================================ ticks

    def on_ticks(self, ticks) -> None:
        now = time.monotonic()
        self.last_message = now
        self.ticks_accepted += len(ticks or [])
        messages = []
        for tick in ticks or []:
            try:
                message = self._accept(tick)
            except Exception as ex:
                self.last_error = str(ex)
                self.ticks_rejected += 1
                # Neither published nor stored: the strategies never hear it.
                print(f"[{self._feed.key}] could not accept a tick ({self.ticks_rejected} so far): {ex}",
                      flush=True)
                continue
            if message is not None:
                messages.append(message)
        # The whole batch to the stream at once, after every tick is on its
        # way to the API: storage never waits on Redis.
        self._publish_many(messages)
        if ticks:
            self.last_error = ""

    def _accept(self, tick: dict) -> dict | None:
        """
        One tick: the platform's rules applied, handed to the pump for the API,
        and returned as the strategy stream's message — None when it is not to
        be published (no stream, a replay's snapshot, or a message that could
        not be built, which counts as not published).
        """
        snapshot = bool(tick.pop("snapshot", False))
        if snapshot and self._feed.is_replay:
            # A replay's snapshot is stamped with the moment it was sent, not
            # with the replay's clock, and it is not part of the session being
            # replayed. Storing it froze all 49 contracts of the first TrueData
            # recap at a 17:31 price.
            return None

        tick.setdefault("sourceKey", self._feed.key)
        tick.setdefault("dataType", "symbolUpdate")
        if self._feed.is_replay:
            tick["isReplay"] = True

        self.enricher.enrich(tick)

        symbol = tick.get("symbol")
        if symbol and not snapshot:
            self.last_real_tick[symbol] = time.time()

        message = self._stream_message(tick)
        self.pump.offer(tick)
        return message

    def _stream_message(self, tick: dict) -> dict | None:
        """The tick as the strategy stream carries it; None without a stream or when it cannot be built."""
        # The strategies read the Redis stream, not the tables. The first
        # TrueData feed only posted to the API, and every running strategy sat
        # "waiting for ticks" while the tables filled.
        if self._publisher is None:
            return None
        try:
            # rawPayload is set here, so normalize_tick is not asked to
            # serialise the whole tick into it only for that to be replaced.
            message = normalize_tick(tick, include_raw=False)
            message["sourceKey"] = tick.get("sourceKey")
            message["isReplay"] = bool(tick.get("isReplay"))
            # The API still gets the full payload (see _accept); this only keeps
            # a bulky one off the stream every strategy has to read.
            message["rawPayload"] = tick.get("rawPayload", "") if self._feed.publish_raw_payload else ""
            return message
        except Exception as ex:
            self._count_publish_errors(1, ex)
            return None

    def _publish(self, tick: dict) -> None:
        """One tick to the strategy stream."""
        message = self._stream_message(tick)
        if message is not None:
            self._publish_many([message])

    def _publish_many(self, messages: list[dict]) -> None:
        """
        Messages to the strategy stream: one pipelined round trip for a batch
        (FEED_STREAM_BATCH, publish_ticks), one XADD each otherwise. A failure
        pauses the stream for STREAM_PAUSE_SECONDS; nothing is retried, since
        a batch that failed part way may already be on the stream.
        """
        publisher = self._publisher
        if publisher is None or not messages:
            return
        if time.monotonic() < self._stream_paused_until:
            self._count_publish_errors(len(messages))
            return
        started = time.perf_counter()
        sent = 0
        try:
            publish_ticks = getattr(publisher, "publish_ticks", None) if self._stream_batch else None
            if publish_ticks is not None and len(messages) > 1:
                publish_ticks(messages)
                sent = len(messages)
            else:
                for message in messages:
                    publisher.publish_tick(message)
                    sent += 1
        except Exception as ex:
            self._stream_paused_until = time.monotonic() + self.STREAM_PAUSE_SECONDS
            self._count_publish_errors(len(messages) - sent, ex)
        finally:
            self._stream_batches += 1
            self._stream_batch_ms.append((time.perf_counter() - started) * 1000.0)

    def _count_publish_errors(self, count: int, ex: Exception | None = None) -> None:
        """
        Ticks the strategies did not get. Said on the first, the 100th, the
        1000th and every 10,000th — as the total crosses each, since a batch
        or a pause adds many at once.
        """
        if count <= 0:
            return
        if ex is not None:
            self._last_publish_error = str(ex)
        before, after = self._publish_errors, self._publish_errors + count
        self._publish_errors = after
        if any(before < mark <= after for mark in (1, 100, 1000)) or after // 10000 > before // 10000:
            paused = (f"; the stream is skipped for {self.STREAM_PAUSE_SECONDS}s after each failure"
                      if after > 1 else "")
            print(f"[{self._feed.key}] could not publish a tick to the strategy stream "
                  f"({after} so far): {self._last_publish_error}{paused}", flush=True)

    def _post_batch(self, batch: list[dict]) -> None:
        url = f"{self._api}/api/LiveData/ticks/upsert-batch"
        try:
            response = self._http.post(url, json=batch, verify=self._verify, timeout=30)
            if response.status_code >= 400:
                total = self._not_stored(batch, f"HTTP {response.status_code}")
                print(f"[{self._feed.key}] TICK BATCH FAILED: {response.status_code} "
                      f"{response.text[:300]} ({total} tick(s) not stored so far)", flush=True)
        except Exception as ex:
            total = self._not_stored(batch, str(ex)[:200])
            print(f"[{self._feed.key}] TICK BATCH HTTP ERROR: {ex} ({total} tick(s) not stored so far)",
                  flush=True)

    def _not_stored(self, batch: list[dict], detail: str) -> int:
        with self._counts_lock:
            self.ticks_not_stored += len(batch)
            self.last_store_failure = (time.monotonic(), detail)
            return self.ticks_not_stored

    def health_note(self, now: float | None = None) -> str:
        """
        What the heartbeat's error says when nothing is wrong with the
        connection itself: ticks the API did not store lately, option greeks
        this process cannot compute, and a socket this process is not reading
        fast enough. Empty when there is none of those.
        """
        now = time.monotonic() if now is None else now
        notes = []
        failure = self.last_store_failure
        if failure is not None and now - failure[0] < self.STORE_FAILURE_NOTE_SECONDS:
            notes.append(f"{self.ticks_not_stored} tick(s) not stored since the feed started; the last batch "
                         f"failed {int(now - failure[0])}s ago ({failure[1]})")
        greeks = self.enricher.unavailable_reason()
        if greeks:
            notes.append(f"option greeks unavailable: {greeks}")
        if self._behind_note:
            notes.append(self._behind_note)
        return "; ".join(notes)

    # ================================================================ events

    def on_event(self, event: str, detail: str = "") -> None:
        now = time.monotonic()
        if event == FeedEvent.CONNECTED:
            self.socket_connected = True
            self.disconnected_since = None
            self.connected_at = now
            self.last_error = ""
        elif event == FeedEvent.DISCONNECTED:
            self.socket_connected = False
            if self.disconnected_since is None:
                self.disconnected_since = now
            self.last_error = detail or "disconnected"
        elif event == FeedEvent.CREDENTIALS_REJECTED:
            self.last_error = detail
            if self.rejected_credential is None:
                self.rejected_credential = self.current_credential
                self.credential_rejected_at = now
                print(f"[{self._feed.key}] CREDENTIAL REJECTED — this socket will never carry data. "
                      f"Waiting for a different one, then reconnecting.", flush=True)
        elif event == FeedEvent.REFUSED:
            self.refused_detail = detail or "refused"
            self.last_error = self.refused_detail
        elif event == FeedEvent.ERROR:
            self.last_error = detail

        print(f"[{self._feed.key}] {event}: {detail}", flush=True)

        if event == self._feed.ready_event:
            # Subscribe the whole list on a fresh connection, as soon as the
            # vendor will take it.
            self.sync_watchlist(force_subscribe=True)

    # ============================================================ watchlist

    def read_watchlist(self) -> list[str]:
        if self._fixed_symbols is not None:
            return list(self._fixed_symbols)
        response = self._http.get(f"{self._api}/api/LiveData/watchlist", verify=self._verify, timeout=30)
        response.raise_for_status()
        return sorted({
            row["symbol"] for row in response.json()
            if row.get("isActive")
            and (row.get("dataType") or "").lower() == "symbolupdate"
            and row.get("symbol")
        })

    def desired_symbols(self) -> tuple[list[str], list[str]]:
        """
        (watchlist, extra): what should be on the wire, in priority order.

        The watchlist, then whatever the feed carries of its own that the
        watchlist does not already name — a fixed symbol list is exactly that
        list and nothing more. Under a symbol limit the watchlist wins: the
        extras only fill the room it leaves. The watchlist itself is never cut
        here, so a feed without extras is offered exactly what it always was
        and declines past its own limit as before.
        """
        watchlist = list(dict.fromkeys(self.read_watchlist()))
        if self._fixed_symbols is not None:
            return watchlist, []

        try:
            offered = list(self._feed.extra_symbols() or [])
            self._last_extra = offered
        except Exception as ex:
            # An empty answer would unsubscribe every extra; the last list is
            # the better guess until the feed can say again.
            offered = self._last_extra
            print(f"[{self._feed.key}] could not get the feed's extra symbols ({ex}) — keeping the last "
                  f"{len(offered)}.", flush=True)

        seen = {symbol.strip().upper() for symbol in watchlist}
        extra = []
        for symbol in offered:
            name = str(symbol or "").strip()
            if name and name.upper() not in seen:
                seen.add(name.upper())
                extra.append(name)

        limit = self._feed.max_symbols
        left_out = 0
        if limit and len(extra) > max(limit - len(watchlist), 0):
            room = max(limit - len(watchlist), 0)
            left_out = len(extra) - room
            extra = extra[:room]
        if left_out != self._extra_left_out:
            self._extra_left_out = left_out
            if left_out:
                print(f"[{self._feed.key}] {left_out} of the feed's extra symbol(s) do not fit under the "
                      f"{limit}-symbol limit after {len(watchlist)} watchlist symbol(s); the watchlist comes "
                      f"first.", flush=True)
        return watchlist, extra

    def sync_watchlist(self, force_subscribe: bool = False) -> None:
        """
        Fetch the watchlist (and the feed's extra symbols) and adjust
        subscriptions on the LIVE socket.

        Only a fresh connection subscribes the whole list. After that the
        difference is applied in place, so an intraday ATM roll costs nothing to
        the symbols it did not touch. `subscribed` stays the truth about what is
        on the wire, since the heartbeat and the stall detector both read it.
        """
        try:
            before = set(self.subscribed)
            watchlist, extra = self.desired_symbols()
            ordered = watchlist + extra
            desired = set(ordered)

            if force_subscribe or not self.subscribed:
                self.declined.clear()
                taken = set(self._feed.subscribe(ordered)) if ordered else set()
                self.subscribed = taken
                self.declined = desired - taken
                # A fresh socket is proved by the feed as a whole, not per symbol.
                self.pending_subscriptions.clear()
            else:
                added = [s for s in ordered if s not in self.subscribed and s not in self.declined]
                removed = self.subscribed - desired
                # A symbol that left the list is no longer declined either.
                self.declined &= desired

                # Removals go first: under a vendor's limit they are the room an
                # addition needs, and a symbol added into a full connection is
                # declined and not offered again.
                if removed:
                    if self._feed.unsubscribe(sorted(removed)):
                        self.subscribed -= removed
                    else:
                        print(f"[{self._feed.key}] still subscribed to {sorted(removed)} — harmless, "
                              f"and cheaper than rebuilding the connection.", flush=True)
                    for symbol in removed:
                        self.pending_subscriptions.pop(symbol, None)

                if added:
                    on_watchlist = set(watchlist)
                    from_watchlist = sorted(s for s in added if s in on_watchlist)
                    from_feed = [s for s in added if s not in on_watchlist]
                    if from_watchlist:
                        print(f"[{self._feed.key}] WATCHLIST CHANGED — subscribing on the live socket: "
                              f"{from_watchlist}", flush=True)
                    if from_feed:
                        print(f"[{self._feed.key}] EXTRA SYMBOLS CHANGED — subscribing {len(from_feed)} on the "
                              f"live socket: {_shown(from_feed)}", flush=True)
                    taken = set(self._feed.subscribe(added))
                    self.subscribed |= taken
                    self.declined |= set(added) - taken
                    marker = (time.time(), time.monotonic() + self.SUBSCRIBE_PROOF_SECONDS)
                    for symbol in taken:
                        self.pending_subscriptions[symbol] = marker

            self.last_watchlist_refresh_utc = datetime.now(timezone.utc)
            if force_subscribe or self.subscribed != before:
                beyond = len(self.subscribed & set(extra))
                print(f"[{self._feed.key}] SUBSCRIBED: {len(self.subscribed)} symbol(s)"
                      + (f", {beyond} beyond the watchlist" if beyond else "")
                      + (f", {len(self.declined)} declined" if self.declined else ""), flush=True)
            self.last_error = ""
        except Exception as ex:
            self.last_error = str(ex)
            print(f"[{self._feed.key}] ERROR SYNCING WATCHLIST: {ex}", flush=True)
            traceback.print_exc()

    def check_pending_subscriptions(self) -> None:
        """
        Rebuild the connection when a batch of newly added symbols is still
        silent past its deadline. Any one of them ticking proves incremental
        subscribe works; only a wholly silent batch, on a connected socket in
        open session, is evidence it did not take.
        """
        if not self.pending_subscriptions or not self.socket_connected:
            return

        now = time.monotonic()
        pending = list(self.pending_subscriptions.items())

        for symbol, (subscribed_at, _) in pending:
            if self.last_real_tick.get(symbol, 0) > subscribed_at:
                print(f"[{self._feed.key}] SUBSCRIBE CONFIRMED: {symbol} is sending data.", flush=True)
                self.pending_subscriptions.clear()
                return

        if any(now < deadline for _, (_, deadline) in pending):
            return

        # Outside the session silence proves nothing; wait another window.
        if self.is_market_open() is not True:
            extended = now + self.SUBSCRIBE_PROOF_SECONDS
            for symbol, (subscribed_at, _) in list(self.pending_subscriptions.items()):
                self.pending_subscriptions[symbol] = (subscribed_at, extended)
            return

        print(f"[{self._feed.key}] SUBSCRIBE UNCONFIRMED: no data for any of "
              f"{sorted(self.pending_subscriptions)} in {self.SUBSCRIBE_PROOF_SECONDS}s of open "
              f"session — rebuilding the connection.", flush=True)
        self.pending_subscriptions.clear()
        self.restart_required = True

    def _watchlist_loop(self) -> None:
        """
        Keeps subscriptions in step with the watchlist: instantly on the Redis
        signal, and on a timer regardless. Either alone has failed — a pubsub
        listen() once ended without raising when its connection reset, and the
        feed went deaf to watchlist changes while everything else stayed green.
        """
        backoff = 1.0
        while not self._stop.is_set():
            pubsub = None
            try:
                if self._publisher is not None and self._fixed_symbols is None:
                    pubsub = self._publisher.client.pubsub()
                    pubsub.subscribe("watchlist_updates")
                backoff = 1.0
                last_reconcile = 0.0
                while not self._stop.is_set():
                    message = None
                    if pubsub is not None:
                        message = pubsub.get_message(ignore_subscribe_messages=True, timeout=1.0)
                    else:
                        self._stop.wait(1.0)
                    if not self.socket_connected:
                        continue
                    if message is not None and message.get("type") == "message":
                        self.sync_watchlist()
                        last_reconcile = time.monotonic()
                    elif time.monotonic() - last_reconcile >= self._watchlist_refresh:
                        self.sync_watchlist()
                        last_reconcile = time.monotonic()
            except Exception as ex:
                self.last_error = str(ex)
                print(f"[{self._feed.key}] ERROR IN WATCHLIST LOOP: {ex}", flush=True)
            finally:
                try:
                    if pubsub is not None:
                        pubsub.close()
                except Exception:
                    pass
            if self._stop.is_set():
                break
            time.sleep(backoff)
            backoff = min(backoff * 2, 30.0)

    # ============================================================ status

    def is_market_open(self) -> bool | None:
        """
        Whether the session is open, cached 60s. A replay is a session in
        progress whatever the wall clock says. None when the answer is
        unavailable — callers then avoid declaring a stall on guesswork.
        """
        if self._market_open_override is not None:
            return self._market_open_override()
        if self._feed.is_replay:
            return True
        now = time.monotonic()
        cache = self._market_open_cache
        if cache["value"] is not None and now - cache["checked_at"] < 60:
            return cache["value"]
        try:
            response = self._http.get(f"{self._api}/api/MarketSession/check?exchange=NSE&segment=CM",
                                      verify=self._verify, timeout=10)
            response.raise_for_status()
            cache["value"] = bool(response.json().get("isMarketOpen"))
            cache["checked_at"] = now
        except Exception as ex:
            print(f"[{self._feed.key}] MARKET SESSION CHECK FAILED: {ex}", flush=True)
            cache["value"] = None
        return cache["value"]

    def compute_status(self) -> str:
        """
        Disconnected — the socket is down;
        Refused      — the vendor refused the login and reconnecting will not help;
        Stalled      — connected but deaf: a refused credential, or open session,
                       symbols subscribed and nothing for STALL_AFTER_SECONDS;
        Running      — everything else, including idle outside the session.
        The API marks the source unhealthy for anything except Running.
        """
        if self.refused_detail:
            return "Refused"
        if not self.socket_connected:
            return "Disconnected"
        if self.rejected_credential is not None:
            return "Stalled"
        if self.subscribed:
            silent = feed_silent_for(time.monotonic(), self.socket_connected, self.connected_at, self.last_message)
            if silent is not None and silent > self.STALL_AFTER_SECONDS and self.is_market_open() is True:
                return "Stalled"
        return "Running"

    def send_heartbeat(self) -> None:
        payload = {
            "sourceName": self.source_name,
            "status": self.compute_status(),
            "lastHeartbeatUtc": utc_now_iso(),
            "lastWatchlistRefreshUtc": (
                self.last_watchlist_refresh_utc.isoformat().replace("+00:00", "Z")
                if self.last_watchlist_refresh_utc else None
            ),
            "currentSubscribedSymbols": sorted(self.subscribed),
            # Backlog, made visible. The 31-minute drift of 2026-09-04 accumulated
            # entirely inside the feed with nothing reporting it.
            "queueDepth": self.pump.depth(),
            "ticksDropped": self.pump.dropped_total(),
            "ticksNotStored": self.ticks_not_stored,
            "ticksRejected": self.ticks_rejected,
            "greeksUnavailable": self.enricher.unavailable_reason() or "",
            "lastError": self.last_error or self.health_note(),
            # Lets the API find (and stop) this process again after it restarts —
            # under this feed's own key, so one vendor's pid never lands in
            # another's slot.
            "processId": os.getpid(),
            "feedKey": self._feed.key,
        }
        response = self._http.post(f"{self._api}/api/LiveData/heartbeat", json=payload,
                                   verify=self._verify, timeout=30)
        if response.status_code >= 400:
            print(f"[{self._feed.key}] HEARTBEAT FAILED: {response.status_code} {response.text[:200]}", flush=True)

    def _heartbeat_step(self) -> None:
        if self._publisher is not None:
            self._publisher.client.expire(self.lock_key, 15)
        self.send_heartbeat()

    def _heartbeat_loop(self) -> None:
        # Must never die: an unreachable API is retried on the next beat, and a
        # closed stdout cannot kill it — see core.heartbeat.
        run_forever(self._heartbeat_step, self._heartbeat_seconds, log=print,
                    on_error=lambda ex: setattr(self, "last_error", str(ex)),
                    label=f"{self._feed.key} heartbeat loop")

    # ============================================================ lifecycle

    def acquire_lock(self) -> bool:
        if self._publisher is None:
            return True
        return bool(self._publisher.client.set(self.lock_key, "active", nx=True, ex=15))

    def stop(self) -> None:
        self._stop.set()
        self.restart_required = True
        try:
            self._feed.close()
        except Exception:
            pass

    def run(self) -> None:
        """Hold the lock, start the helpers, and keep a healthy connection. Blocks."""
        if not self.acquire_lock():
            print(f"[{self._feed.key}] another {self._feed.key} feed already holds {self.lock_key}. "
                  f"Two connections on one account get one of them dropped or refused — "
                  f"this instance exits.", flush=True)
            sys.exit(1)

        self.pump.start()
        threading.Thread(target=self._watchlist_loop, name=f"{self._feed.key}-watchlist", daemon=True).start()
        threading.Thread(target=self._heartbeat_loop, name=f"{self._feed.key}-heartbeat", daemon=True).start()

        if ENABLE_MOCK_TICKS:
            from core.live.mock_ticks import MockTickSource
            print(f"[{self._feed.key}] WARNING: ENABLE_MOCK_TICKS is on — fabricated prices will be "
                  f"written for symbols the feed does not carry.", flush=True)
            source = MockTickSource(subscribed=lambda: set(self.subscribed), offer=self.pump.offer)
            threading.Thread(target=source.run_forever, name="mock-ticks", daemon=True).start()

        # A cycle that carried no ticks is a failure, and failures back off:
        # reconnecting every 20s against a dead session is what got the Dhan
        # account rate-limited on 2026-09-16 (core/live/reconnect_policy.py).
        tickless_cycles = 0
        while not self._stop.is_set():
            ticks_before = self.ticks_accepted
            try:
                self._connect_once_and_watch()
            except Exception as ex:
                self.last_error = str(ex)
                print(f"[{self._feed.key}] ERROR IN CONNECTION MANAGER: {ex}", flush=True)
                traceback.print_exc()
                self._stop.wait(5)
            tickless_cycles = 0 if self.ticks_accepted > ticks_before else tickless_cycles + 1
            delay = reconnect_delay(tickless_cycles, self.last_error)
            if delay > 0 and not self._stop.is_set():
                print(f"[{self._feed.key}] {describe(tickless_cycles, delay, self.last_error)}", flush=True)
                self._stop.wait(delay)

    def _watchdog(self, cause: str, text: str, cause_detail: str = "") -> None:
        """
        The WATCHDOG line in the feed's log, word for word as before (Sentinel's
        logs agent reads it), and the same cause to the System channel.
        """
        print(f"[{self._feed.key}] WATCHDOG: {text}", flush=True)
        detail = text if not cause_detail else f"{text} ({cause_detail})"
        self.watchdog_alerts.report(cause, detail)

    # ======================================================= the read path

    def check_frame_silence(self) -> None:
        """
        Restart a socket that has carried nothing at all — no data frame, not
        even the vendor's own ping — for FEED_FRAME_SILENCE_SECONDS in open
        session. The 120 s rule is about ticks; a line can be alive with
        nothing to price. This one is about the line: a vendor that pings
        every 10 s and has not in 45 s is gone. Only for a feed that knows its
        own socket (transport_idle_seconds), and only once a first frame came:
        before that the 120 s rule is the one that applies. Like that rule it
        follows NSE's hours, so it sleeps through the MCX evening.
        """
        limit = self._frame_silence_seconds
        if limit <= 0 or not self.subscribed:
            return
        idle = self._feed.transport_idle_seconds()
        if idle is None or idle <= limit or self.is_market_open() is not True:
            return
        self._watchdog(watchdog_alerts.SILENT,
                       f"connected but silent for {int(idle)}s in open session (no frame at all, not even a "
                       f"ping) — forcing a full reconnect.",
                       cause_detail=f"{len(self.subscribed)} symbol(s) subscribed")
        self.restart_required = True

    def watch_backlog(self, now: float) -> None:
        """
        One sample of the bytes waiting unread on the socket (once a second,
        from the watch loop). Ten in a row at or above FEED_BACKLOG_ALERT_KB in
        open session is this process falling behind the vendor: said once,
        and carried in the heartbeat's error until ten in a row below it.
        Nothing is restarted for it — a reconnect would only lose the bytes.
        """
        backlog = self._feed.socket_backlog_bytes()
        if backlog is None:
            return
        self._backlog_samples.append(backlog)
        high = (self._backlog_alert_bytes > 0 and backlog >= self._backlog_alert_bytes
                and self.is_market_open() is True)
        if high:
            self._backlog_low = 0
            self._backlog_high += 1
            if self._backlog_episode_start is None:
                self._backlog_episode_start = now
            if self._backlog_high >= self.BACKLOG_ALERT_SAMPLES and not self._behind_note:
                p99 = (self._feed.stats() or {}).get("pass_ms_p99")
                print(f"[{self._feed.key}] FALLING BEHIND: {backlog // 1024} KB unread on the socket for "
                      f"{self.BACKLOG_ALERT_SAMPLES}s (emit pass p99 {p99 if p99 is not None else '?'} ms)",
                      flush=True)
                self._behind_note = (f"falling behind the vendor: {backlog // 1024} KB unread on the socket "
                                     f"for {self.BACKLOG_ALERT_SAMPLES}s or more")
            return
        self._backlog_high = 0
        if not self._behind_note:
            self._backlog_episode_start = None
            return
        self._backlog_low += 1
        if self._backlog_low >= self.BACKLOG_CLEAR_SAMPLES:
            started = self._backlog_episode_start if self._backlog_episode_start is not None else now
            print(f"[{self._feed.key}] socket drained again after {int(now - started)}s", flush=True)
            self._behind_note = ""
            self._backlog_low = 0
            self._backlog_episode_start = None

    def print_stats_if_due(self, now: float) -> None:
        """
        Every STATS_EVERY_SECONDS, one line on how the read path did since the
        last: frames and bytes read, the vendor's pings, packets replaced
        before a pass could hand them on, packets for nobody, the socket's
        unread bytes, ticks handed on, how long the passes and the stream
        writes took, and the stream's errors so far. Only for a feed with
        stats.
        """
        if self._stats_base is not None and now - self._stats_base[0] < self.STATS_EVERY_SECONDS:
            return
        stats = self._feed.stats()
        if not stats:
            return
        if self._stats_base is None:
            self._stats_base = (now, stats, self._stream_batches)
            self._backlog_samples = []
            return
        base_at, base, base_batches = self._stats_base
        elapsed = now - base_at

        def delta(key):
            return (stats.get(key) or 0) - (base.get(key) or 0)

        frames = delta("frames")
        batches = self._stream_batches - base_batches
        recent = list(self._stream_batch_ms.copy())[-batches:] if batches > 0 else []
        samples, self._backlog_samples = self._backlog_samples, []
        self._stats_base = (now, stats, self._stream_batches)

        def ms(value):
            return "?" if value is None else f"{value:.1f}"

        def kb(value):
            return "?" if value is None else f"{value / 1024:.0f}"

        print(f"[{self._feed.key}] read path: frames {frames} ({frames / elapsed:.0f}/s), "
              f"{delta('bytes') / 1024 / elapsed:.0f} KB/s, pings {delta('pings')}, "
              f"superseded {100.0 * delta('superseded') / frames if frames else 0:.0f}%, "
              f"dropped {delta('dropped_unknown')}, "
              f"socket backlog p99 {kb(_percentile(samples, 0.99))} KB max {kb(max(samples) if samples else None)} KB"
              f" | emitted {delta('ticks_out') / elapsed:.0f} ticks/s, pass p99 {ms(stats.get('pass_ms_p99'))} ms "
              f"max {ms(stats.get('pass_ms_max'))} | stream batches {batches}, "
              f"p99 {ms(_percentile(recent, 0.99))} ms, errors {self._publish_errors}", flush=True)

    def _connect_once_and_watch(self) -> None:
        self.restart_required = False
        self.socket_connected = False
        self.disconnected_since = None
        # A new socket's backlog is its own: a run above the alert line on the
        # last one does not count towards this one's. A standing FALLING
        # BEHIND note stays until this socket has drained for ten seconds.
        self._backlog_high = 0
        self._backlog_low = 0
        if not self._behind_note:
            self._backlog_episode_start = None

        refused = self.rejected_credential
        credential = self._feed.acquire_credentials(not_this=refused)
        self.rejected_credential = None
        self.credential_rejected_at = None
        self.refused_detail = None
        self.connected_at = None
        self.last_message = None
        self.current_credential = credential

        print(f"[{self._feed.key}] connecting ...", flush=True)
        self._feed.connect(credential, self.on_ticks, self.on_event)

        connect_started = time.monotonic()
        while not self.restart_required and not self._stop.is_set():
            self._stop.wait(1)
            self.check_pending_subscriptions()

            if self.refused_detail:
                self._watchdog(watchdog_alerts.REFUSED,
                               f"the vendor refused the login ({self.refused_detail}) — "
                               f"trying again in {self.REFUSED_BACKOFF_SECONDS}s.")
                self._stop.wait(self.REFUSED_BACKOFF_SECONDS)
                self.restart_required = True
                continue

            if self.rejected_credential is not None:
                self._watchdog(watchdog_alerts.CREDENTIAL,
                               "the credential was rejected — rebuilding once a different one exists.",
                               cause_detail=self.last_error)
                self.restart_required = True
                continue

            if not self.socket_connected:
                down_base = self.disconnected_since if self.disconnected_since is not None else connect_started
                down_for = time.monotonic() - down_base
                if down_for > self.DISCONNECT_RESTART_SECONDS:
                    self._watchdog(watchdog_alerts.DISCONNECT,
                                   f"socket down for {int(down_for)}s — forcing a full reconnect.",
                                   cause_detail=self.last_error)
                    self.restart_required = True
                continue

            if self.subscribed:
                silent = feed_silent_for(time.monotonic(), self.socket_connected, self.connected_at, self.last_message)
                if silent is not None and silent > self.STALL_AFTER_SECONDS and self.is_market_open() is True:
                    self._watchdog(watchdog_alerts.SILENT,
                                   f"connected but silent for {int(silent)}s in open session — forcing a full "
                                   f"reconnect.",
                                   cause_detail=(f"{len(self.subscribed)} symbol(s) subscribed"
                                                 + ("; not one message since the connect"
                                                    if self.last_message is None else "")))
                    self.restart_required = True

            if not self.restart_required:
                self.check_frame_silence()
            now = time.monotonic()
            self.watch_backlog(now)
            self.print_stats_if_due(now)

        print(f"[{self._feed.key}] closing the connection", flush=True)
        try:
            self._feed.close()
        except Exception as ex:
            print(f"[{self._feed.key}] warning while closing: {ex}", flush=True)
        self.socket_connected = False
        self._stop.wait(1)
