import json
import time
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
from strategies.contract_selector import fallback_strike_step, strike_step_from_chain
from core.api_client import PlatformApiClient
from core.config import API_BASE_URL

# The exchange day these rules reason about; bar timestamps arrive in UTC.
IST = timezone(timedelta(hours=5, minutes=30))

# Note: Telegram alerts are dispatched centrally by the C# API (AlertSubscriberService)
# listening on the Redis channel "alerts:new", instead of being sent directly from Python.

class LogicEngine(BaseStrategy):
    """
    Alert-only rule engine: watches the index against its 15-minute range, heavyweight stocks 
    (HDFC Bank, Reliance) for divergence and the order book for selling pressure, and sends 
    alert events to Redis.
    """
    name = "LogicEngine"
    description = (
        "Alert-only rule engine: watches the index against its 15-minute range, heavyweight stocks "
        "(HDFC Bank, Reliance) for divergence and the order book for selling pressure, and sends Telegram "
        "alerts instead of placing orders. It never opens a paper position."
    )
    category = "Alerts"
    legs_summary = "No legs (Telegram alerts only)"
    default_lots = 1
    default_params: Dict[str, Any] = {
        # Minimum gap between two alerts of this engine, in seconds.
        "cooldown_seconds": 300,
        # Rule 3: how much bigger the ask side must be than the bid side.
        "ask_bid_ratio": 3.0,
        # Rule 2: strikes either side of the ATM to scan for the highest open interest,
        # and how many strikes that peak must move before it counts as a shift.
        "strike_window": 5,
        "oi_shift_steps": 1,
        # Rule 1 (index): the heavyweights whose day low confirms a bear trap.
        "heavyweights": ["NSE:HDFCBANK-EQ", "NSE:RELIANCE-EQ"],
    }
    listed = False

    def __init__(self, params: Dict[str, Any] = None):
        super().__init__()
        self.params = params or {}
        self.lots = self.lots_from(self.params, self.default_lots)

        self.cooldown_seconds = self.params.get("cooldown_seconds", self.default_params["cooldown_seconds"])
        self.ask_bid_ratio = self.params.get("ask_bid_ratio", self.default_params["ask_bid_ratio"])
        self.strike_window = int(self.params.get("strike_window", self.default_params["strike_window"]))
        self.oi_shift_steps = int(self.params.get("oi_shift_steps", self.default_params["oi_shift_steps"]))

        # Chain facts resolved once per underlying, and the "this data is not in
        # the feed" notices, printed once each instead of on every bar.
        self._chain_cache: Dict[str, tuple] = {}
        self._warned: set = set()

        self.api = PlatformApiClient(API_BASE_URL, verify_ssl=False)
        
        import redis
        import os
        self.redis_client = redis.Redis(
            host=os.getenv("REDIS_HOST", "localhost"),
            port=int(os.getenv("REDIS_PORT", "6379")),
            db=int(os.getenv("REDIS_DB", "0")),
            password=os.getenv("REDIS_PASSWORD") or None,
            decode_responses=True
        )
        
        self.heavyweights = list(self.params.get("heavyweights", self.default_params["heavyweights"]))

        import threading
        self._cmd_thread = threading.Thread(target=self._listen_for_commands, daemon=True)
        self._cmd_thread.start()

    def _listen_for_commands(self):
        """Background thread that listens to Redis Pub/Sub for E2E Trigger Commands."""
        import redis
        import os
        
        redis_client = redis.Redis(
            host=os.getenv("REDIS_HOST", "localhost"),
            port=int(os.getenv("REDIS_PORT", "6379")),
            db=int(os.getenv("REDIS_DB", "0")),
            password=os.getenv("REDIS_PASSWORD") or None,
            decode_responses=True
        )
        
        pubsub = redis_client.pubsub()
        pubsub.subscribe("cmd:python_engine")
        print("LogicEngine: Listening for E2E commands on cmd:python_engine...")
        
        for message in pubsub.listen():
            if message["type"] == "message":
                try:
                    data = json.loads(message["data"])
                    if data.get("command") == "TEST_E2E_ALERT":
                        instrument = data.get("instrument")
                        if instrument:
                            self._execute_e2e_test(instrument, redis_client)
                except Exception as e:
                    print(f"Error processing E2E command: {e}")

    def _execute_e2e_test(self, instrument: str, redis_client):
        """Executes the E2E Alert Test logic."""
        print(f"Executing E2E Test for {instrument}...")
        from datetime import datetime, timezone
        
        try:
            mapping = {
                "SENSEX": "BSE:SENSEX-INDEX",
                "NIFTY50": "NSE:NIFTY50-INDEX",
                "BANKNIFTY": "NSE:NIFTYBANK-INDEX",
            }
            if ":" in instrument: fyers_symbol = instrument
            else: fyers_symbol = mapping.get(instrument.upper(), f"NSE:{instrument.upper()}-EQ")
            
            quote = self.api.get_latest_quote(fyers_symbol)
            spot_price = float(quote.get("lastTradedPrice", 0.0))
            if spot_price <= 0: raise ValueError("Invalid spot")
                
            interval = 100
            if instrument == "NIFTY50": interval = 50
            elif instrument not in ["BANKNIFTY", "SENSEX"]: interval = 10
            atm_strike = int(round(spot_price / interval) * interval)
            
            underlying_map = {
                "NSE:NIFTYBANK-INDEX": "BANKNIFTY",
                "NSE:NIFTY50-INDEX": "NIFTY",
                "BSE:SENSEX-INDEX": "SENSEX",
                "BANKNIFTY": "BANKNIFTY",
                "NIFTY50": "NIFTY",
                "SENSEX": "SENSEX"
            }
            underlying = underlying_map.get(instrument, instrument)
            if ":" in underlying:
                underlying = underlying.split(":")[1].split("-")[0].replace("50", "")

            expiries = self.api.get_expiries(underlying)
            if not expiries: raise ValueError(f"No expiries for {underlying}")
                
            today_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")
            valid_expiries = [x for x in expiries if str(x["expiryDate"]) >= today_str]
            if not valid_expiries: raise ValueError("No valid expiries")
            
            nearest_expiry = str(valid_expiries[0]["expiryDate"])
            atm_ce = self.api.get_exact_contract(underlying, nearest_expiry, atm_strike, "CE")
            if not atm_ce or "symbol" not in atm_ce:
                option_symbol = f"NSE:{underlying}{nearest_expiry.replace('-', '')[2:]}{atm_strike}CE"
            else:
                option_symbol = atm_ce["symbol"]
            
            premium = self._get_contract_ltp(option_symbol)
            support = atm_strike - 100
            resistance = atm_strike + 100
        except Exception as e:
            print(f"WARN: Live data resolution failed for E2E test ({e}). Falling back to mock data.")
            spot_price = 10000.0
            atm_strike = 10000
            option_symbol = f"MOCK:{instrument}-10000CE"
            premium = 150.0
            support = 9900
            resistance = 10100

        alert_payload = {
            "title": f"E2E TEST: {instrument} BREAKOUT",
            "message": f"Spot at {spot_price}, ATM calculated at {atm_strike}. Watch {option_symbol}. Premium: ₹{premium}",
            "source": "logic_engine",
            "underlying": instrument,
            "severity": "info",
            "symbol": option_symbol,
            "simulationRunId": None
        }
        redis_client.publish("alerts:new", json.dumps(alert_payload))
        print(f"E2E Test complete for {instrument} - Alert Published!")

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "last_alert_time": 0.0,
            "last_highest_call_oi_strike": None,
            "last_highest_put_oi_strike": None,
        }

    def _fetch_15m_high_low(self, symbol: str) -> tuple[float, float]:
        try:
            bars = self.api.get_recent_bars(symbol, resolution="15m", take=2)
            if not bars:
                return 0.0, 0.0
            latest_bar = bars[0]
            return latest_bar.get("high", 0.0), latest_bar.get("low", 0.0)
        except Exception as e:
            print(f"Error fetching 15m bars for {symbol}: {e}")
            return 0.0, 0.0

    def _warn_once(self, key: str, message: str) -> None:
        """A missing-data notice belongs in the log once, not on every bar."""
        if key not in self._warned:
            self._warned.add(key)
            print(f"[LOGIC-ENGINE] {message}")

    def _session_vwap(self, symbol: str) -> tuple[float, str]:
        """
        Today's volume-weighted average price from the stored 1-minute bars,
        as (value, source) where source is "vwap" or "ltp".

        The quote row carries no VWAP field, so it is computed here:
        Σ(typical price × volume) / Σ volume over the bars of the newest IST
        session. Index symbols report no volume at all, so for them (and for
        any symbol before its first traded minute) this falls back to the last
        traded price — the caller shows which one it used, because "spot above
        its own LTP" is never a breakout.
        """
        try:
            bars = self.api.get_recent_bars(symbol, resolution="1m", take=500) or []
        except Exception as ex:
            print(f"Error fetching 1m bars for {symbol}: {ex}")
            bars = []

        session_day = None
        for bar in bars:
            day = self._ist_day(bar.get("barStartUtc"))
            if day and (session_day is None or day > session_day):
                session_day = day

        weighted = 0.0
        volume = 0.0
        for bar in bars:
            if session_day and self._ist_day(bar.get("barStartUtc")) != session_day:
                continue
            try:
                bar_volume = float(bar.get("volumeDelta") or 0.0)
                if bar_volume <= 0:
                    continue
                high = float(bar.get("high") or 0.0)
                low = float(bar.get("low") or 0.0)
                close = float(bar.get("close") or 0.0)
            except (TypeError, ValueError):
                continue
            weighted += ((high + low + close) / 3.0) * bar_volume
            volume += bar_volume

        if volume > 0:
            return weighted / volume, "vwap"

        self._warn_once(
            f"vwap:{symbol}",
            f"no traded volume in the stored 1m bars for {symbol}; comparing against its LTP instead.",
        )
        return self._get_contract_ltp(symbol), "ltp"

    @staticmethod
    def _ist_day(bar_start_utc: Any) -> str:
        """"2026-09-04T10:00:00Z" -> the IST calendar day "2026-09-04"."""
        text = str(bar_start_utc or "")
        if not text:
            return ""
        try:
            stamp = datetime.fromisoformat(text.replace("Z", "+00:00"))
        except ValueError:
            return ""
        if stamp.tzinfo is None:
            stamp = stamp.replace(tzinfo=timezone.utc)
        return stamp.astimezone(IST).date().isoformat()

    def _top_of_book(self, symbol: str) -> Optional[tuple]:
        """
        (bid size, ask size) from the newest stored tick, or None when the feed
        does not carry depth for this symbol. The broker sends bid/ask sizes for
        option contracts but leaves them null on index symbols, so a caller must
        skip its rule rather than read the nulls as zeros.
        """
        if not symbol:
            return None
        try:
            ticks = self.api.get_recent_ticks(symbol, take=1) or []
        except Exception as ex:
            print(f"Error fetching ticks for {symbol}: {ex}")
            return None
        if not ticks:
            return None

        bid = ticks[0].get("bidSize")
        ask = ticks[0].get("askSize")
        if bid is None or ask is None:
            self._warn_once(
                f"depth:{symbol}",
                f"the feed carries no bid/ask depth for {symbol}; the order-book rule is skipped for it.",
            )
            return None
        try:
            return float(bid), float(ask)
        except (TypeError, ValueError):
            return None

    def _chain_facts(self, underlying: str) -> tuple:
        """
        (expiry, strike step, [strikes]) for the nearest expiry of this
        underlying, resolved once and reused: the chain only changes when the
        expiry rolls, which restarts the runner anyway.
        """
        cached = self._chain_cache.get(underlying)
        if cached is not None:
            return cached

        expiry = ""
        step = fallback_strike_step(underlying)
        chain: List[Dict[str, Any]] = []
        try:
            today = datetime.now(timezone.utc).date().isoformat()
            expiries = [str(x.get("expiryDate", "")) for x in (self.api.get_expiries(underlying) or [])]
            future = sorted(x for x in expiries if x and x >= today)
            if future:
                expiry = future[0]
                chain = self.api.get_option_chain(underlying, expiry) or []
                step = strike_step_from_chain(chain) or step
        except Exception as ex:
            print(f"Error resolving the option chain for {underlying}: {ex}")

        facts = (expiry, step, chain)
        self._chain_cache[underlying] = facts
        return facts

    def _highest_oi_strikes(self, underlying: str, atm_strike: float) -> tuple:
        """
        The CE and PE strikes carrying the most open interest within
        `strike_window` strikes of the ATM, as (ce strike, pe strike, step).
        Either strike is None when the feed reports no open interest for that
        side — the caller must then skip the rule instead of guessing a strike.
        """
        expiry, step, chain = self._chain_facts(underlying)
        if not chain:
            self._warn_once(
                f"chain:{underlying}",
                f"no option chain for {underlying} in the instrument master; the OI rule is skipped.",
            )
            return None, None, step

        span = step * self.strike_window
        wanted = {}
        for row in chain:
            try:
                strike = float(row.get("strikePrice") or 0)
            except (TypeError, ValueError):
                continue
            symbol = str(row.get("symbol") or "")
            option_type = str(row.get("optionType") or "").upper()
            if not symbol or option_type not in ("CE", "PE") or strike <= 0:
                continue
            if abs(strike - float(atm_strike)) > span:
                continue
            wanted[symbol] = (option_type, strike)

        if not wanted:
            return None, None, step

        try:
            quotes = self.api.get_all_latest_quotes() or []
        except Exception as ex:
            print(f"Error fetching quotes for the OI scan: {ex}")
            return None, None, step

        best = {"CE": (None, 0.0), "PE": (None, 0.0)}
        seen_oi = False
        for quote in quotes:
            entry = wanted.get(str(quote.get("symbol") or ""))
            if entry is None:
                continue
            raw_oi = quote.get("openInterest")
            if raw_oi is None:
                continue
            try:
                open_interest = float(raw_oi)
            except (TypeError, ValueError):
                continue
            if open_interest <= 0:
                continue
            seen_oi = True
            option_type, strike = entry
            if open_interest > best[option_type][1]:
                best[option_type] = (strike, open_interest)

        if not seen_oi:
            self._warn_once(
                f"oi:{underlying}",
                f"the feed reports no open interest for {underlying} contracts; the OI rule is skipped "
                "until the ingestor stores OI.",
            )
            return None, None, step

        return best["CE"][0], best["PE"][0], step

    def _get_contract_ltp(self, option_symbol: str) -> float:
        try:
            if not option_symbol: return 0.0
            quote = self.api.get_latest_quote(option_symbol)
            return float(quote.get("lastTradedPrice", 0.0))
        except Exception as e:
            return 0.0

    def _publish_alert(self, title: str, message: str, symbol: str, severity: str, index_symbol: str, simulation_run_id: int):
        alert_payload = {
            "title": title,
            "message": message,
            "source": "logic_engine",
            "underlying": index_symbol,
            "severity": severity,
            "symbol": symbol,
            "simulationRunId": simulation_run_id
        }
        self.redis_client.publish("alerts:new", json.dumps(alert_payload))

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals = []
        if not inp.atm_strike:
            return signals
            
        current_time = time.time()
        if current_time - state["last_alert_time"] < self.cooldown_seconds:
            return signals

        index_symbol = inp.underlying
        spot = inp.spot_price
        
        is_index = index_symbol in ["BANKNIFTY", "NIFTY", "SENSEX", "NIFTY50"]
        alert_triggered = False

        # RULE 1: Breakout Logic
        if is_index:
            spot_sym = "NSE:NIFTYBANK-INDEX" if index_symbol == "BANKNIFTY" else (
                "NSE:NIFTY50-INDEX" if index_symbol in ["NIFTY", "NIFTY50"] else index_symbol
            )
            index_high, _ = self._fetch_15m_high_low(spot_sym)
            
            if spot > index_high and index_high > 0:
                heavyweight_divergence = False
                divergence_reason = ""
                for hw in self.heavyweights:
                    _, hw_15m_low = self._fetch_15m_high_low(hw)
                    hw_ltp = self._get_contract_ltp(hw)
                    if hw_ltp < hw_15m_low and hw_15m_low > 0:
                        heavyweight_divergence = True
                        divergence_reason = f"{hw.split(':')[1].split('-')[0]} broke Day Low"
                        break
                
                if heavyweight_divergence:
                    pe_symbol = inp.contracts.get("atm_pe").symbol if "atm_pe" in inp.contracts else ""
                    premium = self._get_contract_ltp(pe_symbol)
                    
                    self._publish_alert(
                        title=f"{index_symbol} BEAR TRAP",
                        message=f"Logic: {divergence_reason}. Action: Watch {inp.atm_strike} PE. Premium: ₹{premium}",
                        symbol=pe_symbol,
                        severity="warning",
                        index_symbol=index_symbol,
                        simulation_run_id=getattr(inp, 'simulation_run_id', None)
                    )
                    signals.append(StrategySignal(self.name, "ALERT", inp.timestamp_utc, "Rule 1: Bear Trap divergence detected."))
                    alert_triggered = True
        else:
            equity_symbol = f"NSE:{index_symbol}-EQ" if not index_symbol.startswith("NSE:") else index_symbol
            equity_vwap, vwap_source = self._session_vwap(equity_symbol)

            # Without real volume the "VWAP" is just the last traded price, and
            # "spot above its own LTP" says nothing — skip rather than alert.
            if vwap_source == "vwap" and spot > equity_vwap and equity_vwap > 0:
                ce_symbol = inp.contracts.get("atm_ce").symbol if "atm_ce" in inp.contracts else ""
                premium = self._get_contract_ltp(ce_symbol)

                self._publish_alert(
                    title=f"{index_symbol} BULLISH BREAKOUT",
                    message=f"Logic: Price {spot} broke above the session VWAP {equity_vwap:.2f}. Action: Watch {inp.atm_strike} CE. Premium: ₹{premium}",
                    symbol=ce_symbol,
                    severity="info",
                    index_symbol=index_symbol,
                    simulation_run_id=getattr(inp, 'simulation_run_id', None)
                )
                signals.append(StrategySignal(self.name, "ALERT", inp.timestamp_utc, "Rule 1: Equity VWAP Breakout detected."))
                alert_triggered = True

        if alert_triggered:
            state["last_alert_time"] = current_time
            return signals

        # RULE 2: option-chain open-interest shift.
        # The peak-OI strike comes from the real chain plus the feed's open
        # interest; when either is unavailable both strikes are None and the
        # rule stays silent rather than inventing a level.
        ce_oi_strike, pe_oi_strike, oi_step = self._highest_oi_strikes(index_symbol, inp.atm_strike)
        shift_points = oi_step * max(1, self.oi_shift_steps)

        previous_ce = state.get("last_highest_call_oi_strike")
        previous_pe = state.get("last_highest_put_oi_strike")

        if ce_oi_strike is not None and previous_ce is not None and (previous_ce - ce_oi_strike) >= shift_points:
            # Call writing moved closer to the money: sellers expect a lower level.
            pe_symbol = inp.contracts.get("atm_pe").symbol if "atm_pe" in inp.contracts else ""
            premium = self._get_contract_ltp(pe_symbol)
            self._publish_alert(
                title=f"{index_symbol} BEARISH OI SHIFT",
                message=f"Peak call OI moved down from {previous_ce:g} to {ce_oi_strike:g}. Watch {inp.atm_strike} PE. Premium: ₹{premium}",
                symbol=pe_symbol,
                severity="warning",
                index_symbol=index_symbol,
                simulation_run_id=getattr(inp, 'simulation_run_id', None)
            )
            signals.append(StrategySignal(self.name, "ALERT", inp.timestamp_utc, "Rule 2: Bearish OI shift detected."))
            alert_triggered = True
        elif pe_oi_strike is not None and previous_pe is not None and (pe_oi_strike - previous_pe) >= shift_points:
            # Put writing moved up: sellers are defending a higher level.
            ce_symbol = inp.contracts.get("atm_ce").symbol if "atm_ce" in inp.contracts else ""
            premium = self._get_contract_ltp(ce_symbol)
            self._publish_alert(
                title=f"{index_symbol} BULLISH OI SHIFT",
                message=f"Peak put OI moved up from {previous_pe:g} to {pe_oi_strike:g}. Watch {inp.atm_strike} CE. Premium: ₹{premium}",
                symbol=ce_symbol,
                severity="info",
                index_symbol=index_symbol,
                simulation_run_id=getattr(inp, 'simulation_run_id', None)
            )
            signals.append(StrategySignal(self.name, "ALERT", inp.timestamp_utc, "Rule 2: Bullish OI shift detected."))
            alert_triggered = True

        if ce_oi_strike is not None:
            state["last_highest_call_oi_strike"] = ce_oi_strike
        if pe_oi_strike is not None:
            state["last_highest_put_oi_strike"] = pe_oi_strike

        if alert_triggered:
            state["last_alert_time"] = current_time
            return signals

        # RULE 3: order-book imbalance on the call the market would be writing
        # near resistance. Depth is read from the option's own ticks — index
        # symbols carry none — and the rule is skipped when the feed has none.
        ce_contract = inp.contracts.get("atm_ce")
        depth_symbol = ce_contract.symbol if ce_contract is not None else ""
        depth = self._top_of_book(depth_symbol)

        resistance_level = inp.atm_strike + 100
        is_near_resistance = abs(spot - resistance_level) < 20

        total_bid, total_ask = depth if depth is not None else (0.0, 0.0)

        if depth is not None and is_near_resistance and total_bid > 0 and total_ask > (self.ask_bid_ratio * total_bid):
            pe_symbol = inp.contracts.get("atm_pe").symbol if "atm_pe" in inp.contracts else ""
            premium = self._get_contract_ltp(pe_symbol)
            self._publish_alert(
                title=f"{index_symbol} HEAVY SELLING PRESSURE",
                message=(
                    f"Top-of-book on {depth_symbol}: ask {total_ask:g} > {self.ask_bid_ratio}x bid {total_bid:g} "
                    f"near resistance {resistance_level}. Watch {inp.atm_strike} PE. Premium: ₹{premium}"
                ),
                symbol=pe_symbol,
                severity="error",
                index_symbol=index_symbol,
                simulation_run_id=getattr(inp, 'simulation_run_id', None)
            )
            signals.append(StrategySignal(self.name, "ALERT", inp.timestamp_utc, "Rule 3: Selling Pressure."))
            alert_triggered = True

        if alert_triggered:
            state["last_alert_time"] = current_time

        return signals
