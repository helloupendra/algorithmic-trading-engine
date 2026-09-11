"""
TrueData's real-time feed, as a VendorFeed.

Two things make this worth running beside FYERS rather than instead of it:

  * It signs in with a username and a password over the socket's query string.
    Nothing here waits for a person in front of a browser, which is the failure
    that cost this desk the morning of 2026-09-11.
  * Its trade message carries open interest. The FYERS subscription does not,
    which is why the alert rules that need OI have never once fired.

It writes to the same tables as every other feed, stamped truedata in SourceKey,
so the two can be compared on the same day for the same symbols before anything
is routed away from FYERS.
"""

import json
import threading
from datetime import datetime, timedelta, timezone

import websocket

#: The exchange's clock. Every TrueData timestamp is in it.
_IST_OFFSET = timedelta(hours=5, minutes=30)

from core.live.truedata_symbols import to_vendor
from core.live.vendor_feed import VendorFeed

#: Fields of the "trade" message, in the order TrueData sends them
#: (documentation v2.6, section 4.b.viii).
TRADE_FIELDS = (
    "symbol_id", "timestamp", "ltp", "ltq", "atp", "volume",
    "open", "high", "low", "prev_close", "oi", "prev_oi_close",
    "turnover", "special_tag", "tick_seq", "bid", "bid_qty", "ask", "ask_qty",
)

#: Fields of a touchline / "symbols added" row, which is a different order and
#: two fields longer at the front. Mixing the two up would put a symbol id where
#: a price belongs, so they are named separately rather than sliced by index.
TOUCHLINE_FIELDS = (
    "symbol", "symbol_id", "timestamp", "ltp", "ltq", "atp", "volume",
    "open", "high", "low", "prev_close", "oi", "prev_oi_close",
    "turnover", "bid", "bid_qty", "ask", "ask_qty",
)

#: Fields already stored in a column of their own, so they are dropped from the
#: payload we keep. Same rule as the FYERS feed: record what has no column, not
#: the whole message again.
_FIELDS_ALREADY_COLUMNS = frozenset({
    "symbol", "symbol_id", "timestamp", "ltp", "volume",
    "open", "high", "low", "prev_close", "bid", "ask", "bid_qty", "ask_qty",
})


def _number(value):
    """TrueData sends numbers as JSON strings. Absent and unparseable are None."""
    if value in (None, "", "-"):
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _integer(value):
    number = _number(value)
    return int(number) if number is not None else None


def _timestamp_utc(stamp):
    """
    "2026-09-11T14:02:32" is the exchange's clock. Stored as UTC, so the 5:30 is
    taken off here rather than anywhere downstream.
    """
    if not stamp:
        return None
    try:
        ist = datetime.fromisoformat(str(stamp))
    except ValueError:
        return None
    if ist.tzinfo is None:
        ist = ist.replace(tzinfo=timezone(_IST_OFFSET))
    return ist.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")



