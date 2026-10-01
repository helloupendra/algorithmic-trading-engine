"""
strategies/execution_runner.py

Live paper-trading runner for one strategy. Launched by the API as

    execution_runner.py --strategy NAME --strategy-id ID --user-id UID
                        --run-id RID --underlying U --spot-symbol S

It loads the run's parameters (lots, stop_loss, target, underlying, strategy
params), warms the strategy up on historical index bars, then consumes live
ticks from the Redis stream, resolves the contracts the strategy declared in
`get_contract_requirements` (ATM/OTM/ITM, at the distances the run's parameters
ask for), hands them to the strategy and posts every OPEN_GROUP/CLOSE_GROUP
signal to the Simulator (paper fills) and to the Strategy feed (UI).
Stop-loss/target are enforced by the API's risk guard, not here; the runner
only logs them.
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

# First of all, while this is the only thread: give way on the CPU to the live
# feed every runner reads (RUNNER_NICE, default 10). On Linux a thread keeps
# the nice value it started with, so this must come before anything that
# starts threads — the safe stdio tee, redis, requests, numpy's pools.
if __name__ == "__main__":
    from core.process_priority import lower_priority_from_env
    lower_priority_from_env()

# Before anything prints: the API that spawned us may die (restart, crash);
# then stdout is a closed pipe and a plain print() raises BrokenPipeError.
# Every line also goes to logs/engine/runner-<run_id>-<pid>.log from the first
# one, stamped with its time and stream: the API reads a run's console from
# that file, and a runner it adopted after a restart has no pipes at all. The
# log ends with an "EXIT code=… reason=…" line saying how the runner ended.
# Only when run as the runner: importing this module must not take over the
# importer's streams and sys.exit.
from core.safe_output import install_exit_line, install_safe_stdio, log_name_for_run, note_exit
if __name__ == "__main__":
    install_safe_stdio(name=log_name_for_run("runner", sys.argv), tee=True)
    install_exit_line()

import threading
import redis
import requests
from core.api_client import build_session, PlatformApiClient
from core.feed_watchdog import assess_feed
from core.tick_age import tick_age_seconds
from core.leg_pricing import DEFAULT_WAIT_SECONDS, resolve_leg_prices
from core.warmup_source import load_warmup_bars
import urllib3
from typing import Callable, List, Dict, Any, Optional, Tuple

from messaging.redis_subscriber import build_subscriber_from_env
from strategies.base_strategy import (
    StrategyInput,
    StrategySignal,
    ContractRequirement,
    OptionContract,
    BaseStrategy,
    BarFrame,
)
from strategies.registry import discover_strategies
from strategies import signal_filters
# Shared helpers live in importable modules (also used by the backtest engine);
# they are re-exported here so existing `execution_runner.<name>` references work.
from strategies.contract_selector import (  # noqa: F401
    DEFAULT_STRIKE_STEP,
    FALLBACK_STRIKE_STEPS,
    ExactContractCache,
    contracts_for_requirements,
    describe_requirement,
    fallback_strike_step,
    first_expiry_on_or_after,
    format_strike,
    map_contract,
    round_to_step,
    strike_for_requirement,
    strike_step_from_chain,
)
from strategies.signal_utils import (  # noqa: F401
    UiSignalPublisher,
    count_open_groups,
    parse_optional_number,
    signal_to_request,
    signal_to_ui_payload,
    stamp_signal_metadata,
)
from strategies.signal_booking import (
    OPEN_REFUSAL_COOLDOWN_SECONDS as DEFAULT_OPEN_REFUSAL_COOLDOWN_SECONDS,
    Booking,
    ClosedGroups,
    SignalBooker,
    report as report_booking,
    run_tick,
)
from backtest.run_spec import parse_risk_rules

import core.fyers_orders as fyers_orders
from core.recap_session import RecapSession

try:
    # pyrefly: ignore [missing-import]
    from strategies.variants import get_parameterised_strategies
except ImportError:
    def get_parameterised_strategies(): return {}


from state_management.state_models import StrategyState
from state_management.state_store import StrategyStateStore
from state_management.runner_state import save_strategy_state, starting_state

from core.metrics import (
    AUTO_METRICS_PORT_RANGE,
    start_metrics_server,
    start_metrics_server_auto,
    REDIS_LAG,
    ORDERS_EMITTED,
    SIGNALS_FILTERED,
    STRATEGY_LOOP_DURATION,
    TICK_PROCESSED,
    TICK_ERRORS,
)
from datetime import datetime, timezone

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL, DEBUG_PRINT_MESSAGES
VERIFY_SSL = False

# Total wait for ALL legs of one signal. It is time the strategy spends blind,
# so it is bounded well under the feed watchdog's 90s stall threshold.
SIGNAL_PRICE_WAIT_SECONDS = float(os.getenv("SIGNAL_PRICE_WAIT_SECONDS", DEFAULT_WAIT_SECONDS))

# After the API refuses an OPEN_GROUP, how long the run's OPENs are held without
# being posted (strategies/signal_booking.py). Closes are never held.
OPEN_REFUSAL_COOLDOWN_SECONDS = float(os.getenv("OPEN_REFUSAL_COOLDOWN_SECONDS",
                                                DEFAULT_OPEN_REFUSAL_COOLDOWN_SECONDS))


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


def resolve_strike_step(api: PlatformApiClient, underlying: str, expiry_date: str,
                        listed_fallback: bool = False) -> float:
    """
    Strike step derived from the option chain, else the per-underlying fallback.

    `listed_fallback` is for a recap run. The chain endpoint lists enabled
    contracts only, so a replay of a day whose expiry has since gone reads an
    empty chain for it. The step is then taken from the contracts listed now
    (the F&O inventory derives it from the next listed expiry by the same
    rule), and only after that from the table of known steps.
    """
    try:
        chain = api.get_option_chain(underlying, expiry_date)
        step = strike_step_from_chain(chain)
        if step:
            print(f"[{underlying}] Strike step {format_strike(step)} derived from {len(chain)} contracts of expiry {expiry_date}")
            return step
        if not listed_fallback:
            print(f"[{underlying}] WARN: option chain for {expiry_date} has too few strikes; using fallback step")
    except Exception as ex:
        print(f"[{underlying}] WARN: could not read option chain for strike step: {ex}")
    if listed_fallback:
        step = listed_strike_step(api, underlying)
        if step:
            print(f"[{underlying}] Strike step {format_strike(step)} from the contracts listed now: expiry "
                  f"{expiry_date} is not in the chain (it has expired since the replayed day)")
            return step
    step = fallback_strike_step(underlying)
    print(f"[{underlying}] Using fallback strike step {format_strike(step)}")
    return step


def listed_strike_step(api: PlatformApiClient, underlying: str) -> Optional[float]:
    """The underlying's strike step as the F&O inventory reports it, or None."""
    key = (underlying or "").strip().upper()
    try:
        for row in api.get_fno_underlyings():
            if (row.get("underlying") or "").strip().upper() == key:
                step = float(row.get("strikeStep") or 0)
                return round(step, 6) if step > 0 else None
    except Exception as ex:
        print(f"[{underlying}] WARN: could not read the strike step from the F&O inventory: {ex}")
    return None


