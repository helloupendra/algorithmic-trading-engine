"""
This file contains the reference instructions for integrating StrategyStateStore into the strategy runner.

A. Imports at the top of strategy_runner.py
Add:

import os
import threading
import time
from typing import Optional

import redis

from strategy_state.strategy_state_models import StrategyState, ActiveLeg
from strategy_state.strategy_state_store import StrategyStateStore

B. Redis client helper
Add this near the top of the file:

def build_redis_client() -> redis.Redis:
    return redis.Redis(
        host=os.getenv("REDIS_HOST", "localhost"),
        port=int(os.getenv("REDIS_PORT", "6379")),
        db=int(os.getenv("REDIS_DB", "0")),
        password=os.getenv("REDIS_PASSWORD") or None,
        decode_responses=True,
        socket_timeout=5,
    )

C. Startup: acquire lock + load or initialize state
Put this at the point where your runner starts after simulation_run_id, strategy_name, etc. are known:

redis_client = build_redis_client()

owner_id = f"strategy-runner-{simulation_run_id}-{os.getpid()}"

state_store = StrategyStateStore(redis_client, simulation_run_id)

# Try to acquire run lock
if not state_store.try_acquire_lock(owner_id, ttl_ms=30000):
    raise RuntimeError(
        f"Another strategy runner instance is already active for simulation_run_id={simulation_run_id}"
    )

# Try to recover previous state
state = state_store.load()

if state is None:
    # Fresh state
    state = StrategyState(
        simulation_run_id=simulation_run_id,
        strategy_name=strategy_name,
        mode=mode,  # e.g. "LivePaper"
        exchange=exchange,  # e.g. "NSE"
        underlying=underlying,  # e.g. "BANKNIFTY"
    )
    state_store.save(state)
    print(f"[STATE] Fresh strategy state initialized for run {simulation_run_id}")
else:
    print(f"[STATE] Recovered strategy state from Redis for run {simulation_run_id}")

    # IMPORTANT:
    # Reconcile with DB/API here before blindly trusting Redis state.
    #
    # Example:
    # open_positions = api_client.get_positions(simulation_run_id)
    # state = reconcile_state_with_positions(state, open_positions)
    #
    # For now, after reconciliation, save back:
    state_store.save(state)

D. Heartbeat + lock renewal loop
Add this in strategy_runner.py:

keepalive_running = True

def keepalive_loop():
    while keepalive_running:
        try:
            ok = state_store.refresh_lock(owner_id, ttl_ms=30000)
            if not ok:
                print("[STATE] ERROR: strategy lock lost. Another runner may have taken ownership.")
                break

            state_store.heartbeat(state)
            time.sleep(10)
        except Exception as ex:
            print(f"[STATE] WARN: heartbeat/lock refresh failed: {ex}")
            time.sleep(5)

keepalive_thread = threading.Thread(target=keepalive_loop, daemon=True)
keepalive_thread.start()

E. Persist state when a group opens
Wherever your strategy opens a new group / straddle structure, add:

# Example after opening a new group
state.current_group_id = current_group_id
state.last_trade_strike = atm_strike
state.atm_strike = atm_strike
state.active_expiry_date = str(active_expiry_date) if active_expiry_date else None
state.last_underlying_price = current_underlying_price

state.ce_list = ce_list[:] if ce_list else []
state.pe_list = pe_list[:] if pe_list else []
state.straddle_list = straddle_list[:] if straddle_list else []

state.active_legs = [
    ActiveLeg(
        symbol=ce_symbol,
        side="SELL",
        quantity=lot_size,
        entry_price=ce_entry_price,
        strike=atm_strike,
        option_type="CE",
        expiry_date=state.active_expiry_date,
        status="Open",
    ),
    ActiveLeg(
        symbol=pe_symbol,
        side="SELL",
        quantity=lot_size,
        entry_price=pe_entry_price,
        strike=atm_strike,
        option_type="PE",
        expiry_date=state.active_expiry_date,
        status="Open",
    ),
]

state.signal_count += 1
state_store.save(state)

print(f"[STATE] Saved state after opening group {state.current_group_id}")

F. Persist state when a group closes
Wherever your strategy closes the group:

state.current_group_id = None
state.active_legs = []
state.ce_list = []
state.pe_list = []
state.straddle_list = []

state_store.save(state)

print("[STATE] Saved state after closing group")

G. Persist state when strike / active legs shift
If your strategy changes ATM or active strikes:

state.atm_strike = new_atm_strike
state.last_trade_strike = new_trade_strike
state.last_underlying_price = latest_underlying_price

# If active legs changed, replace them
state.active_legs = updated_active_legs

state_store.save(state)

print(f"[STATE] Saved state after strike shift to {new_atm_strike}")

H. Persist recovery cursor
If your runner is consuming Redis market stream messages directly later, save:

state.last_processed_stream_id = stream_id
state_store.save(state)

If not, and you only have processed tick time:
state.last_processed_tick_time_utc = tick_time_utc
state_store.save(state)

I. Shutdown cleanup
At the bottom of your runner:

try:
    # your main strategy loop here
    ...
finally:
    keepalive_running = False
    try:
        state_store.release_lock(owner_id)
    except Exception as ex:
        print(f"[STATE] WARN: failed to release strategy lock: {ex}")
"""