class TrueDataFeed(VendorFeed):
    """
    One TrueData socket.

    TrueData allows a single concurrent session per account: a second connection
    is refused with "User Already Connected" rather than displacing the first.
    That is reported as its own state, because the fix is to stop the other
    process and not to retry — a reconnect loop against it would spin all day
    saying nothing useful, which is the shape of failure this desk has had
    enough of.
    """

    key = "truedata"

    def __init__(self, username, password, host="push.truedata.in", port=8086, max_symbols=50):
        self.max_symbols = max_symbols
        self._username = username
        self._password = password
        self._url = f"wss://{host}:{port}?user={username}&password={password}"
        self._app = None
        self._thread = None
        self._closing = False
        # Bumped on every connect. A socket that is being torn down finishes on
        # its own thread and calls back afterwards; without this its "error" and
        # "disconnected" land on top of the connection that replaced it, and the
        # runner restarts a socket that is perfectly healthy.
        self._generation = 0

        # What we asked for, so a tick's symbol id can be answered with the
        # canonical name rather than translated back out of the vendor's.
        self._canonical_by_id = {}
        self._vendor_to_canonical = {}
        self._subscribed = set()
        self._lock = threading.Lock()

        self._on_tick = None
        self._on_state = None

    # ------------------------------------------------------------- lifecycle

    def connect(self, on_tick, on_state) -> None:
        self._on_tick = on_tick
        self._on_state = on_state
        self._closing = False
        self._generation += 1
        generation = self._generation

        def live(handler):
            """Ignore anything a superseded socket says on its way out."""
            def wrapped(*args):
                if generation == self._generation and not self._closing:
                    handler(*args)
            return wrapped

        self._app = websocket.WebSocketApp(
            self._url,
            on_open=live(lambda _: self._state("connected", self._url.split("?")[0])),
            on_message=live(lambda _, raw: self._on_message(raw)),
            on_error=live(lambda _, err: self._state("error", str(err))),
            on_close=live(lambda _, code, msg: self._state("disconnected", f"{code} {msg}")),
        )

        self._thread = threading.Thread(
            target=self._app.run_forever, name="truedata-socket", daemon=True,
            kwargs={"ping_interval": 30, "ping_timeout": 10},
        )
        self._thread.start()

    def close(self) -> None:
        self._closing = True
        if self._app is not None:
            try:
                self._app.close()
            except Exception:
                # Closing a socket that is already gone is not an error worth
                # raising out of a shutdown path.
                pass

    # ------------------------------------------------------------ subscribing

    def to_vendor(self, canonical_symbol: str) -> str | None:
        return to_vendor(canonical_symbol)

    def subscribe(self, canonical_symbols: list[str]) -> None:
        wanted = {}
        for canonical in canonical_symbols:
            vendor = self.to_vendor(canonical)
            if vendor:
                wanted[vendor] = canonical

        if not wanted:
            return

        with self._lock:
            room = self.max_symbols - len(self._subscribed) if self.max_symbols else len(wanted)
            if room <= 0:
                self._state("limit", f"already at the {self.max_symbols}-symbol limit; "
                                     f"{len(wanted)} more were not asked for")
                return
            if len(wanted) > room:
                self._state("limit", f"only {room} of {len(wanted)} symbols fit under the "
                                     f"{self.max_symbols}-symbol limit")
                wanted = dict(list(wanted.items())[:room])
            self._vendor_to_canonical.update(wanted)
            self._subscribed.update(wanted)

        self._send({"method": "addsymbol", "symbols": list(wanted)})

    def unsubscribe(self, canonical_symbols: list[str]) -> None:
        vendors = [v for v in (self.to_vendor(c) for c in canonical_symbols) if v]
        if not vendors:
            return
        with self._lock:
            self._subscribed.difference_update(vendors)
        self._send({"method": "removesymbol", "symbols": vendors})

    def _send(self, message: dict) -> None:
        if self._app is None or self._app.sock is None:
            return
        try:
            self._app.send(json.dumps(message))
        except Exception as ex:
            self._state("error", f"could not send {message.get('method')}: {ex}")

    # --------------------------------------------------------------- messages

    def _on_message(self, raw) -> None:
        try:
            message = json.loads(raw)
        except (TypeError, ValueError):
            # The binary framing is an option on this feed; this client asked
            # for JSON, so anything else is a surprise worth naming once.
            self._state("error", f"unreadable message: {str(raw)[:120]}")
            return

        if "trade" in message:
            self._emit(dict(zip(TRADE_FIELDS, message["trade"])), "trade")
            return

        if "bidask" in message:
            # Quote-only updates: no trade happened, so there is no LTP to
            # record. They still move the book, which is what the depth rules
            # read, so they are stored with the price left alone.
            self._emit(dict(zip(TRADE_FIELDS, message["bidask"])), "bidask")
            return

        text = str(message.get("message", "")).lower()

        if text == "heartbeat":
            self._state("heartbeat", str(message.get("timestamp", "")))
            return

        if "symbollist" in message:
            self._absorb_symbol_list(message.get("symbollist") or [])
            self._state("subscribed", f"{message.get('symbolsadded', 0)} symbol(s); "
                                      f"{message.get('totalsymbolsubscribed', 0)} total")
            return

        if message.get("success") is True and "real time data service" in text:
            self._state("authenticated",
                        f"segments={message.get('segments')} maxsymbols={message.get('maxsymbols')} "
                        f"subscription={message.get('subscription')} validity={message.get('validity')}")
            # Believe the vendor over the configuration: a trial that was raised
            # to 250 symbols should be usable without a redeploy.
            vendor_max = message.get("maxsymbols")
            if isinstance(vendor_max, int) and vendor_max > 0:
                self.max_symbols = vendor_max
            return

        if message.get("success") is False:
            detail = message.get("message") or "refused"
            # One session per account. Retrying this is pointless: the other
            # connection has to go first.
            state = "already-connected" if "already connected" in text else "refused"
            self._state(state, str(detail))
            return

        if text == "marketstatus" or "market" in text:
            self._state("market", str(message.get("data") or message.get("message")))

    def _absorb_symbol_list(self, rows) -> None:
        """Remember the id TrueData assigned, and post the snapshot it came with."""
        for row in rows:
            fields = dict(zip(TOUCHLINE_FIELDS, row))
            vendor = fields.get("symbol")
            symbol_id = str(fields.get("symbol_id") or "")
            canonical = self._vendor_to_canonical.get(vendor)
            if canonical and symbol_id:
                with self._lock:
                    self._canonical_by_id[symbol_id] = canonical
            if canonical:
                self._emit(fields, "touchline", canonical=canonical)

    def _emit(self, fields: dict, kind: str, canonical: str | None = None) -> None:
        if self._on_tick is None:
            return

        if canonical is None:
            symbol_id = str(fields.get("symbol_id") or "")
            canonical = self._canonical_by_id.get(symbol_id)
            if canonical is None:
                # A price for something we did not ask for cannot be stored
                # under any symbol we trust, so it is dropped rather than
                # guessed at.
                return

        payload = {
            "symbol": canonical,
            "dataType": "symbolUpdate",
            "sourceKey": self.key,
            "exchangeTimestampUtc": _timestamp_utc(fields.get("timestamp")),
            "lastTradedPrice": _number(fields.get("ltp")),
            "bidPrice": _number(fields.get("bid")),
            "askPrice": _number(fields.get("ask")),
            "bidSize": _integer(fields.get("bid_qty")),
            "askSize": _integer(fields.get("ask_qty")),
            "open": _number(fields.get("open")),
            "high": _number(fields.get("high")),
            "low": _number(fields.get("low")),
            "prevClose": _number(fields.get("prev_close")),
            "volume": _integer(fields.get("volume")),
            # The field this feed exists for. An index has none and sends zero,
            # which is reported as unknown rather than as "all positions closed".
            "openInterest": (_integer(fields.get("oi")) or None),
            "rawPayload": json.dumps(
                {k: v for k, v in fields.items()
                 if k not in _FIELDS_ALREADY_COLUMNS and v not in (None, "")}
                or {"kind": kind}
            ),
        }
        self._on_tick(payload)

    def _state(self, event: str, detail: str = "") -> None:
        if self._on_state is not None:
            self._on_state(event, detail)


