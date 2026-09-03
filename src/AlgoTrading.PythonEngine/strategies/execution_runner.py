"""
strategies/execution_runner.py

Live paper-trading runner for one strategy. Launched by the API as

    execution_runner.py --strategy NAME --strategy-id ID --user-id UID
                        --run-id RID --underlying U --spot-symbol S

It loads the run's parameters (lots, stop_loss, target, underlying, strategy
params), warms the strategy up on historical index bars, then consumes live
ticks from the Redis stream, hands the ATM contracts to the strategy and posts
every OPEN_GROUP/CLOSE_GROUP signal to the Simulator (paper fills) and to the
Strategy feed (UI). Stop-loss/target are enforced by the API's risk guard, not
here; the runner only logs them.
"""

from __future__ import annotations

import json
import time
import argparse
import signal
import sys
import os

# Add the parent directory to sys.path so that absolute-style imports work
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

import threading
import redis
import requests
from core.api_client import build_session, PlatformApiClient
import urllib3
from typing import List, Dict, Any, Optional

from messaging.redis_subscriber import build_subscriber_from_env
from strategies.base_strategy import StrategyInput, StrategySignal, OptionContract, BaseStrategy, BarFrame
from strategies.registry import discover_strategies

import core.fyers_orders as fyers_orders

try:
    # pyrefly: ignore [missing-import]
    from strategies.private_strategies import get_private_strategies
except ImportError:
    def get_private_strategies(): return {}


from state_management.state_models import StrategyState
from state_management.state_store import StrategyStateStore

from core.metrics import (
    start_metrics_server,
    REDIS_LAG,
    ORDERS_EMITTED,
    STRATEGY_LOOP_DURATION,
    TICK_PROCESSED
)
from datetime import datetime, timezone

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL, DEBUG_PRINT_MESSAGES
VERIFY_SSL = False


def build_redis_client() -> redis.Redis:
    return redis.Redis(
        host=os.getenv("REDIS_HOST", "localhost"),
        port=int(os.getenv("REDIS_PORT", "6379")),
        db=int(os.getenv("REDIS_DB", "0")),
        password=os.getenv("REDIS_PASSWORD") or None,
        decode_responses=True,
        socket_timeout=5,
    )




def round_to_100(price: float) -> int:
    return int(round(price / 100.0) * 100)


# Strike step per underlying when the option chain cannot be read from the API.
# Kept as floats end-to-end: stock options trade on 2.5 / 0.5 point grids, and
# the API (decimal strikeStep) and the launch dialog report exactly that value,
# so the runner must land on the same grid.
FALLBACK_STRIKE_STEPS: Dict[str, float] = {
    "NIFTY": 50.0,
    "BANKNIFTY": 100.0,
    "FINNIFTY": 50.0,
    "MIDCPNIFTY": 25.0,
    "SENSEX": 100.0,
}
DEFAULT_STRIKE_STEP = 50.0


def fallback_strike_step(underlying: str) -> float:
    return FALLBACK_STRIKE_STEPS.get((underlying or "").upper(), DEFAULT_STRIKE_STEP)


def strike_step_from_chain(chain: List[Dict[str, Any]]) -> Optional[float]:
    """
    Smallest positive gap between consecutive distinct strikes of one expiry,
    as a float so fractional grids survive (same rule as the C# underlyings
    endpoint). Returns None when the chain has fewer than two usable strikes.
    """
    strikes = set()
    for row in chain or []:
        raw = row.get("strikePrice")
        if raw is None:
            continue
        try:
            value = float(raw)
        except (TypeError, ValueError):
            continue
        if value > 0:
            strikes.add(value)

    ordered = sorted(strikes)
    if len(ordered) < 2:
        return None

    gaps = [b - a for a, b in zip(ordered, ordered[1:]) if b - a > 0]
    if not gaps:
        return None
    step = min(gaps)
    if step <= 0:
        return None
    # Six decimals is far below any exchange tick; it only removes float noise.
    return round(step, 6)