def choose_expiry(api: PlatformApiClient, underlying: str, recap: Optional[RecapSession],
                  today: Optional[str] = None) -> Tuple[List[Dict[str, Any]], Optional[str]]:
    """
    The underlying's option expiries, and the one this run trades.

    Live: the listed expiries, and the first on or after today's (UTC) date —
    exactly what the runner always did.

    Recap: the expiry that was nearest on the replayed day D, which has often
    expired since. Expired contracts stay in the master only as disabled rows,
    so the list includes history (those rows and the exchange calendar), and
    the choice is the first expiry on or after D, never today's: a replay of
    the 15th that traded today's weekly would be pricing contracts the 15th
    never saw. None when no expiry on or after D is known; the caller stops
    the run rather than guess one.
    """
    if recap is None:
        expiries = api.get_expiries(underlying)
        day = today or datetime.now(timezone.utc).strftime("%Y-%m-%d")
    else:
        expiries = api.get_expiries(underlying, include_history=True)
        day = recap.day.isoformat()
    return expiries, first_expiry_on_or_after(expiries, day)


def recent_bars(api: PlatformApiClient, symbol: str, resolution: str, recap: Optional[RecapSession],
                stamp: Any, take: int = 500) -> List[Dict[str, Any]]:
    """
    A tick's recent bars, newest first.

    A recap run asks for the bars up to its tick's exchange stamp (`untilUtc`):
    the API's newest bars are from after the replayed day, and a strategy shown
    them would be trading with the close already known. The API bounds them
    now, including the minute the replay is still building; `drop_future_bars`
    stays as the safety net it always was, so an API that ignored the bound
    would show the strategy nothing rather than the future.
    """
    if recap is None:
        return api.get_recent_bars(symbol, resolution=resolution, take=take)
    rows = api.get_recent_bars(symbol, resolution=resolution, take=take, until_utc=stamp)
    return recap.drop_future_bars(rows, stamp)


def resolve_lot_size(api: PlatformApiClient, underlying: str) -> Optional[int]:
    """
    The underlying's contract multiplier, from the instrument master.

    Leg quantities are LOTS; the platform multiplies by this when it fills. The
    strategy does not need it to trade — it is passed through so a strategy can
    reason about position size (notional, per-lot risk) without inventing a
    number for the one underlying its author happened to test on.
    """
    key = (underlying or "").strip().upper()
    try:
        for row in api.get_fno_underlyings():
            if (row.get("underlying") or "").strip().upper() == key:
                lot = row.get("lotSize")
                if lot and int(lot) > 0:
                    return int(lot)
    except Exception as ex:
        print(f"[{underlying}] WARN: could not read the lot size from the master: {ex}")
    return None


def install_signal_handlers() -> None:
    """SIGTERM/SIGINT raise SystemExit so the `finally` block releases the Redis lock."""
    def _handler(signum: int, _frame: Any) -> None:
        try:
            name = signal.Signals(signum).name
        except ValueError:
            name = str(signum)
        print(f"[RUNNER] stopping: {name}", flush=True)
        note_exit(0, f"signal {name}")
        raise SystemExit(0)

    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            signal.signal(sig, _handler)
        except (ValueError, OSError):
            # Not the main thread / unsupported platform: keep the default behaviour.
            pass


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


def resolve_contract_requirements(strategy: Any, params: Optional[Dict[str, Any]]) -> List[ContractRequirement]:
    """
    The contracts the strategy wants, or the ATM CE/PE default when the class
    does not declare any. A broken override must not take the run down: it is
    logged and the default is used.
    """
    try:
        requirements = list(strategy.get_contract_requirements(params or {}) or [])
    except Exception as ex:
        print(f"[CONTRACT] WARN: get_contract_requirements failed ({ex}); falling back to ATM CE/PE", flush=True)
        requirements = []
    if not requirements:
        requirements = list(BaseStrategy.get_contract_requirements(params or {}))
    return requirements


def bar_frames_from_rows(rows: Optional[List[Dict[str, Any]]], symbol: str, resolution: str) -> List[BarFrame]:
    """Recent-bar rows (newest first, as the API returns them) -> oldest-first BarFrames."""
    return [BarFrame(
        symbol=row.get("symbol", symbol),
        resolution=row.get("resolution", resolution),
        timestamp_utc=str(row.get("barStartUtc", "")),
        open=float(row.get("open", 0.0)),
        high=float(row.get("high", 0.0)),
        low=float(row.get("low", 0.0)),
        close=float(row.get("close", 0.0)),
        volume=float(row.get("volumeDelta", 0.0)),
    ) for row in reversed(rows or [])]


def bars_symbol_for(symbol_type: str, contracts: Dict[str, OptionContract], spot_symbol: str) -> Optional[str]:
    """
    The symbol a DataRequirement names: the index, one of the resolved
    contract keys, or an exact broker symbol. None when the key exists in the
    strategy's requirements but the master could not resolve it this tick.
    """
    kind = str(symbol_type or "")
    if kind == "index":
        return spot_symbol
    contract = contracts.get(kind)
    if contract is not None:
        return contract.symbol
    return kind if ":" in kind else None


