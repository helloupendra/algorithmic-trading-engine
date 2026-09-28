#!/usr/bin/env python3
"""
Synthetic live ticks for the local dev stack (scripts/dev/local-live.sh).

WHAT IT DOES
------------
Posts made-up prices to the dev API exactly the way the real ingestor posts
real ones: batches of up to 50 ticks every 150 ms to
POST /api/LiveData/ticks/upsert-batch, signed in as the engine's service
account, plus a heartbeat every 10 s and (unless --no-redis) the same ticks on
the dev Redis stream market:ticks. The API stores them, marks the open
positions with them and pushes each batch to the console over the hub
("ReceiveTicks"), so Data -> Live feeds, Positions and the Desk move.

WHAT IT PRICES
--------------
NSE:NIFTY50-INDEX, NSE:NIFTYBANK-INDEX and BSE:SENSEX-INDEX as correlated
random walks at real-world size (NIFTY about a point a second), the NIFTY
options at five strikes around the money for the next Tuesday expiry priced
off that NIFTY with Black-Scholes, and the near MCX CRUDEOIL future. Options
and the future carry a bid and an ask (paper fills take the far side); indices
carry only a last price, as the real feeds send them. The universe is the one
`seed-positions.py --setup` loaded into the dev API (.dev-live/universe.json).

SAFETY
------
Only this machine: the API and Redis addresses must be loopback, the sign-in
is the dev stack's generated service account (it exists in no other
database), and no proxy is used. It never sends a process id with its
heartbeat, so the API never records it as a feed it could later stop.

USAGE
-----
    python3 scripts/dev/replay-ticks.py                      # until Ctrl-C
    python3 scripts/dev/replay-ticks.py --rate 40 --seconds 120
    python3 scripts/dev/replay-ticks.py --symbols NSE:NIFTY50-INDEX,MCX:CRUDEOIL26OCTFUT
    python3 scripts/dev/replay-ticks.py --vol-mult 5         # a livelier screen
"""

from __future__ import annotations

import argparse
import json
import random
import signal
import socket
import sys
import time
from datetime import datetime, timezone
from typing import Any

import _devlive as dev

FLUSH_INTERVAL = 0.15      # seconds, as the ingestor's TickPump
BATCH_MAX = 50             # ticks per POST, as the ingestor's TickPump
HEARTBEAT_SECONDS = 10.0
GIVE_UP_SECONDS = 30.0     # an API that has gone away (local-live.sh down) ends the replay
STREAM = "market:ticks"
STREAM_MAXLEN = 100_000


