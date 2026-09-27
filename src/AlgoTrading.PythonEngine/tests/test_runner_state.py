"""
What a live runner starts from, and what it leaves in Redis
(state_management/runner_state.py, state_management/state_store.py).

A recovered state skips warm-up, so a bad one is never repaired. Until 28 Sep
the runner saved its state before warm-up and inside its signal loop only, a
state JSON could not hold came back as text (SmcStructureBreak's structure
reader: AttributeError on every tick), and the keys of a stopped run stayed in
Redis for good.
"""

import json
import unittest
from datetime import datetime, timezone
from typing import Any, Dict, Optional

import _bootstrap  # noqa: F401

from state_management.runner_state import save_strategy_state, starting_state
from state_management.state_models import StrategyState
from state_management.state_store import STATE_TTL_SECONDS, StrategyStateStore
from strategies.base_strategy import BaseStrategy
from strategies.directional.smc_structure_break import SmcStructureBreakStrategy
from strategies.market_structure import Bar, MarketStructure

RUN_ID = 412


class FakeRedis:
    """The few Redis calls the store makes, over a dict, with the expiries it was asked for."""

    def __init__(self) -> None:
        self.data: Dict[str, str] = {}
        self.expiry: Dict[str, Optional[float]] = {}

    def get(self, key: str) -> Optional[str]:
        return self.data.get(key)

    def set(self, key: str, value: str, ex: Optional[int] = None, px: Optional[int] = None, nx: bool = False) -> bool:
        if nx and key in self.data:
            return False
        self.data[key] = value
        self.expiry[key] = ex if ex is not None else (px / 1000 if px is not None else None)
        return True

    def pexpire(self, key: str, ttl_ms: int) -> None:
        self.expiry[key] = ttl_ms / 1000

    def delete(self, *keys: str) -> None:
        for key in keys:
            self.data.pop(key, None)
            self.expiry.pop(key, None)


def fresh_record() -> StrategyState:
    return StrategyState(simulation_run_id=RUN_ID, strategy_name="SmcStructureBreak", mode="LivePaper",
                         exchange="NSE", underlying="NIFTY")


def warmed_up_smc_state(strategy: SmcStructureBreakStrategy) -> Dict[str, Any]:
    state = strategy.initialize_state()
    candles = [(100, 102, 99, 101), (101, 105, 100, 104), (104, 108, 103, 107), (107, 109, 106, 108),
               (108, 108, 104, 105)]
    for i, (o, h, low, c) in enumerate(candles):
        state["reader"].push(Bar(f"2026-09-25T04:{i * 5:02d}:00Z", o, h, low, c))
    state["fed"] = 5
    state["seen"] = "2026-09-25T04:20:00+00:00"
    return state


class StartingStateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.redis = FakeRedis()
        self.store = StrategyStateStore(self.redis, RUN_ID)

    def test_nothing_stored_is_a_fresh_start_that_warms_up(self):
        start = starting_state(self.store, SmcStructureBreakStrategy(), fresh_record)

        self.assertFalse(start.recovered)
        self.assertIsInstance(start.state["reader"], MarketStructure)
        self.assertEqual(start.state["fed"], 0)

    def test_recovered_stringified_state_runs_warmup(self):
        # What the runner saved before 28 Sep: json.dumps(default=str) wrote
        # the reader as its repr.
        strategy = SmcStructureBreakStrategy()
        record = fresh_record()
        record.strategy_data = warmed_up_smc_state(strategy)
        self.redis.set(self.store.state_key, json.dumps(record.to_dict(), default=str))

        start = starting_state(self.store, strategy, fresh_record)

        self.assertFalse(start.recovered, "a state that cannot be read back is not recovered: warm-up runs")
        self.assertIsInstance(start.state["reader"], MarketStructure)
        self.assertEqual(start.state["reader"].bars, [])
        self.assertIn("warming up", start.note)

    def test_a_state_that_lost_a_value_to_text_is_not_recovered(self):
        # A strategy whose state holds something JSON cannot, and no hook for it.
        class Clocked(BaseStrategy):
            name = "Clocked"

            def initialize_state(self) -> Dict[str, Any]:
                return {"since": None}

        strategy = Clocked()
        record = fresh_record()
        save_strategy_state(self.store, record, strategy, {"since": datetime(2026, 9, 28, 4, 0, tzinfo=timezone.utc)})

        self.assertEqual(json.loads(self.redis.data[self.store.state_key])["stringified"], ["datetime"])
        start = starting_state(self.store, strategy, fresh_record)
        self.assertFalse(start.recovered)
        self.assertEqual(start.state, {"since": None})
        self.assertIn("datetime", start.note)

    def test_a_state_saved_through_the_strategy_is_recovered_as_it_was(self):
        strategy = SmcStructureBreakStrategy()
        state = warmed_up_smc_state(strategy)
        save_strategy_state(self.store, fresh_record(), strategy, state)

        start = starting_state(self.store, SmcStructureBreakStrategy(), fresh_record)

        self.assertTrue(start.recovered)
        self.assertEqual(start.state["reader"].describe(), state["reader"].describe())
        self.assertEqual(start.state["reader"].swings, state["reader"].swings)
        self.assertEqual(start.state["fed"], 5)
        self.assertNotIn("stringified", json.loads(self.redis.data[self.store.state_key]))

    def test_an_unreadable_store_is_a_fresh_start(self):
        self.redis.set(self.store.state_key, "{not json")

        start = starting_state(self.store, SmcStructureBreakStrategy(), fresh_record)

        self.assertFalse(start.recovered)


class KeysTests(unittest.TestCase):
    def setUp(self) -> None:
        self.redis = FakeRedis()
        self.store = StrategyStateStore(self.redis, RUN_ID)

    def test_a_saved_state_expires(self):
        save_strategy_state(self.store, fresh_record(), SmcStructureBreakStrategy(), {"reader": MarketStructure(),
                                                                                    "bias": MarketStructure()})
        self.assertEqual(self.redis.expiry[self.store.state_key], STATE_TTL_SECONDS)

    def test_a_stopped_run_leaves_nothing_behind(self):
        self.assertTrue(self.store.try_acquire_lock("runner-a"))
        self.store.heartbeat()
        save_strategy_state(self.store, fresh_record(), BaseStrategy(), {"n": 1})

        self.store.forget("runner-a")

        self.assertEqual(self.redis.data, {})

    def test_forgetting_leaves_another_runners_lock_alone(self):
        self.assertTrue(self.store.try_acquire_lock("runner-a"))

        self.store.forget("runner-b")

        self.assertEqual(self.redis.data[self.store.lock_key], "runner-a")


if __name__ == "__main__":
    unittest.main()
