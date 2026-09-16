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
    login refused.
"""

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


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


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

    def __init__(self, feed, http=None, publisher=None, fixed_symbols=None,
                 api_base_url: str | None = None, verify_ssl: bool | None = None,
                 watchlist_refresh_seconds: float = WATCHLIST_REFRESH_SECONDS,
                 heartbeat_seconds: float = HEARTBEAT_SECONDS,
                 market_open=None):
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
        self._market_open_cache = {"value": None, "checked_at": 0.0}
        self._stop = threading.Event()

    # ================================================================ ticks

    def on_ticks(self, ticks) -> None:
        now = time.monotonic()
        self.last_message = now
        self.ticks_accepted += len(ticks or [])
        for tick in ticks or []:
            try:
                self._accept(tick)
            except Exception as ex:
                self.last_error = str(ex)
                print(f"[{self._feed.key}] could not accept a tick: {ex}", flush=True)
        if ticks:
            self.last_error = ""

    def _accept(self, tick: dict) -> None:
        snapshot = bool(tick.pop("snapshot", False))
        if snapshot and self._feed.is_replay:
            # A replay's snapshot is stamped with the moment it was sent, not
            # with the replay's clock, and it is not part of the session being
            # replayed. Storing it froze all 49 contracts of the first TrueData
            # recap at a 17:31 price.
            return

        tick.setdefault("sourceKey", self._feed.key)
        tick.setdefault("dataType", "symbolUpdate")
        if self._feed.is_replay:
            tick["isReplay"] = True

        self.enricher.enrich(tick)

        symbol = tick.get("symbol")
        if symbol and not snapshot:
            self.last_real_tick[symbol] = time.time()

        self._publish(tick)
        self.pump.offer(tick)

    def _publish(self, tick: dict) -> None:
        # The strategies read the Redis stream, not the tables. The first
        # TrueData feed only posted to the API, and every running strategy sat
        # "waiting for ticks" while the tables filled.
        if self._publisher is None:
            return
        try:
            from messaging.redis_publisher import normalize_tick
            message = normalize_tick(tick)
            message["sourceKey"] = tick.get("sourceKey")
            message["isReplay"] = bool(tick.get("isReplay"))
            # The API still gets the full payload (see _accept); this only keeps
            # a bulky one off the stream every strategy has to read.
            message["rawPayload"] = tick.get("rawPayload", "") if self._feed.publish_raw_payload else ""
            self._publisher.publish_tick(message)
        except Exception as ex:
            self._publish_errors += 1
            if self._publish_errors in (1, 100, 1000) or self._publish_errors % 10000 == 0:
                print(f"[{self._feed.key}] could not publish a tick to the strategy stream "
                      f"({self._publish_errors} so far): {ex}", flush=True)

    def _post_batch(self, batch: list[dict]) -> None:
        url = f"{self._api}/api/LiveData/ticks/upsert-batch"
        try:
            response = self._http.post(url, json=batch, verify=self._verify, timeout=30)
            if response.status_code >= 400:
                print(f"[{self._feed.key}] TICK BATCH FAILED: {response.status_code} "
                      f"{response.text[:300]}", flush=True)
        except Exception as ex:
            print(f"[{self._feed.key}] TICK BATCH HTTP ERROR: {ex}", flush=True)

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
            "lastError": self.last_error,
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

    def _connect_once_and_watch(self) -> None:
        self.restart_required = False
        self.socket_connected = False
        self.disconnected_since = None

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
                print(f"[{self._feed.key}] WATCHDOG: the vendor refused the login ({self.refused_detail}) — "
                      f"trying again in {self.REFUSED_BACKOFF_SECONDS}s.", flush=True)
                self._stop.wait(self.REFUSED_BACKOFF_SECONDS)
                self.restart_required = True
                continue

            if self.rejected_credential is not None:
                print(f"[{self._feed.key}] WATCHDOG: the credential was rejected — rebuilding once a "
                      f"different one exists.", flush=True)
                self.restart_required = True
                continue

            if not self.socket_connected:
                down_base = self.disconnected_since if self.disconnected_since is not None else connect_started
                down_for = time.monotonic() - down_base
                if down_for > self.DISCONNECT_RESTART_SECONDS:
                    print(f"[{self._feed.key}] WATCHDOG: socket down for {int(down_for)}s — "
                          f"forcing a full reconnect.", flush=True)
                    self.restart_required = True
                continue

            if self.subscribed:
                silent = feed_silent_for(time.monotonic(), self.socket_connected, self.connected_at, self.last_message)
                if silent is not None and silent > self.STALL_AFTER_SECONDS and self.is_market_open() is True:
                    print(f"[{self._feed.key}] WATCHDOG: connected but silent for {int(silent)}s in open "
                          f"session — forcing a full reconnect.", flush=True)
                    self.restart_required = True

        print(f"[{self._feed.key}] closing the connection", flush=True)
        try:
            self._feed.close()
        except Exception as ex:
            print(f"[{self._feed.key}] warning while closing: {ex}", flush=True)
        self.socket_connected = False
        self._stop.wait(1)