def resolve_strike_step(api: PlatformApiClient, underlying: str, expiry_date: str) -> float:
    """Strike step derived from the option chain, else the per-underlying fallback."""
    try:
        chain = api.get_option_chain(underlying, expiry_date)
        step = strike_step_from_chain(chain)
        if step:
            print(f"[{underlying}] Strike step {format_strike(step)} derived from {len(chain)} contracts of expiry {expiry_date}")
            return step
        print(f"[{underlying}] WARN: option chain for {expiry_date} has too few strikes; using fallback step")
    except Exception as ex:
        print(f"[{underlying}] WARN: could not read option chain for strike step: {ex}")
    step = fallback_strike_step(underlying)
    print(f"[{underlying}] Using fallback strike step {format_strike(step)}")
    return step


def round_to_step(price: float, step: float) -> float:
    """
    Nearest strike on the underlying's grid. Returns an int when the grid is
    whole-point (57600, not 57600.0) so symbols and log lines stay readable,
    and a float (102.5) on fractional grids.
    """
    step = float(step) if step and step > 0 else DEFAULT_STRIKE_STEP
    strike = round(round(price / step) * step, 6)
    if float(strike).is_integer():
        return int(strike)
    return strike


def format_strike(value: float) -> str:
    """102.5 stays 102.5; 57600.0 prints as 57600."""
    return str(int(value)) if float(value).is_integer() else f"{value:g}"


def parse_optional_number(value: Any) -> Optional[float]:
    """Float for numeric-looking values, None for null/blank/garbage."""
    if value is None:
        return None
    if isinstance(value, str) and not value.strip():
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def count_open_groups(state: Dict[str, Any]) -> int:
    """
    Best-effort count of open position groups from a strategy's state, using
    the conventions the bundled strategies follow. Unknown layouts yield 0.
    """
    if not isinstance(state, dict):
        return 0
    for key in ("open_groups", "active_groups", "groups"):
        value = state.get(key)
        if isinstance(value, (list, dict, set, tuple)):
            return len(value)
    if state.get("current_group_id"):
        return 1
    if state.get("is_invested") and state.get("group_id"):
        return 1
    return 0


def stamp_signal_metadata(sig: StrategySignal, inp: StrategyInput) -> None:
    """Every published signal carries its reason and the market context it fired in."""
    if sig.metadata is None:
        sig.metadata = {}
    sig.metadata["reason"] = sig.reason
    sig.metadata["spot_price"] = inp.spot_price
    sig.metadata["atm_strike"] = inp.atm_strike


def install_signal_handlers() -> None:
    """SIGTERM/SIGINT raise SystemExit so the `finally` block releases the Redis lock."""
    def _handler(signum: int, _frame: Any) -> None:
        try:
            name = signal.Signals(signum).name
        except ValueError:
            name = str(signum)
        print(f"[RUNNER] stopping: {name}", flush=True)
        raise SystemExit(0)

    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            signal.signal(sig, _handler)
        except (ValueError, OSError):
            # Not the main thread / unsupported platform: keep the default behaviour.
            pass


def map_contract(raw: Dict[str, Any]) -> OptionContract:
    return OptionContract(
        symbol=raw["symbol"],
        underlying=raw.get("underlying", ""),
        expiry_date=str(raw.get("expiryDate", "")),
        strike_price=float(raw.get("strikePrice") or 0),
        option_type=raw.get("optionType", ""),
        instrument_type=raw.get("instrumentType", ""),
        description=raw.get("description", ""),
    )


def print_signals(signals: List[StrategySignal]) -> None:
    if not signals:
        return

    print("\n================ SIGNALS ================")
    for sig in signals:
        print(json.dumps({
            "strategy_name": sig.strategy_name,
            "signal_type": sig.signal_type,
            "timestamp_utc": sig.timestamp_utc,
            "reason": sig.reason,
            "symbol": sig.symbol,
            "price": sig.price,
            "legs": sig.legs,
            "metadata": sig.metadata,
        }, indent=2, default=str))
    print("=========================================\n")


def print_strategy_state(state: dict):
    print("\n================ STATE =================")
    print(json.dumps(state, indent=2, default=str))
    print("========================================\n")


