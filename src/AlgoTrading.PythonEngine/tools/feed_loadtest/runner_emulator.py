"""
One strategy runner's read of market:ticks, as the tree under test does it.

    python runner_emulator.py --tree ENGINE_DIR --mode filtered|unfiltered --out OUT.json

It runs the tree's own RedisTickSubscriber.listen_for_ticks(block_ms=1000,
yield_idle=True) and, like the runner's loop, keeps only its spot: here the
load test's probe index, whose price is the time the fake Dhan sent it. For
each probe it records how long ago that was.

  filtered   — the fix's runner: RUNNER_NICE first (before any import that can
               start a thread), then build_subscriber_from_env(symbols={probe}),
               so every other entry is passed over on its symbol field;
  unfiltered — today's runner: build_subscriber_from_env(), every entry decoded.
"""

import argparse
import json
import os
import signal
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tree", required=True)
    ap.add_argument("--mode", choices=["filtered", "unfiltered"], required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    sys.path.insert(0, args.tree)
    sys.path.insert(0, HERE)

    nice = 0
    if args.mode == "filtered":
        from core.process_priority import lower_priority_from_env
        nice = lower_priority_from_env()

    import universe as U
    from messaging import redis_subscriber

    if args.mode == "filtered":
        subscriber = redis_subscriber.build_subscriber_from_env(symbols={U.PROBE_SYMBOL})
    else:
        subscriber = redis_subscriber.build_subscriber_from_env()

    lags = []
    seen = [0]

    def finish(*_):
        # Always exits: the subscriber catches Exception around its read, so a
        # handler that raised would leave the emulator running. The counters
        # are read with getattr because the baseline tree has fewer of them.
        try:
            with open(args.out, "w") as f:
                json.dump({"mode": args.mode, "nice": nice, "ticks_seen": seen[0], "probes": list(lags),
                           "skipped": getattr(subscriber, "skipped", None),
                           "undecodable": getattr(subscriber, "undecodable", None)}, f)
        finally:
            os._exit(0)

    signal.signal(signal.SIGTERM, finish)
    for tick in subscriber.listen_for_ticks(block_ms=1000, yield_idle=True):
        if tick is None:
            continue
        seen[0] += 1
        if tick.get("symbol") != U.PROBE_SYMBOL:
            continue
        now = time.time()
        lags.append((now, U.probe_lag_ms(int(now * 1000), float(tick.get("lastTradedPrice") or 0))))


if __name__ == "__main__":
    main()
