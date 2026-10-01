import json
import os
import time
from typing import Dict, Any, Generator, Iterable, Optional
import redis
from dotenv import load_dotenv

load_dotenv()

#: Entries per XREAD. A reader that keeps only some symbols, or waits between
#: reads, reads in bigger bites: the stream carries every contract the feed
#: records, and 23 runners each asking for 100 at a time was 23 × the round
#: trips for the same entries.
DEFAULT_READ_COUNT = 100
FILTERED_READ_COUNT = 1000


class RedisTickSubscriber:
    """
    Subscribes to market ticks from a Redis Stream.
    Optimized for live event-driven strategy execution.

    `symbols`: when given, only ticks for these symbols are decoded and
    yielded; every other entry is passed over on its `symbol` field alone,
    without reading its JSON (counted in `skipped`). An entry without that
    field is decoded and judged by its payload's symbol, so another producer's
    entries are never lost to the filter.

    `min_read_interval_ms`: after a read that emptied the stream, wait until
    this long after that read began before reading again, so a busy stream is
    read in batches rather than once per entry. A full read goes straight
    round again: the reader is behind.
    """

    def __init__(
        self,
        host: str = "localhost",
        port: int = 6379,
        db: int = 0,
        password: Optional[str] = None,
        stream_name: str = "market:ticks",
        decode_responses: bool = True,
        symbols: Optional[Iterable[str]] = None,
        min_read_interval_ms: float = 0,
        sleep=time.sleep,
        clock=time.monotonic,
    ) -> None:
        self.stream_name = stream_name
        self.client = redis.Redis(
            host=host,
            port=port,
            db=db,
            password=password,
            decode_responses=decode_responses,
        )
        # Only messages that arrive from this moment on.
        #
        # This is deliberate, and it is NOT the obvious bug it looks like. A
        # restarted runner does skip every tick published while it was down —
        # but replaying them would be worse than missing them. The strategy
        # reacts to each tick as if it were now: catching up on ten minutes of
        # stale prices would have it compute a stale ATM, resolve strikes around
        # a spot that has since moved, and open a position at the wrong strike
        # using current prices. A missed entry costs an opportunity; a
        # wrong-strike entry costs money.
        #
        # Making catch-up safe needs a staleness guard in the tick loop first,
        # so the runner can tell a replayed tick from a live one. Until then the
        # gap is reported (see the restart log below) rather than closed.
        self.last_id = "$"

        # Entries whose payload is not JSON. They used to be skipped with a bare
        # `pass`: a tick the strategy never saw, and nothing anywhere saying so.
        self.undecodable = 0

        self.symbols = frozenset(symbols) if symbols else None
        self.min_read_interval = max(float(min_read_interval_ms or 0), 0.0) / 1000.0
        self.read_count = (FILTERED_READ_COUNT if self.symbols is not None or self.min_read_interval > 0
                           else DEFAULT_READ_COUNT)
        # Entries passed over because they are for another symbol.
        self.skipped = 0
        self._sleep = sleep
        self._clock = clock

    def ping(self) -> bool:
        try:
            return bool(self.client.ping())
        except Exception:
            return False

    def start_from_now(self) -> bool:
        """
        Pin the reading position to the stream's newest entry as of this call.

        "$" means "whatever is newest when the first XREAD runs", so an entry
        added between now and that read is never seen. A recap runner says it is
        listening before its first read, and the replay player starts playing the
        moment every runner has said so: pinned here first, the replay's opening
        ticks cannot fall into that gap. Returns False, and stays at "$", when
        the stream cannot be asked (or does not exist yet).
        """
        try:
            info = self.client.xinfo_stream(self.stream_name)
        except Exception:
            return False
        last = info.get("last-generated-id") if isinstance(info, dict) else None
        if not last:
            return False
        self.last_id = last
        return True

    def listen_for_ticks(
        self,
        block_ms: int = 1000,
        yield_idle: bool = False,
    ) -> Generator[Optional[Dict[str, Any]], None, None]:
        """
        Yields normalized ticks as they arrive in the stream.
        Blocks for `block_ms` milliseconds waiting for new data.

        With `yield_idle=True` the generator also yields `None` after every read
        that yielded nothing — an empty read, or one whose entries were all for
        other symbols — and after a reconnect pause, so a consumer can keep a
        heartbeat going while the market is closed, the feed is stopped, or its
        own symbol is quiet on a busy stream, instead of blocking silently
        inside this loop.
        """
        # Say where we started. The gap between a runner dying and restarting is
        # invisible otherwise, and silence is the only part of the skip that
        # costs nothing to fix.
        print(f"[RedisTickSubscriber] reading {self.stream_name} from {self.last_id} "
              f"(ticks published before now are not replayed)", flush=True)

        symbols = self.symbols
        while True:
            try:
                # XREAD format: {stream_name: last_id}
                # block=1000 means it will hang for 1s waiting for a message.
                # If no message arrives, it returns empty list, and we loop again.
                streams = {self.stream_name: self.last_id}
                read_started = self._clock()

                # Returns: [['market:ticks', [('168123456789-0', {'payload': '...'})]]]
                messages = self.client.xread(streams, count=self.read_count, block=block_ms)

                entries = 0
                yielded = False
                for _, events in messages or []:
                    for message_id, event_data in events:
                        entries += 1
                        # Past it whatever happens to it: the next read starts after it.
                        self.last_id = message_id

                        symbol = event_data.get("symbol") if symbols is not None else None
                        if symbol and symbol not in symbols:
                            self.skipped += 1
                            continue

                        # Parse the JSON payload
                        raw_payload = event_data.get("payload")
                        if not raw_payload:
                            continue
                        try:
                            tick = json.loads(raw_payload)
                        except json.JSONDecodeError as ex:
                            self.undecodable += 1
                            if self.undecodable in (1, 100, 1000) or self.undecodable % 10000 == 0:
                                print(f"[RedisTickSubscriber] skipped {message_id}: its payload is not "
                                      f"JSON ({ex}); {self.undecodable} skipped so far.", flush=True)
                            continue
                        if symbols is not None and not symbol and (
                                not isinstance(tick, dict) or tick.get("symbol") not in symbols):
                            # No symbol field to judge it by: judged by what it says.
                            self.skipped += 1
                            continue
                        yielded = True
                        yield tick

                if not yielded and yield_idle:
                    yield None
                if 0 < entries < self.read_count and self.min_read_interval > 0:
                    # The stream is emptied: let it gather a batch.
                    wait = read_started + self.min_read_interval - self._clock()
                    if wait > 0:
                        self._sleep(wait)

            except redis.ConnectionError:
                print("[RedisTickSubscriber] Connection lost. Reconnecting in 5 seconds...")
                time.sleep(5)
                if yield_idle:
                    yield None
            except Exception as e:
                print(f"[RedisTickSubscriber] Error reading stream: {e}")
                time.sleep(1)
                if yield_idle:
                    yield None


def build_subscriber_from_env(symbols: Optional[Iterable[str]] = None) -> RedisTickSubscriber:
    """
    The runner's reader. With `symbols`, it keeps only those (RUNNER_STREAM_FILTER,
    default 1; 0 reads everything as before) and reads in batches of at most
    one per RUNNER_STREAM_BATCH_MS (default 100; 0 reads as fast as entries come).
    """
    use_filter = os.getenv("RUNNER_STREAM_FILTER", "1").strip() != "0"
    try:
        batch_ms = float(os.getenv("RUNNER_STREAM_BATCH_MS", "100").strip() or 100)
    except ValueError:
        batch_ms = 100.0
    return RedisTickSubscriber(
        host=os.getenv("REDIS_HOST", "localhost"),
        port=int(os.getenv("REDIS_PORT", "6379")),
        db=int(os.getenv("REDIS_DB", "0")),
        password=os.getenv("REDIS_PASSWORD") or None,
        stream_name=os.getenv("REDIS_STREAM_NAME", "market:ticks"),
        symbols=symbols if (symbols and use_filter) else None,
        min_read_interval_ms=batch_ms if symbols else 0,
    )
