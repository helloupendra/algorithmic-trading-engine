"""
The Dhan feed as production runs it, from the tree under test, against the
LOCAL fake Dhan and a fake platform API. Nothing here reaches a broker.

    python feed_proc.py --tree ENGINE_DIR --port FAKE_DHAN_PORT --out OUT.json [--spinner]

Built the way run_feed.py builds it: DhanFeed.from_env() (DHAN_FEED_URL points
at wss://localhost:PORT, the credential is a fake one in the environment), a
real RedisTickPublisher on the local Redis (through the proxy port), and
FeedRunner(feed, http=FakeApi, market_open=lambda: True) started with
runner.run(), so every production thread exists: the socket reader, the
emitter (or the old conflation flusher), six tick flushers, the watchlist loop,
the heartbeat and the connection manager. On the fix's tree the GIL switch
interval, the stream timeout and the startup-heap freeze come from run_feed's
own helpers, read from the same switches.

The fake API answers the watchlist (158 symbols), the Dhan universe (the other
155), instrument resolve, a 404 session, and stores tick batches by sleeping
15 ms each and counting ticks per second. On SIGTERM it writes what it saw,
with two harness-only diagnostics: each generation-1/2 GC pass and each emit
pass slower than 50 ms, timed, for lining up against the socket's backlog.
"""

import argparse
import json
import os
import signal
import sys
import threading
import time
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))


class Response:
    def __init__(self, status=200, body=None):
        self.status_code = status
        self._body = body
        self.text = json.dumps(body) if body is not None else ""

    def json(self):
        return self._body

    def raise_for_status(self):
        if self.status_code >= 400:
            raise RuntimeError(f"HTTP {self.status_code} from the fake API")


class FakeApi:
    """The platform API as the feed and its runner ask it."""

    def __init__(self, watchlist, extras, names, store_ms=15.0):
        self.watchlist = watchlist
        self.extras = extras
        self.names = names
        self.store_seconds = store_ms / 1000.0
        self.lock = threading.Lock()
        self.stored_per_second = Counter()          # epoch second -> ticks stored
        self.stored_per_symbol = Counter()
        self.symbol_seconds = defaultdict(set)      # symbol -> epoch seconds it was stored in
        self.heartbeats = []

    def get(self, url, **_):
        path = url.split("/api/", 1)[1]
        if path.startswith("LiveData/watchlist"):
            return Response(200, [{"symbol": s, "isActive": True, "dataType": "symbolUpdate"} for s in self.watchlist])
        if path.startswith("Dhan/universe"):
            return Response(200, {"symbols": list(self.extras), "counts": {"loadtest": len(self.extras)}})
        if path.startswith("Dhan/session"):
            return Response(404, {"message": "no Dhan session (load test)"})
        if path.startswith("MarketSession/check"):
            return Response(200, {"isMarketOpen": True})
        return Response(404, {"message": f"no such path in the fake API: {path}"})

    def post(self, url, json=None, **_):
        path = url.split("/api/", 1)[1]
        if path.startswith("Dhan/instruments/resolve"):
            asked = (json or {}).get("symbols") or []
            return Response(200, {"resolved": {s: self.names[s] for s in asked if s in self.names},
                                  "unresolved": [s for s in asked if s not in self.names]})
        if path.startswith("LiveData/ticks/upsert-batch"):
            # What requests does before the API sees it, then the API's time.
            _json.dumps(json, allow_nan=False).encode("utf-8")
            time.sleep(self.store_seconds)
            second = int(time.time())
            with self.lock:
                self.stored_per_second[second] += len(json)
                for tick in json:
                    symbol = tick.get("symbol")
                    self.stored_per_symbol[symbol] += 1
                    self.symbol_seconds[symbol].add(second)
            return Response(200, {"stored": len(json)})
        if path.startswith("LiveData/heartbeat"):
            with self.lock:
                self.heartbeats.append({"t": time.time(), "status": (json or {}).get("status"),
                                        "lastError": (json or {}).get("lastError")})
            return Response(200, {})
        return Response(404, {"message": f"no such path in the fake API: {path}"})


_json = json