def main() -> None:
    """
    Run the TrueData feed as its own process.

    Deliberately a second process rather than a mode of the FYERS one: the two
    feeds must be able to run side by side, writing the same tables under
    different SourceKeys, so they can be compared on the same session before
    anything is routed away from FYERS.
    """
    import os
    import signal

    from core.live.feed_runner import FeedRunner
    from core.safe_output import install_safe_stdio

    install_safe_stdio()

    username = os.getenv("TRUEDATA_USERNAME", "").strip()
    password = os.getenv("TRUEDATA_PASSWORD", "").strip()
    if not username or not password:
        raise SystemExit(
            "TRUEDATA_USERNAME and TRUEDATA_PASSWORD are not set. "
            "Add them to .env — they are never stored in the repository.")

    feed = TrueDataFeed(
        username,
        password,
        host=os.getenv("TRUEDATA_HOST", "push.truedata.in").strip() or "push.truedata.in",
        # 8086 is the sandbox and 8084 production; TrueData moves an account
        # from one to the other once integration is signed off.
        port=int(os.getenv("TRUEDATA_REALTIME_PORT", "8086")),
    )
    runner = FeedRunner(feed, source_name="python-truedata-ingestor")

    def shutdown(*_):
        print("[truedata] stopping", flush=True)
        runner.stop()

    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)

    print(f"[truedata] starting on {feed._url.split('?')[0]}", flush=True)
    runner.run()


if __name__ == "__main__":
    main()
