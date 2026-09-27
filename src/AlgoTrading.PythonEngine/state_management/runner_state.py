"""
state_management/runner_state.py

What the live runner starts from — a state it can trust from Redis, or a fresh
one it must warm up — and how it saves one.

Recovery skips warm-up, because replaying the warm-up bars onto a state that
already holds them double-counts (execution_runner.py says why). That makes a
bad recovered state worse than none: it is never repaired. Two kinds were
recovered until 28 Sep:

- a state saved before warm-up. The runner saved only inside its signal loop
  (and once, fresh, before warm-up), so every stored SmcStructureBreak state
  held an empty structure reader;
- a state JSON could not hold. SmcStructureBreak and Ghost keep a
  MarketStructure object in theirs; saved with ``default=str`` it came back as
  its repr, and every tick raised AttributeError.

Now a strategy writes and reads its own state through ``state_to_json`` /
``state_from_json``, the runner saves right after warm-up, and a saved state
that lost a value to text — or that its strategy cannot read back — is not
recovered: the runner starts fresh and warms up.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable, Dict

from state_management.state_models import StrategyState
from state_management.state_store import STRINGIFIED_KEY, StrategyStateStore


@dataclass
class StartingState:
    """What a runner starts from."""

    #: The record the store keeps for the run (bookkeeping around the strategy's state).
    record: StrategyState
    #: The strategy's own state, ready for on_bar.
    state: Dict[str, Any]
    #: True when it came from Redis, in which case warm-up must not run.
    recovered: bool
    #: One line for the log: what was found, and what the runner does about it.
    note: str


def starting_state(
    store: StrategyStateStore,
    strategy: Any,
    fresh_record: Callable[[], StrategyState],
) -> StartingState:
    """The stored state when it can be trusted as it was saved; otherwise a fresh one to warm up."""
    run_id = store.simulation_run_id

    def fresh(note: str) -> StartingState:
        return StartingState(fresh_record(), strategy.initialize_state(), False, note)

    try:
        payload = store.load_payload()
    except Exception as ex:  # noqa: BLE001 — an unreadable store is a fresh start, not a dead runner
        return fresh(f"Could not read the stored state of run {run_id} ({ex}); starting fresh.")

    if payload is None:
        return fresh(f"Fresh strategy state initialized for run {run_id}")

    lost = payload.get(STRINGIFIED_KEY)
    if lost:
        return fresh(f"The stored state of run {run_id} was saved with {', '.join(map(str, lost))} as text and "
                     "cannot be read back as it was; starting fresh and warming up.")

    try:
        record = StrategyState.from_dict(payload)
        state = strategy.state_from_json(record.strategy_data)
    except Exception as ex:  # noqa: BLE001 — see above
        return fresh(f"The stored state of run {run_id} could not be read back ({ex}); starting fresh and "
                     "warming up.")

    if state is None:
        return fresh(f"The stored state of run {run_id} is not one {strategy.name} can read back; starting "
                     "fresh and warming up.")

    return StartingState(record, state, True, f"Recovered strategy state from Redis for run {run_id}")


def save_strategy_state(store: StrategyStateStore, record: StrategyState, strategy: Any,
                        state: Dict[str, Any]) -> None:
    """Saves the strategy's state as it writes it for JSON."""
    record.strategy_data = strategy.state_to_json(state)
    store.save(record)