#: Symbols this runner has already asked the ingestor to track.
#:
#: Without it `ensure_contracts_tracked` re-POSTed every resolved contract on
#: EVERY tick — a blocking HTTP round trip per contract per tick, to say a thing
#: the API already knew. At a few ticks a second with six contracts that is
#: thousands of pointless requests a minute, all of them inside the loop the
#: strategy is trying to react in.
_tracked_symbols: set = set()


def ensure_contracts_tracked(api: PlatformApiClient, contracts: Dict[str, OptionContract]) -> None:
    """
    Make sure every resolved contract (ATM, OTM and ITM alike) is present in
    the live watchlist, so the ingestor can subscribe and populate latest quotes.

    Each symbol is announced once. The watchlist is a set on the far side, so
    saying it again changes nothing — and an ATM roll still announces the new
    strikes immediately, because those symbols have not been seen before.
    """
    pending = {k: c for k, c in contracts.items() if c.symbol not in _tracked_symbols}
    if not pending:
        return

    for _, contract in pending.items():
        try:
            api.upsert_watchlist(contract.symbol, priority=80)
            _tracked_symbols.add(contract.symbol)
        except requests.exceptions.HTTPError as ex:
            if ex.response is not None and ex.response.status_code == 500:
                # Harmless for paper trades, and the symbol IS tracked.
                _tracked_symbols.add(contract.symbol)
            else:
                # Not recorded, so the next tick tries again.
                print(f"WARN: failed to ensure watchlist for {contract.symbol}: {ex}")
        except Exception as ex:
            print(f"WARN: failed to ensure watchlist for {contract.symbol}: {ex}")


# --- Market Replay ----------------------------------------------------------
# A replayed day goes out on the same Redis stream as the live feed, marked
# "isReplay": true. Each runner keeps its own kind: a live run never trades a
# price from another day, and a recap run never trades tonight's (MCX, or a
# feed someone left running).

def is_replay_tick(tick: Dict[str, Any]) -> bool:
    """True for a tick the Market Replay player published."""
    flag = tick.get("isReplay") if isinstance(tick, dict) else None
    if isinstance(flag, str):
        return flag.strip().lower() in ("true", "1")
    return flag is True or flag == 1


def use_recap_quotes(api: PlatformApiClient, recap_date: str) -> None:
    """
    A recap run's quotes: from the API's ReplayBook (replay=true), never
    live_quotes_latest, and only while the desk replays this run's day
    (recapDate). A vendor's evening recap (TrueData) replays today, and its
    runs read the live table its feed writes, whatever day the desk replays.
    """
    api.replay = True
    api.replay_date = recap_date


def tick_is_for_this_run(tick: Dict[str, Any], recap_run: bool) -> bool:
    """A recap run takes only replayed ticks; every other run takes only live ones."""
    return is_replay_tick(tick) == bool(recap_run)


#: Set by a recap runner when its tick loop starts; the replay player waits for
#: every run of its session to set one (at most five minutes) before it plays.
RECAP_LISTENING_KEY = "recap:listening:{run_id}"
#: Longer than any replay of one day at 1x; it only has to outlive the session.
RECAP_LISTENING_TTL_SECONDS = 6 * 60 * 60


def announce_recap_listening(redis_client: Any, run_id: int, now: Optional[datetime] = None) -> bool:
    """
    Tell the replay player this run is reading the stream. Never fatal: a
    runner that cannot say so still trades whatever arrives, and the player
    gives up waiting after five minutes and plays anyway.
    """
    key = RECAP_LISTENING_KEY.format(run_id=run_id)
    stamp = (now or datetime.now(timezone.utc)).astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    try:
        redis_client.set(key, stamp, ex=RECAP_LISTENING_TTL_SECONDS)
        return True
    except Exception as ex:
        print(f"[RECAP] WARN: could not set {key} for the replay player: {ex}", flush=True)
        return False


def forget_recap_listening(redis_client: Any, run_id: int) -> None:
    """A runner that has stopped is no longer listening; the key must not say otherwise."""
    try:
        redis_client.delete(RECAP_LISTENING_KEY.format(run_id=run_id))
    except Exception:
        pass


def resolve_leg_symbol(api: PlatformApiClient, symbol: str, expiry_date: str,
                       include_history: bool = False) -> str:
    """
    A strategy's logical symbol ("BANKNIFTY_PE_50300") as the broker's real one.

    Anything that is already a broker symbol is returned untouched, and a lookup
    that fails returns the input rather than raising: the leg then goes on with
    a symbol nothing can price, and the API refuses the group — which is the
    honest outcome, and a louder one than a runner that dies mid-signal.

    `include_history` (a recap run) also finds a contract that has expired
    since the replayed day.
    """
    if not symbol or "_" not in symbol or "NSE:" in symbol:
        return symbol

    parts = symbol.split("_")
    if len(parts) != 3:
        return symbol

    try:
        underlying, option_type = parts[0], parts[1]
        # Fractional strikes (102.5) are legitimate on stock grids.
        strike_value = float(parts[2])
        strike = int(strike_value) if strike_value.is_integer() else strike_value
        if include_history:
            exact = api.get_exact_contract(underlying, expiry_date, strike, option_type, include_history=True)
        else:
            exact = api.get_exact_contract(underlying, expiry_date, strike, option_type)
        if exact and "symbol" in exact:
            return exact["symbol"]
    except Exception as ex:
        print(f"WARN: Could not resolve exact contract for logical symbol {symbol}: {ex}")

    return symbol


