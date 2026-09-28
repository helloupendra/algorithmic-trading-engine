"""
CPU per frame on the Dhan feed's read path, in one process, with no sockets
and no contention: the number a busy machine cannot blur.

    python cpu_per_frame.py --tree REPO_ROOT [--frames 30000] [--rate 3000]

Replays the load test's frame mix through the tree's own DhanFeed and
FeedRunner, on a clock stepped 1/rate s per frame, with a real redis-py client
whose connection never touches a socket (commands are packed exactly as for a
server; replies are canned) and an API that does nothing. It reports thread
CPU (time.thread_time) per frame:

  baseline (no store/emit split): the socket thread's whole cost per frame —
    decode, merge, conflate, greeks, stream write — plus the conflation
    flusher's, run every 100 ms of the stepped clock;
  fix: the socket thread's cost per frame (_store_frame alone), and the
    emitter's per frame (emit_pass every 100 ms), separately.

Nothing here opens a network connection.
"""

import argparse
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))


class NoApi:
    status_code = 200
    text = ""

    def json(self):
        return {}

    def raise_for_status(self):
        return None

    def post(self, *a, **k):
        return self

    def get(self, *a, **k):
        return self


def socketless_redis():
    import redis

    class Connection(redis.Connection):
        def connect(self):
            self._sock = object()

        def send_packed_command(self, command, check_health=True):
            return None

        def read_response(self, disable_decoding=False, **kwargs):
            return "1790000000000-0"

        def can_read(self, timeout=0):
            return False

        def disconnect(self, *args, **kwargs):
            self._sock = None

        def check_health(self):
            return None

    return redis.Redis(connection_pool=redis.ConnectionPool(connection_class=Connection, decode_responses=True))


class Pipeline:
    """redis-py's pipeline packs commands itself; answer execute() with one id per command."""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tree", required=True, help="repository root of the tree to measure")
    ap.add_argument("--frames", type=int, default=30000)
    ap.add_argument("--rate", type=float, default=3000)
    args = ap.parse_args()
    engine = os.path.join(args.tree, "src", "AlgoTrading.PythonEngine")
    sys.path.insert(0, engine)
    sys.path.insert(0, HERE)

    import universe as U
    import fake_dhan
    from core.live.feed_runner import FeedRunner
    from market_data.live.vendors.dhan import DhanFeed, DhanInstrument, SEGMENTS
    from messaging.redis_publisher import RedisTickPublisher

    import core.greeks_calculator  # noqa: F401  (loaded before timing, as the fix does at startup)

    clock = [1000.0]
    instruments = U.build_universe()
    feed = DhanFeed("bench-client", "bench-token", http=NoApi(), credentials_source=lambda: None,
                    clock=lambda: clock[0])
    for inst in instruments:
        name = U.SEGMENT_NAMES[inst["seg"]]
        feed._subscribed[inst["canonical"]] = DhanInstrument(name, inst["sid"], "X")
        feed._canonical_by_key[(SEGMENTS[name], inst["sid"])] = inst["canonical"]

    publisher = RedisTickPublisher.__new__(RedisTickPublisher)
    publisher.stream_name, publisher.maxlen = "market:ticks", 500_000
    publisher.client = publisher.stream_client = socketless_redis()
    runner = FeedRunner(feed, http=NoApi(), publisher=publisher, api_base_url="http://x", verify_ssl=False,
                        market_open=lambda: True)
    feed._on_ticks = runner.on_ticks
    feed._on_event = lambda e, d="": None

    # The fake Dhan's own frames, with the websocket header taken off (the
    # client library hands on the payload).
    cycle = [frame[4:] if frame[1] == 126 else frame[2:] for frame in fake_dhan.build_cycle(instruments, args.rate)]
    split = hasattr(feed, "_store_frame")
    step = 1.0 / args.rate
    every = max(1, int(args.rate * 0.1))            # a pass or a flush every 100 ms of the stepped clock

    # Warm up: every instrument ticks once, so greeks have spots and nothing is first-time.
    for frame in cycle[:len(cycle)]:
        feed._on_message(frame)
    clock[0] += 2.0
    runner.pump.drain(10 ** 9)

    reader = emitter = 0.0
    for n in range(args.frames):
        frame = cycle[n % len(cycle)]
        clock[0] += step
        started = time.thread_time()
        if split:
            feed._store_frame(frame)
        else:
            feed._on_message(frame)
        reader += time.thread_time() - started
        if n % every == every - 1:
            started = time.thread_time()
            if split:
                feed.emit_pass()
            else:
                feed.flush_due()
            emitter += time.thread_time() - started
            runner.pump.drain(10 ** 9)

    per = 1e6 / args.frames
    label = "fix" if split else "baseline"
    print(f"{label}: socket thread {reader * per:.1f} us/frame -> at most {args.frames / reader:,.0f} frames/s "
          f"on one idle core; {'emitter' if split else 'conflation flusher'} {emitter * per:.1f} us/frame; "
          f"ticks handed on {runner.ticks_accepted:,}")


if __name__ == "__main__":
    main()
