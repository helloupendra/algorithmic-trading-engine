from __future__ import annotations

import json
import time
import argparse
import sys
import os
import threading
import redis
import requests
import urllib3
from typing import List, Dict, Any, Optional

# Add the parent directory to sys.path so that absolute-style imports work
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from messaging.redis_subscriber import build_subscriber_from_env
from strategies.base_strategy import StrategyInput, StrategySignal, OptionContract
from strategies.titli.titli_standard import TitliStrategy
from strategies.titli.titli_2_straddle_20 import Titli2Straddle20Strategy
from strategies.titli.titli_3_straddle_175 import Titli3Straddle175Strategy
from strategies.titli.titli_multi import TitliMultiStraddleStrategy
from strategies.titli.titli_qty_adj import TitliQtyAdjustmentStrategy

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
from core.config import API_BASE_URL
VERIFY_SSL = False

UNDERLYING = "BANKNIFTY"
SPOT_SYMBOL = "NSE:NIFTYBANK-INDEX"
POLL_SECONDS = 1


def build_redis_client() -> redis.Redis:
    return redis.Redis(
        host=os.getenv("REDIS_HOST", "localhost"),
        port=int(os.getenv("REDIS_PORT", "6379")),
        db=int(os.getenv("REDIS_DB", "0")),
        password=os.getenv("REDIS_PASSWORD") or None,
        decode_responses=True,
        socket_timeout=5,
    )

