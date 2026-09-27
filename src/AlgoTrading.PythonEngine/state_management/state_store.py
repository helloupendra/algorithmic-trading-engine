from __future__ import annotations

import json
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

import redis

from .state_models import StrategyState

#: How long a saved state outlives its last save. A run lasts one session at
#: most (the close stops it), and the runner deletes its keys when it exits;
#: this only reaps the state of a runner that was killed before it could.
STATE_TTL_SECONDS = 3 * 24 * 3600

#: Key in a saved payload naming the value types JSON could not hold and that
#: were written as their text instead. Such a state cannot be read back as it
#: was, so it is not recovered.
STRINGIFIED_KEY = "stringified"


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


class StrategyStateStore:
    """
    Redis-backed state store for strategy runner fault tolerance.

    Keys used:
      strategy:state:{run_id}
      strategy:lock:{run_id}
      strategy:heartbeat:{run_id}
    """

    def __init__(self, redis_client: redis.Redis, simulation_run_id: int) -> None:
        self.redis = redis_client
        self.simulation_run_id = simulation_run_id

        self.state_key = f"strategy:state:{simulation_run_id}"
        self.lock_key = f"strategy:lock:{simulation_run_id}"
        self.heartbeat_key = f"strategy:heartbeat:{simulation_run_id}"

    # ---------------------------------------------------------------------
    # State persistence
    # ---------------------------------------------------------------------
    def save(self, state: StrategyState) -> None:
        """
        Saves the state as JSON. A value JSON cannot hold is still written as
        its text, so a save never fails, but the payload then names what was
        lost (see STRINGIFIED_KEY): until 28 Sep SmcStructureBreak's structure
        reader went in as its repr and came back a string, and every tick after
        a reload raised AttributeError.
        """
        state.version += 1
        state.last_updated_utc = utc_now_iso()

        data = state.to_dict()
        stringified: List[str] = []

        def as_text(value: Any) -> str:
            stringified.append(type(value).__name__)
            return str(value)

        payload = json.dumps(data, separators=(",", ":"), default=as_text)
        if stringified:
            data[STRINGIFIED_KEY] = sorted(set(stringified))
            payload = json.dumps(data, separators=(",", ":"), default=str)

        self.redis.set(self.state_key, payload, ex=STATE_TTL_SECONDS)

    def load_payload(self) -> Optional[Dict[str, Any]]:
        """The saved payload as it was written, or None when there is none."""
        raw = self.redis.get(self.state_key)
        if not raw:
            return None
        data = json.loads(raw)
        return data if isinstance(data, dict) else None

    def load(self) -> Optional[StrategyState]:
        data = self.load_payload()
        return StrategyState.from_dict(data) if data is not None else None

    def clear(self) -> None:
        self.redis.delete(self.state_key)

    def forget(self, owner_id: str) -> None:
        """
        Everything this run kept in Redis: its state, its heartbeat and — when
        this runner holds it — its lock. For when the run stops: a runner that
        exits leaves a stopped run, and nothing will read its state again.
        """
        self.redis.delete(self.state_key, self.heartbeat_key)
        self.release_lock(owner_id)

    # ---------------------------------------------------------------------
    # Locking
    # ---------------------------------------------------------------------
    def try_acquire_lock(self, owner_id: str, ttl_ms: int = 30000) -> bool:
        """
        Acquire process ownership lock for this strategy run.
        Returns True if lock acquired.
        """
        return bool(self.redis.set(self.lock_key, owner_id, nx=True, px=ttl_ms))

    def refresh_lock(self, owner_id: str, ttl_ms: int = 30000) -> bool:
        """
        Refresh lock only if we still own it.
        Returns True if refreshed, False if lock is lost.
        """
        current = self.redis.get(self.lock_key)
        # Redis return bytes by default usually, so decode if necessary, assuming decoded connection
        if current is not None and (isinstance(current, bytes) and current.decode('utf-8') == owner_id) or current == owner_id:
            self.redis.pexpire(self.lock_key, ttl_ms)
            return True
        return False

    def release_lock(self, owner_id: str) -> None:
        current = self.redis.get(self.lock_key)
        if current is not None and (isinstance(current, bytes) and current.decode('utf-8') == owner_id) or current == owner_id:
            self.redis.delete(self.lock_key)

    # ---------------------------------------------------------------------
    # Heartbeat
    # ---------------------------------------------------------------------
    def heartbeat(self, state: Optional[StrategyState] = None, ttl_seconds: int = 60) -> None:
        now = utc_now_iso()
        self.redis.set(self.heartbeat_key, now, ex=ttl_seconds)

        if state is not None:
            state.heartbeat_utc = now

    def get_heartbeat(self) -> Optional[str]:
        val = self.redis.get(self.heartbeat_key)
        if isinstance(val, bytes):
            return val.decode('utf-8')
        return val