def safe_get_contract_price(api: PlatformApiClient, symbol: str) -> Optional[float]:
    """
    Try latest quote first.
    If unavailable, fallback to the latest bar close.
    """
    try:
        quote = api.get_latest_quote(symbol)
        ltp = quote.get("lastTradedPrice")
        if ltp is not None:
            return float(ltp)
    except Exception as ex:
        print(f"WARN: latest quote not available yet for {symbol}: {ex}")

    try:
        bars = api.get_recent_bars(symbol, resolution="1m", take=1)
        if bars:
            close_price = bars[0].get("close")
            if close_price is not None:
                return float(close_price)
    except Exception as ex:
        print(f"WARN: latest bar not available for {symbol}: {ex}")

    return None


def ensure_contracts_tracked(api: PlatformApiClient, contracts: Dict[str, OptionContract]) -> None:
    """
    Make sure CE/PE contracts are present in the live watchlist,
    so the ingestor can subscribe and populate latest quotes.
    """
    for _, contract in contracts.items():
        try:
            api.upsert_watchlist(contract.symbol, priority=80)
        except requests.exceptions.HTTPError as ex:
            if ex.response is not None and ex.response.status_code == 500:
                pass # Suppress harmless 500 error for paper trades
            else:
                print(f"WARN: failed to ensure watchlist for {contract.symbol}: {ex}")
        except Exception as ex:
            print(f"WARN: failed to ensure watchlist for {contract.symbol}: {ex}")


def enrich_signal_leg_prices(api: PlatformApiClient, sig: StrategySignal, expiry_date: str) -> StrategySignal:
    """
    Fill each signal leg with the real latest option price from the platform.
    Wait briefly for ingestor to populate if needed.
    """
    enriched_legs = []

    for leg in sig.legs:
        symbol = leg.get("symbol", "")
        leg_price = leg.get("price")

        # Resolve logical symbols (e.g. BANKNIFTY_PE_50300) to real broker symbols (e.g. NSE:BANKNIFTY...)
        if symbol and "_" in symbol and "NSE:" not in symbol:
            parts = symbol.split("_")
            if len(parts) == 3:
                try:
                    underlying = parts[0]
                    option_type = parts[1]
                    # Fractional strikes (102.5) are legitimate on stock grids.
                    strike_value = float(parts[2])
                    strike = int(strike_value) if strike_value.is_integer() else strike_value
                    exact_contract = api.get_exact_contract(underlying, expiry_date, strike, option_type)
                    if exact_contract and "symbol" in exact_contract:
                        symbol = exact_contract["symbol"]
                except Exception as ex:
                    print(f"WARN: Could not resolve exact contract for logical symbol {symbol}: {ex}")

        if symbol:
            # Tell the Live Data Ingestor to subscribe to this exact symbol
            try:
                api.upsert_watchlist(symbol)
            except requests.exceptions.HTTPError as ex:
                if ex.response is not None and ex.response.status_code == 500:
                    pass
            except Exception as ex:
                pass

        if leg_price is None and symbol:
            # Wait up to 35 seconds so the ingestor has time to refresh its watchlist (every 5-30s)
            leg_price = wait_for_contract_price(api, symbol, retries=35, delay_seconds=1)

        enriched_legs.append({
            "symbol": symbol,
            "side": leg.get("side", ""),
            "quantity": int(leg.get("quantity", 0)),
            "price": leg_price,
        })

    sig.legs = enriched_legs
    return sig


def signal_to_request(simulation_run_id: int, sig: StrategySignal) -> Dict[str, Any]:
    group_id = sig.metadata.get("group_id", "")

    return {
        "simulationRunId": simulation_run_id,
        "strategyName": sig.strategy_name,
        "signalType": sig.signal_type,
        "timestampUtc": sig.timestamp_utc,
        "groupId": group_id,
        "metadataJson": json.dumps(sig.metadata),
        "legs": sig.legs,
    }

