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

    from core.live.feed_runner import FeedRunner
    from core.live.symbol_list import symbols_for
    from market_data.live.vendors import build_feed
    from messaging.redis_publisher import build_publisher_from_env

    feed = build_feed(vendor)
    fixed, _ = symbols_for(feed.key)

    publisher = build_publisher_from_env()
    publisher.ensure_connection()

    runner = FeedRunner(feed, publisher=publisher, fixed_symbols=fixed or None)

    def shutdown(*_):
        print(f"[{feed.key}] stopping", flush=True)
        runner.stop()
        sys.exit(0)

    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)

    print(f"[{feed.key}] STARTING LIVE FEED ({'replay' if feed.is_replay else 'live'}) — "
          f"source {runner.source_name}"
          + (f", {len(fixed)} named symbol(s) instead of the recording list" if fixed else ""), flush=True)
    runner.run()


if __name__ == "__main__":
    main()
