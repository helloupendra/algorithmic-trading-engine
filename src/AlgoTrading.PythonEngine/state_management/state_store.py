from __future__ import annotations

import json
from datetime import datetime, timezone
from typing import Optional

import redis

from .state_models import StrategyState


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
        state.version += 1
        state.last_updated_utc = utc_now_iso()

        payload = json.dumps(state.to_dict(), separators=(",", ":"), default=str)
        self.redis.set(self.state_key, payload)

    def load(self) -> Optional[StrategyState]:
        raw = self.redis.get(self.state_key)
        if not raw:
            return None

        data = json.loads(raw)
        return StrategyState.from_dict(data)

    def clear(self) -> None:
        self.redis.delete(self.state_key)

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