def wait_for_contract_price(api: PlatformApiClient, symbol: str, retries: int = 10, delay_seconds: int = 1) -> Optional[float]:
    """
    Wait for the live ingestor to populate latest quote for a symbol.
    """
    for attempt in range(1, retries + 1):
        try:
            quote = api.get_latest_quote(symbol)
            ltp = quote.get("lastTradedPrice")
            if ltp is not None:
                return float(ltp)
        except Exception as ex:
            print(f"WARN: waiting for live quote for {symbol}, attempt {attempt}/{retries}: {ex}")

        time.sleep(delay_seconds)

    return None


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Run a specific strategy.")
    parser.add_argument("--strategy", type=str, required=True, help="Strategy name to run")
    parser.add_argument("--strategy-id", type=int, required=True, help="Strategy ID to run")
    parser.add_argument("--user-id", type=int, required=True, help="User ID running the strategy")
    parser.add_argument("--run-id", type=int, required=False, help="Optional: The SimulationRunId generated from the C# Backend API. If not provided, a new run will be created automatically.")
    parser.add_argument("--underlying", type=str, default="BANKNIFTY", help="The underlying instrument symbol (e.g., BANKNIFTY, NIFTY50, SENSEX).")
    parser.add_argument("--spot-symbol", type=str, default="NSE:NIFTYBANK-INDEX", help="The exact Fyers spot symbol for the underlying.")
    parser.add_argument("--metrics-port", type=int, default=8000, help="The port for the Prometheus metrics server.")
    args = parser.parse_args()

    install_signal_handlers()

    api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)

    # Dynamically discover all BaseStrategy subclasses in the strategies folder
    strategies_map = discover_strategies()
    
    # Update with any explicit overrides/parameterized instances from private_strategies.py
    strategies_map.update(get_private_strategies())

    if args.strategy not in strategies_map:
        print(f"ERROR: Strategy '{args.strategy}' not found. Available strategies: {list(strategies_map.keys())}")
        sys.exit(1)

    # When the run was created by the Deploy wizard, its parametersJson carries
    # the user's configuration — load it and hand it to the strategy factory.
    run_params: Dict[str, Any] = {}
    if args.run_id is not None:
        try:
            run_row = api.get_simulation_run(args.run_id)
            
            # Override spot_symbol and underlying from the frontend's SimulationRun
            run_symbol = run_row.get("symbol")
            if run_symbol:
                args.spot_symbol = run_symbol
                if "NIFTYBANK" in run_symbol:
                    args.underlying = "BANKNIFTY"
                elif "NIFTY50" in run_symbol:
                    args.underlying = "NIFTY"
                elif "SENSEX" in run_symbol:
                    args.underlying = "SENSEX"
                elif ":" in run_symbol:
                    # e.g., NSE:RELIANCE-EQ -> RELIANCE
                    args.underlying = run_symbol.split(":")[1].split("-")[0]
                else:
                    args.underlying = run_symbol
                print(f"Loaded symbol {args.spot_symbol} ({args.underlying}) from run {args.run_id}")

            raw = run_row.get("parametersJson") or "{}"
            run_params = json.loads(raw) if isinstance(raw, str) else (raw or {})
            if not isinstance(run_params, dict):
                run_params = {}
            if run_params:
                print(f"Loaded {len(run_params)} parameter(s) from run {args.run_id}: {sorted(run_params.keys())}")
        except Exception as ex:
            print(f"WARNING: could not load parameters for run {args.run_id}: {ex}. Using strategy defaults.")

    # The API writes the launch configuration into the run's parametersJson;
    # it is authoritative over the symbol-derived guess and the CLI defaults.
    run_underlying = str(run_params.get("underlying") or "").strip().upper()
    if run_underlying:
        args.underlying = run_underlying

    strategy = strategies_map[args.strategy](run_params)
    state = strategy.initialize_state()

    run_lots = BaseStrategy.lots_from(run_params, getattr(strategy, "default_lots", 1))
    strategy_lots = getattr(strategy, "lots", None)
    if not isinstance(strategy_lots, int) or strategy_lots < 1:
        strategy_lots = run_lots
    run_stop_loss = parse_optional_number(run_params.get("stop_loss"))
    run_target = parse_optional_number(run_params.get("target"))

    print(
        f"[CONFIG] strategy={args.strategy} run_id={args.run_id} underlying={args.underlying} "
        f"spot_symbol={args.spot_symbol} lots={strategy_lots} "
        f"stop_loss={run_stop_loss if run_stop_loss is not None else 'none'} "
        f"target={run_target if run_target is not None else 'none'} "
        f"(SL/target enforced by the API risk guard)",
        flush=True,
    )

    expiries = api.get_expiries(args.underlying)
    if not expiries:
        # Non-zero exit with the cause on stderr: the API records the last stderr
        # line in the run's stop reason, so the card says why instead of
        # "Runner exited (code 0)".
        message = (
            f"No option contracts loaded for {args.underlying} — import the F&O master "
            f"(NSE_FO/BSE_FO) first."
        )
        print(f"[{args.underlying}] ERROR: {message}", flush=True)
        print(message, file=sys.stderr, flush=True)
        sys.exit(2)

    today_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    valid_expiries = [x for x in expiries if str(x["expiryDate"]) >= today_str]

    if not valid_expiries:
        raise RuntimeError(f"No future expiries found for {args.underlying}")

    expiry_date = str(valid_expiries[0]["expiryDate"])
    print(f"Using expiry: {expiry_date}")

    strike_step = resolve_strike_step(api, args.underlying, expiry_date)

    run_id = args.run_id
    if run_id is None:
        print(f"No --run-id provided. Automatically creating a new LivePaper simulation run for {args.strategy}...")
        run_payload = {
            "mode": "LivePaper",
            "symbol": args.spot_symbol,
            "resolution": "1m",
            "strategyName": args.strategy,
            "initialCapital": 1000000,
            "userId": args.user_id
        }
        try:
            run_response = api.create_simulation_run(run_payload)
            run_id = run_response["id"]
            print(f"Successfully created SimulationRunId: {run_id}")
        except Exception as ex:
            print(f"Failed to create simulation run automatically: {ex}")
            sys.exit(1)

    last_seen_updated_utc = None

    print(f"[{args.underlying}] Starting continuous LIVE PAPER runner...")
    print(f"[{args.underlying}] Using underlying source symbol: {args.spot_symbol}")
    print(f"[{args.underlying}] UserId: {args.user_id}")
    print(f"[{args.underlying}] SimulationRunId: {run_id}")
    print(f"[{args.underlying}] Strategy: {args.strategy}")

    print(f"[{args.underlying}] Starting Prometheus metrics server on port {args.metrics_port}...")
    try:
        start_metrics_server(args.metrics_port)
    except Exception as e:
        print(f"[{args.underlying}] Failed to start metrics server on port {args.metrics_port}: {e}. Continuing without metrics.")

    redis_client = build_redis_client()
    owner_id = f"strategy-runner-{run_id}-{os.getpid()}"
    state_store = StrategyStateStore(redis_client, run_id)

    if not state_store.try_acquire_lock(owner_id, ttl_ms=30000):
        print(f"ERROR: Another strategy runner is already active for run_id={run_id}")
        sys.exit(1)

    loaded_state = state_store.load()
    if loaded_state is None:
        print(f"[STATE] Fresh strategy state initialized for run {run_id}")
        loaded_state = StrategyState(
            simulation_run_id=run_id,
            strategy_name=args.strategy,
            mode="LivePaper",
            exchange=args.spot_symbol.split(":")[0] if ":" in args.spot_symbol else "NSE",
            underlying=args.underlying,
        )
        loaded_state.strategy_data = state
        state_store.save(loaded_state)
    else:
        print(f"[STATE] Recovered strategy state from Redis for run {run_id}")
        state = loaded_state.strategy_data

    keepalive_running = True
    def keepalive_loop():
        while keepalive_running:
            try:
                ok = state_store.refresh_lock(owner_id, ttl_ms=30000)
                if not ok:
                    print("[STATE] ERROR: strategy lock lost.")
                    break
                state_store.heartbeat(loaded_state)
                time.sleep(10)
            except Exception as ex:
                print(f"[STATE] WARN: heartbeat failed: {ex}")
                time.sleep(5)

    keepalive_thread = threading.Thread(target=keepalive_loop, daemon=True)
    keepalive_thread.start()

    print(f"[{args.underlying}] Ensuring {args.spot_symbol} is active in the live ingestor watchlist...")
    try:
        api.upsert_watchlist(args.spot_symbol, priority=100)
    except requests.exceptions.HTTPError as ex:
        if ex.response is not None and ex.response.status_code == 500:
            pass # Suppress harmless 500 error
        else:
            print(f"[{args.underlying}] WARN: Could not upsert spot symbol {args.spot_symbol}: {ex}")
    except Exception as ex:
        print(f"[{args.underlying}] WARN: Could not upsert spot symbol {args.spot_symbol}: {ex}")

    print(f"[{args.underlying}] Executing Phase 1: Strategy Warmup...")
    try:
        from core.data_engine import DataEngine
        engine = DataEngine()
        
        reqs = strategy.get_data_requirements()
        for req in reqs:
            if req.symbol_type == "index":
                print(f"[{args.underlying}] Fetching historical warmup data ({req.resolution}m) for {args.spot_symbol}...")
                
                from datetime import datetime, timedelta
                end_time = datetime.now()
                start_time = end_time - timedelta(days=15)
                
                bars = engine.get_historical_bars(
                    symbol=args.spot_symbol,
                    resolution=req.resolution,
                    start_date=start_time.strftime("%Y-%m-%d"),
                    end_date=end_time.strftime("%Y-%m-%d")
                )
                
                if bars:
                    # Take the last 500 for warmup
                    warmup_bars = bars[-500:] if len(bars) > 500 else bars
                    print(f"[{args.underlying}] Feeding {len(warmup_bars)} bars into strategy warmup...")
                    
                    cumulative_frames = []
                    for b in warmup_bars:
                        # Map BarData to BarFrame
                        frame = BarFrame(
                            symbol=b.symbol,
                            resolution=b.resolution,
                            timestamp_utc=b.timestamp_start.isoformat().replace("+00:00", "Z"),
                            open=b.open,
                            high=b.high,
                            low=b.low,
                            close=b.close,
                            volume=b.volume
                        )
                        cumulative_frames.append(frame)
                        
                        inp = StrategyInput(
                            mode="LivePaper",
                            timestamp_utc=frame.timestamp_utc,
                            underlying=args.underlying,
                            spot_price=frame.close,
                            atm_strike=round_to_step(frame.close, strike_step),
                            contracts={},
                            bars={req.resolution: {"index": list(cumulative_frames)}},
                            metadata={"source": "warmup"}
                        )
                        strategy.on_bar(state, inp)
                        
        print(f"[{args.underlying}] Warmup complete. State initialized.")
    except Exception as ex:
        print(f"[{args.underlying}] WARN: Warmup failed: {ex}")

    print(f"[{args.underlying}] Listening for live ticks on Redis Stream...")
    subscriber = build_subscriber_from_env()

    ticks_processed = 0
    last_status_print = 0.0
    last_tick_at: Optional[float] = None
    last_spot_price: Optional[float] = None
    last_atm_strike: Any = None
    last_contract_count = 0

    def print_status_if_due() -> None:
        """
        One [STATUS] line every 10 s regardless of whether ticks arrive, so the
        UI can tell an idle runner (market closed, feed stopped) from a wedged
        one. The trigger levels are Ghost-specific and shown only when present.
        """
        # Module-level names (this block runs under __main__, not inside a function).
        global last_status_print
        now_ts = time.time()
        if now_ts - last_status_print <= 10:
            return
        last_status_print = now_ts

        if last_spot_price is None:
            status = (
                f"[STATUS] {args.underlying} waiting for ticks on {args.spot_symbol} "
                f"(no tick received yet) ticks={ticks_processed}"
            )
        else:
            age = f"{now_ts - last_tick_at:.0f}s ago" if last_tick_at is not None else "unknown"
            status = (
                f"[STATUS] {args.underlying} spot={last_spot_price:.2f} atm={last_atm_strike} "
                f"open_groups={count_open_groups(state)} ticks={ticks_processed} "
                f"contracts={last_contract_count} last_tick={age}"
            )
        if isinstance(state, dict) and ("target_buy_trigger" in state or "target_sell_trigger" in state):
            buy_t = state.get("target_buy_trigger")
            sell_t = state.get("target_sell_trigger")
            buy_str = f"{buy_t:.2f}" if buy_t else "waiting for pivot"
            sell_str = f"{sell_t:.2f}" if sell_t else "waiting for pivot"
            status += f" | triggers: BUY CE (up) {buy_str}, BUY PE (down) {sell_str}"
        print(status, flush=True)

    try:
        for tick in subscriber.listen_for_ticks(block_ms=1000, yield_idle=True):
            if tick is None:
                # Empty read: nothing on the stream for block_ms.
                print_status_if_due()
                continue

            try:
                if tick.get("symbol") != args.spot_symbol:
                    print_status_if_due()
                    continue

                spot_price = float(tick.get("lastTradedPrice", 0))
                if spot_price <= 0:
                    print_status_if_due()
                    continue

                timestamp_utc = tick.get("exchangeTimestampUtc") or tick.get("receivedUtc") or datetime.now(timezone.utc).isoformat()
                # ATM strike on the underlying's real strike grid (from the option chain)
                atm_strike = round_to_step(spot_price, strike_step)

                if DEBUG_PRINT_MESSAGES:
                    print(f"INPUT PRICE: {spot_price} | ATM: {atm_strike}")

                # Record TICK_PROCESSED metric
                TICK_PROCESSED.inc()
                ticks_processed += 1
                last_tick_at = time.time()
                last_spot_price = spot_price
                last_atm_strike = atm_strike

                # Record REDIS_LAG metric
                try:
                    dt_format = "%Y-%m-%dT%H:%M:%S.%fZ" if "." in timestamp_utc else "%Y-%m-%dT%H:%M:%SZ"
                    dt_obj = datetime.strptime(timestamp_utc, dt_format).replace(tzinfo=timezone.utc)
                    lag = time.time() - dt_obj.timestamp()
                    if lag >= 0:
                        REDIS_LAG.set(lag)
                except Exception as e:
                    pass


                atm_ce = api.get_exact_contract(
                    underlying=args.underlying,
                    expiry=expiry_date,
                    strike=atm_strike,
                    option_type="CE"
                )
                atm_pe = api.get_exact_contract(
                    underlying=args.underlying,
                    expiry=expiry_date,
                    strike=atm_strike,
                    option_type="PE"
                )

                contracts = {}
                if atm_ce:
                    contracts["atm_ce"] = map_contract(atm_ce)
                if atm_pe:
                    contracts["atm_pe"] = map_contract(atm_pe)
                last_contract_count = len(contracts)

                # Make sure live ingestor will start tracking these contracts
                ensure_contracts_tracked(api, contracts)

                try:
                    bars_dict: Dict[str, Dict[str, List[BarFrame]]] = {}
                    
                    reqs = strategy.get_data_requirements()
                    
                    for req in reqs:
                        res = req.resolution
                        sym_type = req.symbol_type
                        
                        if res not in bars_dict:
                            bars_dict[res] = {}
                            
                        # 1. Fetch for index
                        if sym_type == "index":
                            raw_idx = api.get_recent_bars(args.spot_symbol, resolution=res, take=500)
                            if raw_idx:
                                bars_dict[res]["index"] = [BarFrame(
                                    symbol=b.get("symbol", args.spot_symbol),
                                    resolution=b.get("resolution", res),
                                    timestamp_utc=str(b.get("barStartUtc", "")),
                                    open=float(b.get("open", 0.0)),
                                    high=float(b.get("high", 0.0)),
                                    low=float(b.get("low", 0.0)),
                                    close=float(b.get("close", 0.0)),
                                    volume=float(b.get("volumeDelta", 0.0))
                                ) for b in reversed(raw_idx)]

                        # 2. Fetch for atm_ce
                        elif sym_type == "atm_ce" and atm_ce:
                            raw_ce = api.get_recent_bars(atm_ce["symbol"], resolution=res, take=500)
                            if raw_ce:
                                bars_dict[res]["atm_ce"] = [BarFrame(
                                    symbol=b.get("symbol", atm_ce["symbol"]),
                                    resolution=b.get("resolution", res),
                                    timestamp_utc=str(b.get("barStartUtc", "")),
                                    open=float(b.get("open", 0.0)),
                                    high=float(b.get("high", 0.0)),
                                    low=float(b.get("low", 0.0)),
                                    close=float(b.get("close", 0.0)),
                                    volume=float(b.get("volumeDelta", 0.0))
                                ) for b in reversed(raw_ce)]

                        # 3. Fetch for atm_pe
                        elif sym_type == "atm_pe" and atm_pe:
                            raw_pe = api.get_recent_bars(atm_pe["symbol"], resolution=res, take=500)
                            if raw_pe:
                                bars_dict[res]["atm_pe"] = [BarFrame(
                                    symbol=b.get("symbol", atm_pe["symbol"]),
                                    resolution=b.get("resolution", res),
                                    timestamp_utc=str(b.get("barStartUtc", "")),
                                    open=float(b.get("open", 0.0)),
                                    high=float(b.get("high", 0.0)),
                                    low=float(b.get("low", 0.0)),
                                    close=float(b.get("close", 0.0)),
                                    volume=float(b.get("volumeDelta", 0.0))
                                ) for b in reversed(raw_pe)]
                        
                        
                except Exception as ex:
                    print(f"WARN: Failed to fetch recent bars: {ex}")
                    bars_dict = {}

                inp = StrategyInput(
                    mode="LivePaper",
                    timestamp_utc=timestamp_utc,
                    underlying=args.underlying,
                    spot_price=spot_price,
                    atm_strike=atm_strike,
                    contracts=contracts,
                    bars=bars_dict,
                    metadata={"source": "live-api", "tick": tick},
                )

                # Record STRATEGY_LOOP_DURATION metric
                t_start = time.time()
                signals = strategy.on_bar(state, inp)
                t_end = time.time()
                STRATEGY_LOOP_DURATION.observe(t_end - t_start)

                print_status_if_due()

                print_signals(signals)
                if DEBUG_PRINT_MESSAGES:
                    print_strategy_state(state)

                for sig in signals:
                    if args.strategy == "GhostTangentCrossings" and sig.signal_type in {"BUY", "SELL"}:
                        try:
                            print(f"Converting {sig.signal_type} to PAPER OPEN_GROUP Signal...")
                            direction = sig.signal_type
                            target_contract = atm_ce if direction == "BUY" else atm_pe
                            if target_contract and "symbol" in target_contract:
                                exec_symbol = target_contract["symbol"]
                                print(f"Selected Option Symbol for Paper: {exec_symbol}")

                                # Morph the signal into OPEN_GROUP for the Simulator
                                sig.signal_type = "OPEN_GROUP"
                                sig.metadata["group_id"] = f"GTC_{int(time.time())}"
                                sig.metadata["direction"] = direction
                                sig.legs = [{"symbol": exec_symbol, "side": "BUY", "quantity": strategy_lots}]

                            else:
                                print(f"ERROR: Could not resolve contract for {direction}.")
                        except Exception as ex:
                            print(f"ERROR during paper conversion: {ex}")
                            import traceback
                            traceback.print_exc()

                    if sig.signal_type in {"OPEN_GROUP", "CLOSE_GROUP"}:
                        sig = enrich_signal_leg_prices(api, sig, expiry_date)
                        stamp_signal_metadata(sig, inp)

                        print("ENRICHED SIGNAL LEGS:")
                        print(json.dumps(sig.legs, indent=2, default=str))

                        # Always push signal to UI dynamically via Strategy endpoint
                        try:
                            api.http.post(f"{api.base_url}/api/Strategy/{args.strategy_id}/signals", json={
                                "timestamp_utc": datetime.now(timezone.utc).isoformat(),
                                "signal_type": sig.signal_type,
                                "reason": sig.reason,
                                "legs": sig.legs,
                                "metadata": sig.metadata
                            })
                        except Exception as e:
                            print(f"WARN: Could not publish live signal to UI: {e}")

                        if run_id:
                            payload = signal_to_request(run_id, sig)
                            result = api.create_simulation_signal(payload)
                            ORDERS_EMITTED.inc()

                            print("PERSISTED SIGNAL:")
                            print(json.dumps(result, indent=2, default=str))
                        else:
                            ORDERS_EMITTED.inc()
                            print("LIVE ORDER EMITTED (No Run ID)")

                    # Persist state after loop iteration
                    loaded_state.strategy_data = state
                    state_store.save(loaded_state)

            except Exception as ex:
                import traceback
                print("ERROR PROCESSING TICK:", ex)
                traceback.print_exc()

    finally:
        keepalive_running = False
        try:
            state_store.release_lock(owner_id)
            print("[STATE] Released strategy lock gracefully.", flush=True)
        except Exception as ex:
            print(f"[STATE] WARN: could not release strategy lock: {ex}", flush=True)
