from __future__ import annotations

import json
import time
import argparse
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
import pkgutil
import inspect
import importlib
import strategies
from strategies.base_strategy import StrategyInput, StrategySignal, OptionContract, BaseStrategy, BarFrame

import core.fyers_orders as fyers_orders

try:
    # pyrefly: ignore [missing-import]
    from strategies.private_strategies import get_private_strategies
except ImportError:
    def get_private_strategies(): return {}

def discover_strategies() -> Dict[str, Any]:
    discovered = {}
    for info in pkgutil.walk_packages(strategies.__path__, strategies.__name__ + "."):
        try:
            module = importlib.import_module(info.name)
            for name, obj in inspect.getmembers(module, inspect.isclass):
                if issubclass(obj, BaseStrategy) and obj is not BaseStrategy:
                    if obj.__module__ == info.name:
                        strategy_name = getattr(obj, "name", obj.__name__)
                        discovered[strategy_name] = lambda params=None, cls=obj: cls(params or {})
        except Exception:
            pass
    return discovered


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
                    strike = int(parts[2])
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
            if run_params:
                print(f"Loaded {len(run_params)} parameter(s) from run {args.run_id}: {sorted(run_params.keys())}")
        except Exception as ex:
            print(f"WARNING: could not load parameters for run {args.run_id}: {ex}. Using strategy defaults.")

    strategy = strategies_map[args.strategy](run_params)
    state = strategy.initialize_state()

    expiries = api.get_expiries(args.underlying)
    if not expiries:
        print(f"[{args.underlying}] WARNING: No option expiries found for {args.underlying} in the database. Ensure NSE_FO/BSE_FO data is loaded.")
        print(f"[{args.underlying}] Shutting down runner for {args.underlying}.")
        sys.exit(0)

    today_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
    valid_expiries = [x for x in expiries if str(x["expiryDate"]) >= today_str]
    
    if not valid_expiries:
        raise RuntimeError(f"No future expiries found for {args.underlying}")

    expiry_date = str(valid_expiries[0]["expiryDate"])
    print(f"Using expiry: {expiry_date}")

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
                            atm_strike=int(round(frame.close / 100) * 100),
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

    try:
        for tick in subscriber.listen_for_ticks(block_ms=1000):
            try:
                if tick.get("symbol") != args.spot_symbol:
                    continue

                spot_price = float(tick.get("lastTradedPrice", 0))
                if spot_price <= 0:
                    continue
                    
                timestamp_utc = tick.get("exchangeTimestampUtc") or tick.get("receivedUtc") or datetime.now(timezone.utc).isoformat()
                # Calculate dynamic ATM strike based on underlying
                interval = 50 if args.underlying == "NIFTY" else 100
                atm_strike = int(round(spot_price / interval) * interval)

                if DEBUG_PRINT_MESSAGES:
                    print(f"INPUT PRICE: {spot_price} | ATM: {atm_strike}")
            
                # Record TICK_PROCESSED metric
                TICK_PROCESSED.inc()

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
                
                # Print live status to terminal every 10 seconds for user visibility
                now_ts = time.time()
                if now_ts - state.get("last_status_print", 0) > 10:
                    buy_t = state.get('target_buy_trigger')
                    sell_t = state.get('target_sell_trigger')
                    buy_str = f"{buy_t:.2f}" if buy_t else "Waiting for pivot"
                    sell_str = f"{sell_t:.2f}" if sell_t else "Waiting for pivot"
                    print(f"[{args.underlying} TICK] Spot: {spot_price:.2f} | Triggers -> BUY CE (Up): {buy_str} | BUY PE (Down): {sell_str}")
                    state["last_status_print"] = now_ts

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
                                sig.legs = [{"symbol": exec_symbol, "side": "BUY", "quantity": 15}]
                                
                            else:
                                print(f"ERROR: Could not resolve contract for {direction}.")
                        except Exception as ex:
                            print(f"ERROR during paper conversion: {ex}")
                            import traceback
                            traceback.print_exc()

                    if sig.signal_type in {"OPEN_GROUP", "CLOSE_GROUP"}:
                        sig = enrich_signal_leg_prices(api, sig, expiry_date)

                        print("ENRICHED SIGNAL LEGS:")
                        print(json.dumps(sig.legs, indent=2, default=str))

                        # Always push signal to UI dynamically via Strategy endpoint
                        try:
                            api.http.post(f"{api.base_url}/api/Strategy/{args.strategy_id}/signals", json={
                                "timestamp_utc": datetime.now(timezone.utc).isoformat(),
                                "signal_type": sig.signal_type,
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
        state_store.release_lock(owner_id)
        print("[STATE] Released strategy lock gracefully.")
