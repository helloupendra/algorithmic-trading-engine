"""
The live data feed for any vendor — one entry point for all of them.

    python market_data/live/run_feed.py --vendor fyers
    python market_data/live/run_feed.py --vendor truedata

The API starts one of these per connector that declares live ticks. Which
adapter runs is the only thing --vendor decides; everything else — the Redis
stream the strategies read, the batched posting to the API, greeks, the
watchlist, the heartbeat, the watchdogs — is the same shared runner.
"""

import argparse
import os
import signal
import sys

# Absolute imports from the engine root, however this file was launched.
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..")))

#: FEED_GIL_SWITCH_MS: how long a Python thread may hold the interpreter before
#: another that wants it gets a turn. Python's own is 5 ms. The socket thread
#: needs only a sliver per frame, but waits a whole interval whenever another
#: thread is busy; measured on 28 Sep, a store-only reader fell behind a thread
#: that was always busy at 5 ms and kept up at 1 ms. 0 leaves Python's.
DEFAULT_GIL_SWITCH_MS = 1.0

#: FEED_REDIS_TIMEOUT_SECONDS: the strategy stream's own client gives up on a
#: write after this long, with no retries. 0 keeps the shared client and its
#: defaults (a 5 s timeout, retried ten times).
DEFAULT_REDIS_TIMEOUT_SECONDS = 2.0


def _number_from_env(name: str, default: float, vendor: str) -> float:
    raw = os.getenv(name, "").strip()
    if not raw:
        return default
    try:
        value = float(raw)
        if value < 0:
            raise ValueError
        return value
    except ValueError:
        print(f"[{vendor}] {name}={raw!r} is not a number of 0 or more — using {default:g}.", flush=True)
        return default


def apply_gil_switch_interval(vendor: str) -> None:
    """Shorten the interpreter's switch interval for this feed process (FEED_GIL_SWITCH_MS)."""
    ms = _number_from_env("FEED_GIL_SWITCH_MS", DEFAULT_GIL_SWITCH_MS, vendor)
    if ms <= 0:
        print(f"[{vendor}] GIL switch interval left at Python's {sys.getswitchinterval() * 1000:g} ms "
              f"(FEED_GIL_SWITCH_MS=0).", flush=True)
        return
    sys.setswitchinterval(ms / 1000.0)
    print(f"[{vendor}] GIL switch interval {ms:g} ms (FEED_GIL_SWITCH_MS; Python's default is 5 ms), so the "
          f"socket thread is not kept waiting behind a busy one.", flush=True)


def stream_timeout_from_env(vendor: str) -> float | None:
    """The strategy stream client's timeout in seconds (FEED_REDIS_TIMEOUT_SECONDS), or None for the shared client."""
    seconds = _number_from_env("FEED_REDIS_TIMEOUT_SECONDS", DEFAULT_REDIS_TIMEOUT_SECONDS, vendor)
    return seconds if seconds > 0 else None


def freeze_startup_heap(vendor: str) -> int:
    """
    FEED_GC_FREEZE (default 1; 0 leaves the collector alone): load the greeks
    libraries now, then move everything startup made out of the cyclic garbage
    collector's reach (gc.freeze), and return how many objects that was.

    A full collection stops every thread in the process, the socket reader
    with them, for as long as it takes to walk every tracked object — most of
    them numpy, scipy and vollib's, made once at startup and never garbage.
    Measured in the load test on two contended vCPUs, one such pass took
    391 ms mid-session. Frozen, those objects are not walked again; what the
    feed makes afterwards is collected as before, and reference counting
    frees frozen objects as it always did. The import itself moves here too:
    otherwise the first option tick pays for it, on the emitter, about 4 s.
    """
    raw = os.getenv("FEED_GC_FREEZE", "1").strip()
    if raw == "0":
        print(f"[{vendor}] cyclic GC left as it is (FEED_GC_FREEZE=0).", flush=True)
        return 0
    import gc
    try:
        import core.greeks_calculator  # noqa: F401
    except Exception as ex:  # noqa: BLE001 - the heartbeat reports a missing library
        print(f"[{vendor}] greeks libraries did not load at startup ({ex}).", flush=True)
    gc.collect()
    gc.freeze()
    frozen = gc.get_freeze_count()
    print(f"[{vendor}] {frozen} startup objects frozen out of the cyclic GC (FEED_GC_FREEZE), so a full "
          f"collection does not stop the socket reader to walk them.", flush=True)
    return frozen


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description="Run one vendor's live data feed.")
    parser.add_argument("--vendor", required=True, help="connector key, e.g. fyers or truedata")
    args = parser.parse_args(argv)
    vendor = args.vendor.strip().lower()

    # Before anything prints: when the API that spawned this dies, stdout becomes
    # a closed pipe and a plain print() would raise inside every thread. Output
    # then goes to logs/engine/<vendor>-feed-<pid>.log instead.
    from core.safe_output import install_safe_stdio
    install_safe_stdio(name="ingestor" if vendor == "fyers" else f"{vendor}-feed")

    # Loads .env. The API starts this process without the desk's environment,
    # so credentials and feed settings exist only in that file.
    import core.config  # noqa: F401

    apply_gil_switch_interval(vendor)

    from core.live.feed_runner import FeedRunner
    from core.live.symbol_list import symbols_for
    from market_data.live.vendors import build_feed
    from messaging.redis_publisher import build_publisher_from_env

    feed = build_feed(vendor)
    fixed, _ = symbols_for(feed.key)

    publisher = build_publisher_from_env(stream_timeout=stream_timeout_from_env(vendor))
    publisher.ensure_connection()

    runner = FeedRunner(feed, publisher=publisher, fixed_symbols=fixed or None)

    def shutdown(*_):
        print(f"[{feed.key}] stopping", flush=True)
        runner.stop()
        sys.exit(0)

    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)

    freeze_startup_heap(feed.key)

    print(f"[{feed.key}] STARTING LIVE FEED ({'replay' if feed.is_replay else 'live'}) — "
          f"source {runner.source_name}"
          + (f", {len(fixed)} named symbol(s) instead of the recording list" if fixed else ""), flush=True)
    runner.run()


if __name__ == "__main__":
    main()