def spin(stop):
    """A thread that always wants the GIL: the worst neighbour the socket thread can have."""
    x = 0
    while not stop.is_set():
        for i in range(1000):
            x += i * i


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tree", required=True)
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--spinner", action="store_true")
    args = ap.parse_args()
    sys.path.insert(0, args.tree)
    sys.path.insert(0, HERE)

    os.environ["DHAN_FEED_URL"] = f"wss://localhost:{args.port}"
    os.environ["DHAN_CLIENT_ID"] = "loadtest-client"
    os.environ["DHAN_ACCESS_TOKEN"] = "loadtest-token"

    import core.config  # noqa: F401  (loads nothing: the tree copy has no .env)

    gil = None
    stream_timeout = None
    try:
        from market_data.live.run_feed import apply_gil_switch_interval, stream_timeout_from_env
        apply_gil_switch_interval("dhan")
        stream_timeout = stream_timeout_from_env("dhan")
        gil = sys.getswitchinterval()
    except ImportError:
        gil = sys.getswitchinterval()        # the baseline has neither switch

    import universe as U
    from core.live.feed_runner import FeedRunner
    from market_data.live.vendors.dhan import DhanFeed
    from messaging.redis_publisher import build_publisher_from_env

    instruments = U.build_universe()
    watchlist, extras = U.split_watchlist(instruments)
    api = FakeApi(watchlist, extras, U.dhan_names(instruments))

    feed = DhanFeed.from_env()
    feed._http = api
    feed._credentials_source = lambda: None

    try:
        publisher = build_publisher_from_env(stream_timeout=stream_timeout)
    except TypeError:
        publisher = build_publisher_from_env()   # the baseline's signature
    publisher.ensure_connection()

    # What the analysis needs from inside: each connect, each watchdog
    # restart, and when the stream last took a write.
    connects, watchdogs, published = [], [], []
    real_connect = feed.connect

    def counting_connect(*a, **k):
        connects.append(time.time())
        return real_connect(*a, **k)

    feed.connect = counting_connect

    for name in ("publish_ticks", "publish_tick"):
        original = getattr(publisher, name, None)
        if original is None:
            continue

        def wrapped(*a, _original=original, **k):
            result = _original(*a, **k)
            published.append(time.time())
            return result

        setattr(publisher, name, wrapped)

    # Diagnostics, harness only: every cyclic-GC pass of generation 1 or 2 (a
    # collection stops every thread, the socket reader included), and every
    # emit pass slower than 50 ms, with when it happened.
    gc_events, slow_passes = [], []
    gc_started = {}

    def on_gc(phase, info):
        if phase == "start":
            gc_started["t"] = time.perf_counter()
            gc_started["wall"] = time.time()
        elif info.get("generation", 0) >= 1 and "t" in gc_started:
            gc_events.append((gc_started["wall"], info["generation"],
                              round((time.perf_counter() - gc_started["t"]) * 1000, 2)))

    import gc
    gc.callbacks.append(on_gc)

    if hasattr(feed, "emit_pass"):
        real_pass = feed.emit_pass

        def timed_pass(*a, **k):
            started, wall = time.perf_counter(), time.time()
            try:
                return real_pass(*a, **k)
            finally:
                took = (time.perf_counter() - started) * 1000
                if took > 50:
                    slow_passes.append((wall, round(took, 1)))

        feed.emit_pass = timed_pass

    runner = FeedRunner(feed, http=api, publisher=publisher, market_open=lambda: True)
    real_watchdog = runner._watchdog

    def counting_watchdog(cause, text, cause_detail=""):
        watchdogs.append({"t": time.time(), "cause": cause, "text": text})
        return real_watchdog(cause, text, cause_detail)

    runner._watchdog = counting_watchdog

    stop = threading.Event()
    if args.spinner:
        threading.Thread(target=spin, args=(stop,), name="loadtest-spinner", daemon=True).start()

    frozen = None
    try:
        # As run_feed.main does just before runner.run(), on a tree that has it.
        from market_data.live.run_feed import freeze_startup_heap
        frozen = freeze_startup_heap("dhan")
    except ImportError:
        pass

    started_cpu = os.times()

    def safe(read, default=None):
        """One field of the report: the baseline tree lacks several of them."""
        try:
            return read()
        except Exception:  # noqa: BLE001
            return default

    def finish(*_):
        # Always exits, whatever the tree under test lacks.
        try:
            stop.set()
            cpu = os.times()
            with api.lock:
                result = {
                    "gil_switch_seconds": gil,
                    "stream_timeout": stream_timeout,
                    "ingest": getattr(feed, "_ingest", "inline (baseline)"),
                    "connects": list(connects),
                    "watchdogs": list(watchdogs),
                    "ticks_accepted": safe(lambda: runner.ticks_accepted),
                    "ticks_rejected": safe(lambda: runner.ticks_rejected),
                    "publish_errors": safe(lambda: runner._publish_errors),
                    "pump_dropped": safe(lambda: runner.pump.dropped_total()),
                    "pump_depth": safe(lambda: runner.pump.depth()),
                    "stored_per_second": dict(api.stored_per_second),
                    "stored_per_symbol": dict(api.stored_per_symbol),
                    "symbol_seconds": {s: sorted(v) for s, v in api.symbol_seconds.items()},
                    "published_times": published[-20000:],
                    "published_count": len(published),
                    "heartbeats": list(api.heartbeats),
                    "feed_stats": safe(lambda: feed.stats(), {}),
                    "cpu_user": cpu.user - started_cpu.user,
                    "cpu_system": cpu.system - started_cpu.system,
                    "health_note": safe(lambda: runner.health_note()),
                    "gc_frozen_objects": frozen,
                    "gc_events": list(gc_events),
                    "gc_counts": safe(lambda: gc.get_count()),
                    "gc_objects": safe(lambda: len(gc.get_objects())),
                    "slow_passes": list(slow_passes),
                }
            with open(args.out, "w") as f:
                json.dump(result, f)
            sys.stdout.flush()
        finally:
            os._exit(0)

    signal.signal(signal.SIGTERM, finish)
    threading.Thread(target=runner.run, name="loadtest-connection-manager", daemon=True).start()
    while True:
        signal.pause()


if __name__ == "__main__":
    main()
