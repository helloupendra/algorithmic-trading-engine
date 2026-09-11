"""
Everything a live feed does that is not the vendor's socket.

One of these drives one VendorFeed: it reads the platform's watchlist, keeps the
vendor's subscription level with it, pumps ticks into the API in batches, sends
the heartbeat the console reads, and restarts the socket when prices stop.

Written once so a second vendor is only its socket. The FYERS streamer still has
its own copy of this loop, grown around its SDK's callbacks; it moves onto this
class once TrueData has run beside it long enough to trust the shared path.
"""

import os
import threading
import time
from datetime import datetime, timezone

from core.api_client import build_session
from core.config import API_BASE_URL, VERIFY_SSL, WATCHLIST_REFRESH_SECONDS
from core.live.tick_pump import TickPump


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


class FeedRunner:
    """
    Drives one vendor feed against this platform.

    Nothing here knows a vendor's name beyond `feed.key`, which is the point:
    the only reason to write this twice would be to have two of them drift.
    """

    #: How long a socket is allowed to take to come up before the runner gives
    #: up on it. Opening one is asynchronous, so without this the first pass of
    #: the loop tears down a connection that was about to succeed — which is
    #: what the first run of this class actually did.
    CONNECT_GRACE_SECONDS = 20.0

    def __init__(self, feed, http=None, source_name=None,
                 silent_restart_seconds: float = 90.0,
                 fixed_symbols: list[str] | None = None):
        self._feed = feed
        self._http = http or build_session()
        self._source_name = source_name or f"python-{feed.key}-ingestor"
        # An explicit list replaces the recording list. The trial allows 50
        # symbols and the list carries 80, sorted, so the contracts a strategy
        # actually trades can fall outside the first 50; naming them is the only
        # way to be sure they are the ones subscribed.
        self._fixed_symbols = sorted(set(fixed_symbols)) if fixed_symbols else None
        self._silent_restart_seconds = silent_restart_seconds

        self._pump = TickPump(self._post_batch, label=feed.key)
        # What the vendor actually took, not what it was offered.
        self._subscribed: set[str] = set()
        # Offered and declined (no vendor name, or over the symbol limit). Kept
        # so they are not offered again every five seconds and reported every
        # five seconds with them; forgotten on reconnect, when a raised limit or
        # a new mapping could change the answer.
        self._declined: set[str] = set()
        self._stop = threading.Event()

        self._last_message = 0.0
        self._connected = False
        self._refused = None
        self._ticks = 0
        self._last_watchlist_utc = None
        self._connect_started = 0.0

    # ------------------------------------------------------------------ start

    def run(self) -> None:
        """Connect, keep the subscription current, and stay up. Blocks."""
        self._pump.start()
        self._connect()

        threading.Thread(target=self._heartbeat_loop, name="feed-heartbeat", daemon=True).start()

        while not self._stop.is_set():
            try:
                self._sync_watchlist()
            except Exception as ex:
                print(f"[{self._feed.key}] watchlist sync failed: {ex}", flush=True)

            self._check_silence()
            self._stop.wait(WATCHLIST_REFRESH_SECONDS)

    def stop(self) -> None:
        self._stop.set()
        self._feed.close()

    # ------------------------------------------------------------- the vendor

    def _connect(self) -> None:
        self._connected = False
        self._refused = None
        self._connect_started = time.monotonic()
        self._last_message = self._connect_started
        self._subscribed.clear()
        self._declined.clear()
        self._feed.connect(on_tick=self._on_tick, on_state=self._on_state)

    def _on_tick(self, payload: dict) -> None:
        self._last_message = time.monotonic()
        self._ticks += 1
        self._pump.offer(payload)

    def _on_state(self, event: str, detail: str = "") -> None:
        self._last_message = time.monotonic()

        if event in ("connected", "authenticated"):
            self._connected = True
        elif event == "disconnected":
            self._connected = False
        elif event in ("refused", "already-connected"):
            # Not a transport problem, so reconnecting would only repeat it.
            # Said once, loudly, and the loop stops trying.
            self._connected = False
            self._refused = f"{event}: {detail}"

        print(f"[{self._feed.key}] {event}: {detail}", flush=True)

    def _check_silence(self) -> None:
        """
        A socket that is open and saying nothing is the failure that reads as
        healthy. It is treated as a dead connection, because that is what it is.
        """
        if self._refused:
            return
        if not self._connected:
            # A socket that is still opening is not a socket that failed.
            if time.monotonic() - self._connect_started < self.CONNECT_GRACE_SECONDS:
                return
            print(f"[{self._feed.key}] no connection after "
                  f"{self.CONNECT_GRACE_SECONDS:.0f}s — reconnecting", flush=True)
            self._feed.close()
            self._connect()
            return

        silent_for = time.monotonic() - self._last_message
        if silent_for >= self._silent_restart_seconds:
            print(f"[{self._feed.key}] nothing heard for {silent_for:.0f}s while connected "
                  f"— restarting the socket", flush=True)
            self._feed.close()
            self._connect()

    # ---------------------------------------------------------- the watchlist

    def _sync_watchlist(self) -> None:
        wanted = set(self._read_watchlist())
        if not self._connected:
            return

        added = wanted - self._subscribed - self._declined
        removed = self._subscribed - wanted
        # A symbol that left the list is no longer declined either; if it comes
        # back it gets a fresh answer.
        self._declined &= wanted

        if added:
            accepted = set(self._feed.subscribe(sorted(added)))
            self._subscribed |= accepted
            self._declined |= added - accepted
        if removed:
            self._feed.unsubscribe(sorted(removed))
            self._subscribed -= removed

    def _read_watchlist(self) -> list[str]:
        if self._fixed_symbols is not None:
            self._last_watchlist_utc = utc_now_iso()
            return list(self._fixed_symbols)
        response = self._http.get(
            f"{API_BASE_URL}/api/LiveData/watchlist", verify=VERIFY_SSL, timeout=30)
        response.raise_for_status()
        rows = response.json()
        self._last_watchlist_utc = utc_now_iso()
        return sorted({
            row["symbol"] for row in rows
            if row.get("isActive")
            and (row.get("dataType") or "").lower() == "symbolupdate"
            and row.get("symbol")
        })

    # ------------------------------------------------------------- the output

    def _post_batch(self, batch: list[dict]) -> None:
        url = f"{API_BASE_URL}/api/LiveData/ticks/upsert-batch"
        try:
            response = self._http.post(url, json=batch, verify=VERIFY_SSL, timeout=30)
            if response.status_code >= 400:
                print(f"[{self._feed.key}] TICK BATCH FAILED: {response.status_code} "
                      f"{response.text[:300]}", flush=True)
        except Exception as ex:
            print(f"[{self._feed.key}] TICK BATCH HTTP ERROR: {ex}", flush=True)

    # ---------------------------------------------------------- the heartbeat

    def _heartbeat_loop(self) -> None:
        while not self._stop.is_set():
            try:
                self._send_heartbeat()
            except Exception as ex:
                print(f"[{self._feed.key}] heartbeat failed: {ex}", flush=True)
            self._stop.wait(15)

    def _send_heartbeat(self) -> None:
        status = "Refused" if self._refused else ("Connected" if self._connected else "Disconnected")
        self._http.post(
            f"{API_BASE_URL}/api/LiveData/heartbeat",
            json={
                "sourceName": self._source_name,
                "status": status,
                "lastHeartbeatUtc": utc_now_iso(),
                "currentSubscribedSymbols": sorted(self._subscribed),
                "lastError": self._refused,
                # Backlog, made visible: a drift that accumulates inside this
                # process with nothing reporting it is how a feed looks healthy
                # while prices arrive minutes late.
                "queueDepth": self._pump.depth(),
                "ticksDropped": self._pump.dropped_total(),
                "lastWatchlistRefreshUtc": self._last_watchlist_utc,
                # Lets the API find this process again after a restart, and stop
                # it. Without it the feed can only be killed from a terminal.
                "processId": os.getpid(),
            },
            verify=VERIFY_SSL, timeout=15,
        )