class PlatformApiClient:
    def __init__(self, base_url: str, verify_ssl: bool = False):
        self.base_url = base_url.rstrip("/")
        self.verify_ssl = verify_ssl
        self.http = requests.Session()

    def get_latest_quote(self, symbol: str) -> Dict[str, Any]:
        resp = self.http.get(
            f"{self.base_url}/api/LiveData/latest",
            params={"symbol": symbol},
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def get_recent_bars(self, symbol: str, resolution: str = "1m", take: int = 1) -> List[Dict[str, Any]]:
        resp = self.http.get(
            f"{self.base_url}/api/LiveData/bars",
            params={"symbol": symbol, "resolution": resolution, "take": take},
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def upsert_watchlist(self, symbol: str, priority: int = 50) -> Dict[str, Any]:
        payload = {
            "symbol": symbol,
            "dataType": "symbolUpdate",
            "isActive": True,
            "priority": priority
        }

        resp = self.http.post(
            f"{self.base_url}/api/LiveData/watchlist",
            json=payload,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def get_expiries(self, underlying: str) -> List[Dict[str, Any]]:
        resp = self.http.get(
            f"{self.base_url}/api/Instruments/derivatives/expiries",
            params={"underlying": underlying},
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def get_exact_contract(
        self,
        underlying: str,
        expiry: str,
        strike: int,
        option_type: str,
    ) -> Dict[str, Any]:
        resp = self.http.get(
            f"{self.base_url}/api/Instruments/derivatives/contract",
            params={
                "underlying": underlying,
                "expiry": expiry,
                "strike": strike,
                "optionType": option_type,
            },
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def create_simulation_signal(self, payload: Dict[str, Any]) -> Dict[str, Any]:
        resp = self.http.post(
            f"{self.base_url}/api/Simulator/signals",
            json=payload,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def create_simulation_run(self, payload: Dict[str, Any]) -> Dict[str, Any]:
        resp = self.http.post(
            f"{self.base_url}/api/Simulator/runs",
            json=payload,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()


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
    print(json.dumps({
        "last_trade_strike": state.get("last_trade_strike"),
        "ce_list": state.get("ce_list"),
        "pe_list": state.get("pe_list"),
        "straddle_list": state.get("straddle_list"),
        "signal_count": state.get("signal_count"),
        "current_group_id": state.get("current_group_id"),
        "current_group_legs": state.get("current_group_legs"),
    }, indent=2, default=str))
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
    parser = argparse.ArgumentParser(description="Run a specific Titli Strategy.")
    parser.add_argument("--strategy", type=str, required=True, help="The strategy name to run (e.g., Titli, Titli2Straddle20, TitliMulti50).")
    parser.add_argument("--user-id", type=int, required=True, help="The User ID to associate with this simulation run.")
    parser.add_argument("--run-id", type=int, required=False, help="Optional: The SimulationRunId generated from the C# Backend API. If not provided, a new run will be created automatically.")
    args = parser.parse_args()

    api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)

    # Dictionary to pick strategy
    strategies_map = {
        "Titli": lambda: TitliStrategy(params={"strike_step": 100}),
        "Titli2Straddle20": lambda: Titli2Straddle20Strategy(params={"adjustment_threshold": 20}),
        "Titli3Straddle175": lambda: Titli3Straddle175Strategy(),
        "TitliMulti50": lambda: TitliMultiStraddleStrategy(params={"adjustment_threshold": 50, "minor_threshold": 10}),
        "TitliMulti70": lambda: TitliMultiStraddleStrategy(params={"adjustment_threshold": 70, "minor_threshold": 10}),
        "TitliMulti90": lambda: TitliMultiStraddleStrategy(params={"adjustment_threshold": 90, "minor_threshold": 10}),
        "TitliQtyAdjustment": lambda: TitliQtyAdjustmentStrategy(params={"adjustment_threshold": 70, "minor_threshold": 10}),
    }

    if args.strategy not in strategies_map:
        print(f"ERROR: Strategy '{args.strategy}' not found. Available strategies: {list(strategies_map.keys())}")
        sys.exit(1)

    strategy = strategies_map[args.strategy]()
    state = strategy.initialize_state()

    expiries = api.get_expiries(UNDERLYING)
    if not expiries:
        raise RuntimeError(f"No expiries found for {UNDERLYING}")

    expiry_date = str(expiries[0]["expiryDate"])
    print(f"Using expiry: {expiry_date}")

    run_id = args.run_id
    if run_id is None:
        print(f"No --run-id provided. Automatically creating a new LivePaper simulation run for {args.strategy}...")
        run_payload = {
            "mode": "LivePaper",
            "symbol": SPOT_SYMBOL,
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

    print("Starting continuous LIVE PAPER runner...")
    print(f"Using underlying source symbol: {SPOT_SYMBOL}")
    print(f"UserId: {args.user_id}")
    print(f"SimulationRunId: {run_id}")
    print(f"Strategy: {args.strategy}")

    print("Starting Prometheus metrics server on port 8000...")
    start_metrics_server(8000)

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
            exchange="NSE",
            underlying=UNDERLYING,
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

    print(f"Ensuring {SPOT_SYMBOL} is active in the live ingestor watchlist...")
    try:
        api.upsert_watchlist(SPOT_SYMBOL, priority=100)
    except Exception as ex:
        print(f"WARN: Could not upsert spot symbol {SPOT_SYMBOL}: {ex}")

    print("Listening for live ticks on Redis Stream...")
    subscriber = build_subscriber_from_env()

    try:
        for tick in subscriber.listen_for_ticks(block_ms=1000):
            try:
                if tick.get("symbol") != SPOT_SYMBOL:
                    continue

                spot_price = float(tick.get("lastTradedPrice", 0))
                if spot_price <= 0:
                    continue
                    
                timestamp_utc = tick.get("exchangeTimestampUtc") or tick.get("receivedUtc") or datetime.now(timezone.utc).isoformat()
                atm_strike = round_to_100(spot_price)

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
                    underlying=UNDERLYING,
                    expiry=expiry_date,
                    strike=atm_strike,
                    option_type="CE"
                )
                atm_pe = api.get_exact_contract(
                    underlying=UNDERLYING,
                    expiry=expiry_date,
                    strike=atm_strike,
                    option_type="PE"
                )

                contracts = {
                    "atm_ce": map_contract(atm_ce),
                    "atm_pe": map_contract(atm_pe),
                }

                # Make sure live ingestor will start tracking these contracts
                ensure_contracts_tracked(api, contracts)

                inp = StrategyInput(
                    mode="LivePaper",
                    timestamp_utc=timestamp_utc,
                    underlying=UNDERLYING,
                    spot_price=spot_price,
                    atm_strike=atm_strike,
                    contracts=contracts,
                    bars_by_symbol={},
                    metadata={"source": "live-api"},
                )

                # Record STRATEGY_LOOP_DURATION metric
                t_start = time.time()
                signals = strategy.on_bar(state, inp)
                t_end = time.time()
                STRATEGY_LOOP_DURATION.observe(t_end - t_start)

                print_signals(signals)
                print_strategy_state(state)

                for sig in signals:
                    if sig.signal_type in {"OPEN_GROUP", "CLOSE_GROUP"}:
                        sig = enrich_signal_leg_prices(api, sig, expiry_date)

                        print("ENRICHED SIGNAL LEGS:")
                        print(json.dumps(sig.legs, indent=2, default=str))

                        payload = signal_to_request(run_id, sig)
                        result = api.create_simulation_signal(payload)
                        ORDERS_EMITTED.inc()

                        print("PERSISTED SIGNAL:")
                        print(json.dumps(result, indent=2, default=str))

                    # Persist state after loop iteration
                    loaded_state.strategy_data = state
                    state_store.save(loaded_state)

            except Exception as ex:
                print("ERROR PROCESSING TICK:", ex)

    finally:
        keepalive_running = False
        state_store.release_lock(owner_id)
        print("[STATE] Released strategy lock gracefully.")
