import json
import os
import time
from typing import Dict, Any, Generator, Optional
import redis
from dotenv import load_dotenv

load_dotenv()

class RedisTickSubscriber:
    """
    Subscribes to market ticks from a Redis Stream.
    Optimized for live event-driven strategy execution.
    """

    def __init__(
        self,
        host: str = "localhost",
        port: int = 6379,
        db: int = 0,
        password: Optional[str] = None,
        stream_name: str = "market:ticks",
        decode_responses: bool = True,
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

    def ping(self) -> bool:
        try:
            return bool(self.client.ping())
        except Exception:
            return False

    def listen_for_ticks(
        self,
        block_ms: int = 1000,
        yield_idle: bool = False,
    ) -> Generator[Optional[Dict[str, Any]], None, None]:
        """
        Yields normalized ticks as they arrive in the stream.
        Blocks for `block_ms` milliseconds waiting for new data.

        With `yield_idle=True` the generator also yields `None` after every empty
        read (and after a reconnect pause), so a consumer can keep a heartbeat
        going while the market is closed or the feed is stopped instead of
        blocking silently inside this loop.
        """
        # Say where we started. The gap between a runner dying and restarting is
        # invisible otherwise, and silence is the only part of the skip that
        # costs nothing to fix.
        print(f"[RedisTickSubscriber] reading {self.stream_name} from {self.last_id} "
              f"(ticks published before now are not replayed)", flush=True)

        while True:
            try:
                # XREAD format: {stream_name: last_id}
                # block=1000 means it will hang for 1s waiting for a message.
                # If no message arrives, it returns empty list, and we loop again.
                streams = {self.stream_name: self.last_id}

                # Returns: [['market:ticks', [('168123456789-0', {'payload': '...'})]]]
                messages = self.client.xread(streams, count=100, block=block_ms)

                if messages:
                    for stream_name, events in messages:
                        for message_id, event_data in events:
                            self.last_id = message_id

                            # Parse the JSON payload
                            raw_payload = event_data.get("payload")
                            if raw_payload:
                                try:
                                    tick = json.loads(raw_payload)
                                    yield tick
                                except json.JSONDecodeError:
                                    pass
                elif yield_idle:
                    yield None

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

def build_subscriber_from_env() -> RedisTickSubscriber:
    return RedisTickSubscriber(
        host=os.getenv("REDIS_HOST", "localhost"),
        port=int(os.getenv("REDIS_PORT", "6379")),
        db=int(os.getenv("REDIS_DB", "0")),
        password=os.getenv("REDIS_PASSWORD") or None,
        stream_name=os.getenv("REDIS_STREAM_NAME", "market:ticks")
    )