def enrich_signal_leg_prices(
    api: PlatformApiClient,
    sig: StrategySignal,
    expiry_date: str,
    on_poll: Optional[Callable[[], None]] = None,
    recap_run: bool = False,
) -> StrategySignal:
    """
    Give every leg a live price, subscribing them all before waiting for any.

    This runs inside the tick loop, so its cost is time the strategy spends blind
    to the market. It used to poll ONE symbol at a time for up to 35 seconds
    each: a four-leg roll could block for over two minutes, a sixteen-leg one for
    nine — and each leg's wait only began after the one before it had given up,
    so the last leg was subscribed minutes after the first.

    Now every leg is resolved and subscribed first, and then a single budget
    covers all of them together against one bulk quote call per round. A closing
    signal waits not at all: its symbols have been subscribed since the position
    was opened, the API has a documented fallback for closing legs, and getting
    flat must never queue behind an entry.

    A leg that stays unpriced is sent unpriced. Inventing a number here would
    defeat the API's refusal of unpriced opening groups, which is the thing
    standing between a missed trade and a fabricated one.

    A recap run (`recap_run`) subscribes nothing: its prices come from the
    replay, which plays every symbol the desk recorded that day, and the live
    watchlist is not the replay's to change. Its quotes come from the replay
    too (the client is in replay mode), and its logical symbols resolve to
    contracts that may have expired since.
    """
    legs = []
    for leg in sig.legs:
        legs.append({
            "symbol": resolve_leg_symbol(api, leg.get("symbol", ""), expiry_date, include_history=recap_run),
            "side": leg.get("side", ""),
            "quantity": int(leg.get("quantity", 0)),
            "price": leg.get("price"),
        })

    # Subscribe everything before waiting for anything.
    if not recap_run:
        for symbol in {x["symbol"] for x in legs if x["symbol"]}:
            try:
                api.upsert_watchlist(symbol)
            except Exception:
                # Already tracked, or the API refused it — either way the wait below
                # is what decides whether a price actually turns up.
                pass

    closing = (sig.signal_type or "").upper() == "CLOSE_GROUP"
    budget = 0.0 if closing else SIGNAL_PRICE_WAIT_SECONDS

    def fetch_quotes():
        return {row["symbol"]: row for row in api.get_all_latest_quotes() if row.get("symbol")}

    missing = resolve_leg_prices(
        legs,
        fetch_quotes=fetch_quotes,
        now=time.time,
        sleep=time.sleep,
        budget_seconds=budget,
        on_poll=on_poll,
    )

    if missing:
        print(f"WARN: no live price for {', '.join(missing)} after {budget:.0f}s; "
              f"sending the {sig.signal_type} unpriced — the API decides.", flush=True)

    sig.legs = legs
    return sig


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Run a specific strategy.")
    parser.add_argument("--strategy", type=str, required=True, help="Strategy name to run")
    parser.add_argument("--strategy-id", type=int, required=True, help="Strategy ID to run")
    parser.add_argument("--user-id", type=int, required=True, help="User ID running the strategy")
    parser.add_argument("--run-id", type=int, required=False, help="Optional: The SimulationRunId generated from the C# Backend API. If not provided, a new run will be created automatically.")
    parser.add_argument("--underlying", type=str, default="BANKNIFTY", help="The underlying instrument symbol (e.g., BANKNIFTY, NIFTY50, SENSEX).")
    parser.add_argument("--spot-symbol", type=str, default="NSE:NIFTYBANK-INDEX", help="The exact Fyers spot symbol for the underlying.")
    parser.add_argument(
        "--metrics-port", type=int, default=0,
        help=(
            "The port for the Prometheus metrics server. 0 (default) = auto: the first free port in "
            f"{AUTO_METRICS_PORT_RANGE[0]}..{AUTO_METRICS_PORT_RANGE[1]}, so several runners can share a host."
        ),
    )
    args = parser.parse_args()

    if args.run_id is not None:
        install_safe_stdio(name=f"runner-{args.run_id}")
    install_signal_handlers()

    api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)

    # Dynamically discover all BaseStrategy subclasses in the strategies folder
    strategies_map = discover_strategies()
    
    # Explicit overrides and parameterised instances from variants.py.
    strategies_map.update(get_parameterised_strategies())

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

    # A recap run trades a replay of a day the platform already holds (the
    # Market Replay player's ticks, marked isReplay). The clock is
    # RecapSession's: warm-up stops the day before, bars later than the
    # replay's clock are never shown, and the run ends at the replayed 15:30.
    # Everything dated is read as of the replayed day too: the expiry, the
    # contracts (expired ones included) and the quotes, which come from the
    # replay. It takes only replayed ticks and leaves the live watchlist alone.
    # An ordinary live run has recap = None and takes none of those branches;
    # the one thing it does differently is pass over replayed ticks.
    recap = RecapSession.from_params(run_params)
    # The replayed day, handed to the strategy on every input; None on a live run.
    recap_date = recap.day.isoformat() if recap is not None else None
    if recap is not None:
        # Quotes from the API's ReplayBook (replay=true), never live_quotes_latest:
        # the replay's prices and the live desk's must not mix in either direction.
        use_recap_quotes(api, recap_date)
        print(f"[{args.underlying}] RECAP RUN — replaying {recap_date}; the strategy sees only "
              f"prices up to the replay's clock and stops at its 15:30. Quotes come from the replay, "
              f"contracts and the expiry as of {recap_date}, and the live watchlist is left alone.", flush=True)

    strategy = strategies_map[args.strategy](run_params)
    state = strategy.initialize_state()

    run_lots = BaseStrategy.lots_from(run_params, getattr(strategy, "default_lots", 1))
    strategy_lots = getattr(strategy, "lots", None)
    if not isinstance(strategy_lots, int) or strategy_lots < 1:
        strategy_lots = run_lots
    # Risk rules (leg / group / overall) are read for the log only: the API's
    # risk guard enforces them and they can be edited while the run is live.
    run_risk = parse_risk_rules(run_params)
    run_stop_loss, run_target = run_risk.stop_loss, run_risk.target

    print(
        f"[CONFIG] strategy={args.strategy} run_id={args.run_id} underlying={args.underlying} "
        f"spot_symbol={args.spot_symbol} lots={strategy_lots} "
        f"stop_loss={run_stop_loss if run_stop_loss is not None else 'none'} "
        f"target={run_target if run_target is not None else 'none'} "
        f"risk={json.dumps(run_risk.to_dict(), separators=(',', ':'))} "
        f"[{run_risk.describe()}] "
        f"(risk rules enforced by the API risk guard: leg → group → overall)",
        flush=True,
    )

    # Live: the first listed expiry on or after today. Recap: the first on or
    # after the replayed day, expired ones included (see choose_expiry).
    expiries, expiry_date = choose_expiry(api, args.underlying, recap)
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

    if expiry_date is None:
        if recap is None:
            raise RuntimeError(f"No future expiries found for {args.underlying}")
        # Nothing the platform knows expired on or after the replayed day: no
        # listed contract, no expired row in the master, nothing in the exchange
        # calendar. The run cannot know which contracts that day traded, and a
        # later expiry would be a guess priced from another week, so it stops
        # here with the reason, as a live run with no contracts does.
        message = (
            f"No {args.underlying} option expiry on or after the replayed day {recap_date} is known "
            f"(listed, expired or in the exchange calendar); the recap run cannot choose its contracts."
        )
        print(f"[{args.underlying}] ERROR: {message}", flush=True)
        print(message, file=sys.stderr, flush=True)
        sys.exit(2)

    print(f"Using expiry: {expiry_date}" + (f" (as of the replayed day {recap_date})" if recap is not None else ""))

    strike_step = resolve_strike_step(api, args.underlying, expiry_date, listed_fallback=recap is not None)
    lot_size = resolve_lot_size(api, args.underlying)
    print(f"[{args.underlying}] Lot size {lot_size if lot_size else 'unknown'} "
          f"(leg quantities are lots; the platform multiplies by this).")

    # The contracts this strategy wants on every tick, at the distances the
    # run's parameters ask for. Resolved once: the keys never change during a
    # run, only the strikes they land on as the underlying moves.
    # Market-context rules for THIS run, applied to whatever the strategy
    # emits. Configured per run, so the same strategy can be tried with and
    # without them and the difference measured.
    filter_config = signal_filters.parse_filters(run_params)
    if filter_config is not None:
        print(f"[CONFIG] signal filters active: {filter_config}", flush=True)

    contract_requirements = resolve_contract_requirements(strategy, run_params)
    contract_cache = ExactContractCache(api, args.underlying, log=lambda line: print(line, flush=True),
                                        include_history=recap is not None)
    missing_contracts_logged: set = set()
    print(
        f"[CONFIG] contracts (strike step {format_strike(strike_step)}, expiry {expiry_date}): "
        + "; ".join(describe_requirement(req, strike_step, run_params) for req in contract_requirements),
        flush=True,
    )

    def log_missing_contract(key: str, strike: Any, option_type: str) -> None:
        """One line per (key, strike): a moving underlying must not spam the log."""
        marker = (key, strike)
        if marker in missing_contracts_logged:
            return
        missing_contracts_logged.add(marker)
        print(
            f"[CONTRACT] missing {key}: no {option_type} {format_strike(float(strike))} contract for "
            f"expiry {expiry_date} in the instrument master; the strategy runs without it",
            flush=True,
        )

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
        install_safe_stdio(name=f"runner-{run_id}")

    # Tell the API which OS process runs this run, so a restarted API can
    # re-adopt (and still stop) the runner. Best effort: an older API answers
    # 404, and a failure here must never take the strategy down.
    runner_started_utc = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    try:
        api.register_runner(run_id, os.getpid(), runner_started_utc)
        print(f"[RUNNER] registered pid {os.getpid()} for run {run_id}", flush=True)
    except Exception as ex:
        print(f"[RUNNER] WARN: could not register pid {os.getpid()} for run {run_id}: {ex}", flush=True)

    last_seen_updated_utc = None

    print(f"[{args.underlying}] Starting continuous LIVE PAPER runner...")
    print(f"[{args.underlying}] Using underlying source symbol: {args.spot_symbol}")
    print(f"[{args.underlying}] UserId: {args.user_id}")
    print(f"[{args.underlying}] SimulationRunId: {run_id}")
    print(f"[{args.underlying}] Strategy: {args.strategy}")

    # Metrics are optional: a port clash (several runners on one host) or a
    # sandboxed socket must never take the strategy down with it.
    metrics_port: Optional[int] = None
    try:
        if args.metrics_port and args.metrics_port > 0:
            print(f"[{args.underlying}] Starting Prometheus metrics server on port {args.metrics_port}...")
            metrics_port = start_metrics_server(args.metrics_port)
        else:
            print(
                f"[{args.underlying}] Starting Prometheus metrics server on the first free port in "
                f"{AUTO_METRICS_PORT_RANGE[0]}..{AUTO_METRICS_PORT_RANGE[1]}..."
            )
            metrics_port = start_metrics_server_auto()
        print(f"[{args.underlying}] Prometheus metrics served on port {metrics_port}", flush=True)
    except Exception as e:
        wanted = args.metrics_port if args.metrics_port and args.metrics_port > 0 else "auto"
        print(
            f"[{args.underlying}] Failed to start metrics server (port {wanted}): {e}. "
            f"Continuing without metrics.",
            flush=True,
        )

    # Dashboard copy of every signal goes to the run-scoped feed; the
    # publisher falls back to the strategy-scoped route on an older API.
    ui_signals = UiSignalPublisher(api.http, api.base_url, run_id, args.strategy_id)

    redis_client = build_redis_client()
    owner_id = f"strategy-runner-{run_id}-{os.getpid()}"
    state_store = StrategyStateStore(redis_client, run_id)

    if not state_store.try_acquire_lock(owner_id, ttl_ms=30000):
        print(f"ERROR: Another strategy runner is already active for run_id={run_id}")
        sys.exit(1)

    # A stored state is recovered only when it can be read back as it was
    # saved; otherwise the run starts fresh and warms up. A fresh state is not
    # saved until warm-up has run: saved before it, it would be recovered as a
    # state that skips warm-up and never had it (state_management/runner_state.py).
    start = starting_state(state_store, strategy, lambda: StrategyState(
        simulation_run_id=run_id,
        strategy_name=args.strategy,
        mode="LivePaper",
        exchange=args.spot_symbol.split(":")[0] if ":" in args.spot_symbol else "NSE",
        underlying=args.underlying,
    ))
    loaded_state, state, recovered_state = start.record, start.state, start.recovered
    print(f"[STATE] {start.note}", flush=True)

    def save_state() -> None:
        """Never fatal: a state that could not be saved costs a recovery, not the run."""
        try:
            save_strategy_state(state_store, loaded_state, strategy, state)
        except Exception as ex:
            print(f"[STATE] WARN: could not save the strategy state: {ex}", flush=True)

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

    if recap is not None:
        # The replay plays the spot whatever the watchlist says, and re-activating
        # a symbol on the live watchlist is not a replay's decision to make.
        print(f"[{args.underlying}] Recap run: the live watchlist is left as it is.")
    else:
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

    # Warmup replays historical bars through on_bar to rebuild whatever the
    # strategy derives from them. On a FRESH run that is exactly right.
    #
    # On a recovered one it is destructive: the state already holds everything
    # those bars produced, and feeding them again pushes counters forward a
    # second time. Ghost advances bar_index per distinct bar, so a mid-session
    # restart could hand the strategy an index hundreds of bars beyond reality
    # and leave it waiting for a trigger the rest of the day — silently, because
    # a strategy that never fires looks exactly like a quiet market.
    #
    # Note this is the ONLY safe place to fix it. Saving state on every tick
    # instead would make every restart recover a large index, turning a rare
    # failure into a certain one.
    if recovered_state:
        print(f"[{args.underlying}] Skipping warmup: state was recovered and already "
              f"reflects those bars. Replaying them would double-count.", flush=True)
    else:
        print(f"[{args.underlying}] Executing Phase 1: Strategy Warmup...")
    try:
        from core.data_engine import DataEngine
        engine = DataEngine()
        
        # Nothing to replay onto a state that already contains it.
        reqs = [] if recovered_state else strategy.get_data_requirements()
        for req in reqs:
            if req.symbol_type == "index":
                print(f"[{args.underlying}] Fetching historical warmup data ({req.resolution}m) for {args.spot_symbol}...")
                
                from datetime import datetime, timedelta
                end_time = datetime.now()
                if recap is not None:
                    # Warm-up up to today would feed this morning's real close
                    # into the state before the replay reaches the open.
                    end_time = datetime.fromisoformat(recap.warmup_end_date())
                start_time = end_time - timedelta(days=15)

                warmup = load_warmup_bars(
                    api=api,
                    make_engine=DataEngine,
                    symbol=args.spot_symbol,
                    resolution=req.resolution,
                    start_date=start_time.strftime("%Y-%m-%d"),
                    end_date=end_time.strftime("%Y-%m-%d"),
                    label=args.underlying,
                )
                bars = warmup.bars
                print(f"[{args.underlying}] Warmup source: {warmup.source}"
                      + (f" — {warmup.detail}" if warmup.detail else ""), flush=True)
                
                if bars and recap is not None:
                    bars = [b for b in bars if recap.before_session(b.timestamp_start)]
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
                            strike_step=strike_step,
                            lot_size=lot_size,
                            contracts={},
                            bars={req.resolution: {"index": list(cumulative_frames)}},
                            metadata={"source": "warmup"},
                            recap_date=recap_date,
                        )
                        strategy.on_bar(state, inp)
                        
        if not recovered_state:
            print(f"[{args.underlying}] Warmup complete. State initialized.")
            # Saved here, once, so a state recovered after a restart is always
            # one that had its warm-up.
            save_state()
    except Exception as ex:
        print(f"[{args.underlying}] WARN: Warmup failed: {ex}")

    print(f"[{args.underlying}] Listening for live ticks on Redis Stream...")
    # Only the spot drives the loop, so only the spot is decoded: every other
    # contract on the stream is passed over on its symbol field.
    subscriber = build_subscriber_from_env(symbols={args.spot_symbol})
    niceness = os.nice(0) if hasattr(os, "nice") else None
    print(f"[{args.underlying}] Reading "
          + (f"only {args.spot_symbol}" if subscriber.symbols else "every symbol")
          + " from the stream"
          + (f", in {subscriber.min_read_interval * 1000:.0f} ms batches" if subscriber.min_read_interval else "")
          + (f", at nice {niceness}" if niceness is not None else "")
          + " (RUNNER_STREAM_FILTER, RUNNER_STREAM_BATCH_MS, RUNNER_NICE).", flush=True)

    ticks_processed = 0
    # Ticks the loop raised on and skipped. Each prints a traceback, but a
    # strategy failing on every tick looked, on the [STATUS] line, exactly like
    # one that was simply quiet.
    tick_errors = 0
    last_status_print = 0.0
    last_tick_at: Optional[float] = None
    last_spot_price: Optional[float] = None
    last_atm_strike: Any = None
    last_contract_count = 0

    # --- feed watchdog -----------------------------------------------------
    # The runner watches its own input, because nothing upstream can: a Redis
    # stream that stops carrying ticks looks exactly like a market with nothing
    # to say. The decision itself lives in core/feed_watchdog.py, where it can
    # be tested; this only supplies the clock, market hours and the reporting.
    FEED_CHECK_SECONDS = 15

    listen_started_at = time.time()
    feed_stalled = False
    last_feed_check = 0.0
    last_stall_report = 0.0
    market_open_cache: dict[str, Any] = {"value": None, "checked_at": 0.0}

    def market_is_open() -> Optional[bool]:
        """Cached for a minute; None means the question could not be asked."""
        now_ts = time.time()
        if market_open_cache["value"] is not None and now_ts - market_open_cache["checked_at"] < 60:
            return market_open_cache["value"]
        answer = api.is_market_open()
        if answer is not None:
            market_open_cache["value"] = answer
            market_open_cache["checked_at"] = now_ts
        return answer

    def report_feed(is_stalled: bool, silent_for: int) -> None:
        """Never fatal: losing the warning must not take the strategy with it."""
        if args.run_id is None:
            return
        try:
            api.report_feed_health(args.run_id, is_stalled, silent_for, args.underlying)
        except Exception as ex:
            print(f"[{args.underlying}] WARN: could not report feed health: {ex}", flush=True)

    def check_feed_if_due() -> None:
        global last_feed_check, feed_stalled, last_stall_report

        now_ts = time.time()
        if now_ts - last_feed_check < FEED_CHECK_SECONDS:
            return
        last_feed_check = now_ts

        verdict = assess_feed(
            now=now_ts,
            last_tick_at=last_tick_at,
            listening_since=listen_started_at,
            currently_stalled=feed_stalled,
            last_report_at=last_stall_report,
            market_open=market_is_open(),
        )

        if not verdict.should_report:
            return

        feed_stalled = verdict.is_stalled
        last_stall_report = now_ts

        if verdict.action == "recovered":
            print(f"[{args.underlying}] FEED RECOVERED after {verdict.silent_seconds}s.", flush=True)
        elif verdict.action == "stalled":
            print(f"[{args.underlying}] FEED STALLED — no ticks for {verdict.silent_seconds}s "
                  "while the market is open.", flush=True)
        else:
            print(f"[{args.underlying}] FEED STILL STALLED — {verdict.silent_seconds}s.", flush=True)

        report_feed(verdict.is_stalled, verdict.silent_seconds)

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
        if tick_errors:
            status += f" tick_errors={tick_errors}"
        # A structure reader says how far it has read, so one that has stopped
        # reading shows here instead of as a quiet day (SMC, 22–25 Sep).
        if isinstance(state, dict) and "structure_candles" in state:
            status += f" | structure: {state.get('structure_candles')} candles, last {state.get('structure_last') or 'none'}"
        if isinstance(state, dict) and ("target_buy_trigger" in state or "target_sell_trigger" in state):
            buy_t = state.get("target_buy_trigger")
            sell_t = state.get("target_sell_trigger")
            buy_str = f"{buy_t:.2f}" if buy_t else "waiting for pivot"
            sell_str = f"{sell_t:.2f}" if sell_t else "waiting for pivot"
            status += f" | triggers: BUY CE (up) {buy_str}, BUY PE (down) {sell_str}"
        print(status, flush=True)

    def housekeeping() -> None:
        """
        The per-loop chores, on every path through the tick loop — idle reads
        and processed ticks alike. The watchdog has to see both: an idle read is
        how a stall looks, and a processed tick is how recovery does.
        """
        print_status_if_due()
        check_feed_if_due()

    # Posts each signal with retries and a clientSignalId the API books once,
    # and holds the run's OPENs for a while after one is refused.
    booker = (SignalBooker(api, run_id, on_wait=housekeeping, open_cooldown_seconds=OPEN_REFUSAL_COOLDOWN_SECONDS)
              if run_id else None)
    # Closes already booked: the same close emitted again (a roll whose OPEN
    # was refused puts the strategy back) is not enriched, posted or published.
    closed_groups = ClosedGroups()

    def evaluate_tick(tick_state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        """The strategy's on_bar for one tick, timed and printed."""
        t_start = time.time()
        emitted = strategy.on_bar(tick_state, inp)
        STRATEGY_LOOP_DURATION.observe(time.time() - t_start)

        housekeeping()

        print_signals(emitted)
        if DEBUG_PRINT_MESSAGES:
            print_strategy_state(tick_state)
        return emitted

    def post_signal(sig: StrategySignal, inp: StrategyInput, atm_ce_contract: Optional[OptionContract],
                    atm_pe_contract: Optional[OptionContract]) -> Optional[Booking]:
        """
        One signal of a tick to the paper book. Returns the Booking, or None
        for a signal that is not for the book (not a group signal, or held back
        by the run's filters).
        """
        if args.strategy == "GhostTangentCrossings" and sig.signal_type in {"BUY", "SELL"}:
            try:
                print(f"Converting {sig.signal_type} to PAPER OPEN_GROUP Signal...")
                direction = sig.signal_type
                # Ghost always trades the ATM leg of the direction it called.
                target_contract = atm_ce_contract if direction == "BUY" else atm_pe_contract
                if target_contract is not None:
                    exec_symbol = target_contract.symbol
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

        if sig.signal_type not in {"OPEN_GROUP", "CLOSE_GROUP"}:
            return None

        # Keyed on the legs as the strategy emitted them: enrichment below
        # rewrites them with resolved symbols and prices.
        legs_key = ClosedGroups.legs_key(sig)
        if booker is not None:
            repeat = closed_groups.repeat_of(sig, legs_key)
            if repeat is not None:
                group = repeat.group_id
                if closed_groups.repeats(group) == 1:
                    print(f"CLOSE_GROUP {group} was booked already; the strategy emits it again (its state was put "
                          f"back after a refused signal). The same close is not posted again.", flush=True)
                return repeat

        # The live loop is always handed a bar that is still
        # forming, so the gate judges the one before it.
        verdict = signal_filters.evaluate(
            filter_config, sig, inp, newest_bar_is_forming=True)
        if verdict.blocked:
            print(f"[FILTER] {sig.signal_type} blocked by {verdict.blocked_by}: "
                  f"{verdict.reason} | {verdict.details}", flush=True)
            SIGNALS_FILTERED.inc()
            return None

        sig = enrich_signal_leg_prices(api, sig, expiry_date, on_poll=housekeeping, recap_run=recap is not None)
        stamp_signal_metadata(sig, inp)

        print("ENRICHED SIGNAL LEGS:")
        print(json.dumps(sig.legs, indent=2, default=str))

        booking = booker.book(sig) if booker is not None else None
        if booking is not None and not booking.booked:
            return booking
        if booking is not None:
            closed_groups.booked(sig, legs_key)

        ORDERS_EMITTED.inc()
        if booking is None:
            print("LIVE ORDER EMITTED (No Run ID)")
        else:
            print("PERSISTED SIGNAL:")
            print(json.dumps(booking.result, indent=2, default=str))

        # The dashboard copy goes out once the book has the signal: one the
        # strategy is made to emit again after a refusal would otherwise show
        # on the card once per attempt.
        try:
            ui_signals.publish(signal_to_ui_payload(sig, datetime.now(timezone.utc).isoformat()))
        except Exception as e:
            print(f"WARN: Could not publish live signal to UI: {e}")
        return booking

    # Ticks a recap run refused because they fell outside the replayed session.
    recap_refused = [0]
    # Set once the replay has played to 15:30; the strategy is fed nothing after.
    recap_closed = [False]
    # Ticks of the other kind passed over: replayed ones on a live run, live
    # ones on a recap run (tick_is_for_this_run).
    other_session_ticks = [0]

    if recap is not None:
        # Read from the stream's newest entry as of now, then say so: the
        # player starts the replay as soon as every run of its session is
        # listening, and its first ticks must not land before the first read.
        if not subscriber.start_from_now():
            print(f"[{args.underlying}] RECAP WARN: could not pin the stream position; reading from "
                  f"the first read on.", flush=True)
        if announce_recap_listening(redis_client, run_id):
            print(f"[{args.underlying}] RECAP listening: set {RECAP_LISTENING_KEY.format(run_id=run_id)} "
                  f"for the replay player.", flush=True)

    try:
        for tick in subscriber.listen_for_ticks(block_ms=1000, yield_idle=True):
            if tick is None:
                # Empty read: nothing on the stream for block_ms.
                housekeeping()
                continue

            try:
                # The replay shares the live stream. A live run must never trade
                # a replayed price, nor a recap run a live one.
                if not tick_is_for_this_run(tick, recap is not None):
                    other_session_ticks[0] += 1
                    if other_session_ticks[0] in (1, 1000, 100000):
                        kind = "live" if recap is not None else "replayed"
                        print(f"[{args.underlying}] Passing over {kind} ticks: this is a "
                              f"{'recap' if recap is not None else 'live'} run "
                              f"({other_session_ticks[0]} so far).", flush=True)
                    housekeeping()
                    continue

                if tick.get("symbol") != args.spot_symbol:
                    housekeeping()
                    continue

                spot_price = float(tick.get("lastTradedPrice", 0))
                if spot_price <= 0:
                    housekeeping()
                    continue

                timestamp_utc = tick.get("exchangeTimestampUtc") or tick.get("receivedUtc") or datetime.now(timezone.utc).isoformat()

                if recap is not None:
                    recap_stamp = tick.get("exchangeTimestampUtc")
                    if recap_closed[0]:
                        housekeeping()
                        continue
                    if recap.reached_close(recap_stamp):
                        # The runner does not stop the run itself: it is not
                        # allowed to (403), and exiting would leave the square-off
                        # to whatever handles a vanished runner. It stops trading,
                        # and says what to do. The latest quotes are the replay's
                        # closing prices and stay so until Monday's feed.
                        recap_closed[0] = True
                        print(f"[{args.underlying}] RECAP reached the replayed 15:30 — the strategy takes no "
                              f"further entries or exits. Stop run {args.run_id} from the console to square "
                              f"off at the replay's closing prices (a Market Replay stops its runs itself "
                              f"when it ends).", flush=True)
                        housekeeping()
                        continue
                    if not recap.in_session(recap_stamp):
                        # Pre-open, or a stamp that is not the replayed session's
                        # clock at all. Refused rather than fed through: the one
                        # way this mode could quietly go wrong is by trusting a
                        # tick it cannot place in the day.
                        recap_refused[0] += 1
                        if recap_refused[0] in (1, 100, 1000):
                            print(f"[{args.underlying}] RECAP ignoring a tick outside the replayed session "
                                  f"({tick.get('exchangeTimestampUtc')}); {recap_refused[0]} so far.", flush=True)
                        housekeeping()
                        continue
                    recap.note_in_session(recap_stamp)
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

                # How far behind the feed is running. The old inline parse
                # expected a literal "Z" and every real tick spells the offset
                # out as "+00:00", so it raised on every tick into a bare
                # `except: pass` and this gauge was never once set.
                lag = tick_age_seconds(timestamp_utc, time.time())
                if lag is not None:
                    REDIS_LAG.set(lag)
                    if recap is None and lag > 30 and ticks_processed % 200 == 0:
                        print(f"[{args.underlying}] WARN: ticks are {lag:.0f}s behind the exchange.",
                              flush=True)


                # Every contract the strategy declared (ATM, OTM, ITM), resolved
                # on the underlying's real strike grid. A key the master lacks is
                # simply absent — the strategy decides what to do without it.
                contracts = contracts_for_requirements(
                    contract_cache,
                    contract_requirements,
                    expiry_date,
                    atm_strike,
                    strike_step,
                    run_params,
                    on_missing=log_missing_contract,
                )
                last_contract_count = len(contracts)
                atm_ce_contract = contracts.get("atm_ce")
                atm_pe_contract = contracts.get("atm_pe")

                # Make sure the live ingestor will start tracking every one of them.
                # Not on a recap run: the replay plays what the desk recorded that
                # day, and the live watchlist is not the replay's to change.
                if recap is None:
                    ensure_contracts_tracked(api, contracts)

                try:
                    bars_dict: Dict[str, Dict[str, List[BarFrame]]] = {}

                    for req in strategy.get_data_requirements():
                        res = req.resolution
                        sym_type = req.symbol_type
                        bars_dict.setdefault(res, {})

                        symbol = bars_symbol_for(sym_type, contracts, args.spot_symbol)
                        if not symbol:
                            continue
                        rows = recent_bars(api, symbol, res, recap, timestamp_utc)
                        if rows:
                            bars_dict[res][sym_type] = bar_frames_from_rows(rows, symbol, res)

                except Exception as ex:
                    print(f"WARN: Failed to fetch recent bars: {ex}")
                    bars_dict = {}

                inp = StrategyInput(
                    mode="LivePaper",
                    timestamp_utc=timestamp_utc,
                    underlying=args.underlying,
                    spot_price=spot_price,
                    atm_strike=atm_strike,
                    strike_step=strike_step,
                    lot_size=lot_size,
                    contracts=contracts,
                    bars=bars_dict,
                    # The expiry the run trades, so the expiry-day filters judge the
                    # same way here as they do in a replay.
                    metadata={"source": "live-api", "tick": tick, "expiry_date": expiry_date},
                    recap_date=recap_date,
                )

                # Every signal of the tick is booked, or the strategy's state
                # goes back to what it was before on_bar and it emits them again
                # on a later tick (strategies/signal_booking.py).
                tick_outcome = run_tick(
                    state,
                    lambda tick_state: evaluate_tick(tick_state, inp),
                    lambda sig: post_signal(sig, inp, atm_ce_contract, atm_pe_contract),
                )
                state = tick_outcome.state
                report_booking(tick_outcome, log=lambda line: print(line, flush=True))

                # Saved when the tick's signals are in the book; a state put
                # back is the one the tick began with.
                if tick_outcome.signals and not tick_outcome.restored:
                    save_state()

            except Exception as ex:
                import traceback
                tick_errors += 1
                TICK_ERRORS.inc()
                print(f"ERROR PROCESSING TICK ({tick_errors} so far):", ex)
                traceback.print_exc()

    finally:
        keepalive_running = False
        if recap is not None:
            forget_recap_listening(redis_client, run_id)
        try:
            # The runner exits only when its run stops (the API stops the run
            # when the runner exits), so nothing will read these keys again.
            state_store.forget(owner_id)
            print("[STATE] Cleared the run's strategy state, heartbeat and lock.", flush=True)
        except Exception as ex:
            print(f"[STATE] WARN: could not clear the run's state from Redis: {ex}", flush=True)