class RedisStream:
    """XADD to the dev Redis over a bare socket (RESP), standard library only.

    The same fields the engine's RedisTickPublisher writes: payload (the
    normalized tick as JSON), symbol, exchange, dataType.
    """

    def __init__(self, host: str, port: int) -> None:
        if host not in dev.LOOPBACK_HOSTS:
            raise SystemExit(f"Refusing Redis at {host}: the dev scripts only talk to this machine.")
        self._sock = socket.create_connection((host, port), timeout=5)
        self._file = self._sock.makefile("rb")

    @staticmethod
    def _command(*parts: str) -> bytes:
        out = [f"*{len(parts)}\r\n".encode()]
        for part in parts:
            data = part.encode("utf-8")
            out.append(b"$%d\r\n%s\r\n" % (len(data), data))
        return b"".join(out)

    def _reply(self) -> None:
        line = self._file.readline()
        if not line:
            raise ConnectionError("Redis closed the connection")
        kind = line[:1]
        if kind == b"-":
            raise RuntimeError(line[1:].decode().strip())
        if kind == b"$":
            size = int(line[1:])
            if size >= 0:
                self._file.read(size + 2)

    def publish(self, ticks: list[dict[str, Any]]) -> None:
        commands = []
        received = dev.utc_stamp(datetime.now(timezone.utc))
        for t in ticks:
            exchange = t["symbol"].split(":", 1)[0]
            normalized = {
                "exchange": exchange, "symbol": t["symbol"], "dataType": t["dataType"],
                "exchangeTimestampUtc": t["exchangeTimestampUtc"], "lastTradedPrice": t["lastTradedPrice"],
                "bidPrice": t.get("bidPrice"), "askPrice": t.get("askPrice"),
                "bidSize": t.get("bidSize"), "askSize": t.get("askSize"),
                "open": t.get("open"), "high": t.get("high"), "low": t.get("low"), "close": t.get("prevClose"),
                "volume": t.get("volume"), "openInterest": t.get("openInterest"),
                "receivedUtc": received, "rawPayload": "", "sourceKey": t["sourceKey"], "isReplay": False,
            }
            commands.append(self._command(
                "XADD", STREAM, "MAXLEN", "~", str(STREAM_MAXLEN), "*",
                "payload", json.dumps(normalized, separators=(",", ":")),
                "symbol", t["symbol"], "exchange", exchange, "dataType", t["dataType"]))
        self._sock.sendall(b"".join(commands))
        for _ in commands:
            self._reply()

    def close(self) -> None:
        try:
            self._sock.close()
        except OSError:
            pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Post synthetic live ticks to the local dev API.")
    parser.add_argument("--rate", type=float, default=20.0, help="ticks per second across all symbols (default 20)")
    parser.add_argument("--seconds", type=float, default=0.0, help="stop after this long (default: run until Ctrl-C)")
    parser.add_argument("--symbols", default="", help="comma-separated subset of the dev universe (default: all)")
    parser.add_argument("--vol-mult", type=float, default=1.0, help="scale every volatility (default 1.0)")
    parser.add_argument("--seed", type=int, default=None, help="random seed, for a repeatable walk")
    parser.add_argument("--no-redis", action="store_true", help="post to the API only, not the Redis stream")
    parser.add_argument("--quiet", action="store_true", help="one line a minute instead of every 5 s")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.rate <= 0:
        raise SystemExit("--rate must be positive.")

    state = dev.load_state()
    universe = dev.load_universe()
    if args.symbols:
        wanted = {s.strip() for s in args.symbols.split(",") if s.strip()}
        unknown = wanted - {x.symbol for x in universe}
        if unknown:
            raise SystemExit(f"Not in the dev universe: {', '.join(sorted(unknown))}. "
                             f"Known: {', '.join(x.symbol for x in universe)}")
        chosen = [x for x in universe if x.symbol in wanted]
    else:
        chosen = universe

    api = dev.DevApi.as_role(state, "service")
    api.login()

    market = dev.Market(universe, vol_mult=args.vol_mult, rng=random.Random(args.seed))
    try:
        latest = {q["symbol"]: q.get("lastTradedPrice") for q in api.get("/api/LiveData/latest/all") or []}
        market.start_from({k: v for k, v in latest.items() if v})
    except dev.ApiError as error:
        print(f"Could not read the latest quotes, starting from the default levels: {error}", flush=True)

    redis: RedisStream | None = None
    if not args.no_redis and state.get("REDIS_PORT"):
        try:
            redis = RedisStream("127.0.0.1", int(state["REDIS_PORT"]))
        except OSError as error:
            print(f"Redis stream off (could not connect: {error}); posting to the API only.", flush=True)

    stopping = False

    def stop(_signum: int, _frame: Any) -> None:
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)

    print(f"Replaying {len(chosen)} symbols at {args.rate:g} ticks/s into {api.base_url} "
          f"as {api.username}{' and Redis ' + STREAM if redis else ''}. Ctrl-C to stop.", flush=True)

    started = time.monotonic()
    next_flush = started
    next_heartbeat = started
    next_report = started + (60 if args.quiet else 5)
    owed = 0.0
    posted = failed = 0
    failing_since: float | None = None
    weights = [3 if x.kind == "option" else 2 for x in chosen]

    while not stopping:
        now_mono = time.monotonic()
        if args.seconds and now_mono - started >= args.seconds:
            break
        if now_mono < next_flush:
            time.sleep(next_flush - now_mono)
            continue
        next_flush += FLUSH_INTERVAL

        now = datetime.now(timezone.utc)
        market.step(now)
        owed += args.rate * FLUSH_INTERVAL
        count = int(owed)
        owed -= count
        if count == 0:
            continue

        picks = market.rng.choices(chosen, weights=weights, k=count)
        # One tick per symbol per flush at most: the newest price is the one that matters.
        batch = [market.tick(x, now) for x in {x.symbol: x for x in picks}.values()]
        for start in range(0, len(batch), BATCH_MAX):
            chunk = batch[start:start + BATCH_MAX]
            try:
                api.post("/api/LiveData/ticks/upsert-batch", chunk)
                posted += len(chunk)
                failing_since = None
            except (dev.ApiError, OSError) as error:
                failed += len(chunk)
                failing_since = failing_since or now_mono
                print(f"TICK BATCH FAILED: {error}", flush=True)
                if now_mono - failing_since > GIVE_UP_SECONDS:
                    print(f"The API has refused every batch for {GIVE_UP_SECONDS:.0f} s; stopping.", flush=True)
                    stopping = True
                    break
            if redis is not None:
                try:
                    redis.publish(chunk)
                except (OSError, RuntimeError, ConnectionError) as error:
                    print(f"Redis stream off after an error: {error}", flush=True)
                    redis.close()
                    redis = None

        if now_mono >= next_heartbeat:
            next_heartbeat = now_mono + HEARTBEAT_SECONDS
            try:
                # No processId: the API would record it as a feed's pid and
                # could later stop it as one.
                api.post("/api/LiveData/heartbeat", {
                    "sourceName": dev.SOURCE_KEY,
                    "status": "Running",
                    "lastHeartbeatUtc": dev.utc_stamp(now),
                    "currentSubscribedSymbols": [x.symbol for x in chosen],
                    "lastError": "",
                })
            except (dev.ApiError, OSError) as error:
                print(f"Heartbeat failed: {error}", flush=True)

        if now_mono >= next_report:
            next_report = now_mono + (60 if args.quiet else 5)
            nifty = market.spots.get("NIFTY")
            crude = market.spots.get("CRUDEOIL")
            print(f"{datetime.now().strftime('%H:%M:%S')}  posted {posted}  failed {failed}  "
                  f"NIFTY {nifty:,.2f}  CRUDEOIL {crude:,.0f}", flush=True)

    if redis is not None:
        redis.close()
    print(f"Stopped. Posted {posted} ticks, {failed} failed.", flush=True)
    return 0 if posted > 0 or failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
