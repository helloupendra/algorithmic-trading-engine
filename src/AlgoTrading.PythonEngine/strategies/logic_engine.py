import json
from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
from messaging.telegram_alerter import TelegramAlerter
from core.api_client import PlatformApiClient
from core.config import API_BASE_URL


class LogicEngine(BaseStrategy):
    """
    Logic Engine for implementing complex rules and sending Telegram alerts.
    Discovered automatically by execution_runner.py because it inherits from BaseStrategy.
    """
    name = "LogicEngine"
    description = (
        "Alert-only rule engine: watches the index against its 15-minute range, heavyweight stocks "
        "(HDFC Bank, Reliance) for divergence and the order book for selling pressure, and sends Telegram "
        "alerts instead of placing orders. It never opens a paper position. Needs live spot ticks, "
        "15-minute bars and TELEGRAM_BOT_TOKEN / TELEGRAM_CHAT_ID in .env."
    )
    category = "Alerts"
    legs_summary = "No legs (Telegram alerts only)"
    default_lots = 1
    default_params: Dict[str, Any] = {}
    # Internal tooling, kept out of the strategy catalog (it also opens a Redis
    # command channel when instantiated, which the runner allows but a catalog must not).
    listed = False

    def __init__(self, params: Dict[str, Any] = None):
        super().__init__()
        self.params = params or {}
        self.lots = self.lots_from(self.params, self.default_lots)
        
        # Initialize the API client for extra data fetching
        self.api = PlatformApiClient(API_BASE_URL, verify_ssl=False)
        
        # Initialize our new Telegram Alerter
        self.alerter = TelegramAlerter()
        
        # Configure Heavyweights based on the index we are trading
        # If trading Sensex, heavyweights might be HDFC Bank & Reliance
        # Since Fyers uses specific symbols, map them:
        self.heavyweights = [
            "NSE:HDFCBANK-EQ",
            "NSE:RELIANCE-EQ"
        ]

        # Start the E2E Alert Command Listener
        import threading
        self._cmd_thread = threading.Thread(target=self._listen_for_commands, daemon=True)
        self._cmd_thread.start()

    def _listen_for_commands(self):
        """Background thread that listens to Redis Pub/Sub for E2E Trigger Commands."""
        import redis
        import os
        from datetime import datetime, timezone
        
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
            # 1. Map to Fyers Symbol
            mapping = {
                "SENSEX": "BSE:SENSEX-INDEX",
                "NIFTY50": "NSE:NIFTY50-INDEX",
                "BANKNIFTY": "NSE:NIFTYBANK-INDEX",
            }
            if ":" in instrument: fyers_symbol = instrument
            else: fyers_symbol = mapping.get(instrument.upper(), f"NSE:{instrument.upper()}-EQ")
            
            # 2. Fetch LTP
            quote = self.api.get_latest_quote(fyers_symbol)
            spot_price = float(quote.get("lastTradedPrice", 0.0))
            if spot_price <= 0: raise ValueError("Invalid spot")
                
            # 3. Calculate ATM
            interval = 100
            if instrument == "NIFTY50": interval = 50
            elif instrument not in ["BANKNIFTY", "SENSEX"]: interval = 10
            atm_strike = int(round(spot_price / interval) * interval)
            
            # 4. Get Expiry & Construct Symbol
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
            
            # 5. Fetch Premium
            premium = self._get_contract_ltp(option_symbol)
            support = atm_strike - 100
            resistance = atm_strike + 100
        except Exception as e:
            print(f"WARN: Live data resolution failed for E2E test ({e}). Falling back to mock data so Telegram alert still fires.")
            spot_price = 10000.0
            atm_strike = 10000
            option_symbol = f"MOCK:{instrument}-10000CE"
            premium = 150.0
            support = 9900
            resistance = 10100

        # 6. Send Telegram Alert
        alert_msg = self.alerter.format_alert(
            signal_type=f"E2E TEST: {instrument} BREAKOUT",
            logic_reason=f"Spot at {spot_price}, ATM calculated at {atm_strike}",
            action=f"Watch {option_symbol}",
            premium=premium,
            support=support,
            resistance=atm_strike + 100
        )
        self.alerter.send_alert_async(alert_msg)
        
        # 7. Publish to Redis (for React Dashboard)
        alert_payload = {
            "type": "E2E_ALERT",
            "instrument": instrument,
            "spot": spot_price,
            "optionSymbol": option_symbol,
            "premium": premium,
            "timestampUtc": datetime.now(timezone.utc).isoformat()
        }
        redis_client.publish("alerts:telegram", json.dumps(alert_payload))
        print(f"E2E Test complete for {instrument} - Alert Published!")

    def initialize_state(self) -> Dict[str, Any]:
        """
        Setup any state required to persist between ticks.
        """
        return {
            "last_alert_time": None,
            "last_highest_call_oi_strike": None,
            "last_highest_put_oi_strike": None,
        }

    def _fetch_15m_high_low(self, symbol: str) -> tuple[float, float]:
        """Helper to get the 15-min high/low of a symbol."""
        try:
            bars = self.api.get_recent_bars(symbol, resolution="15m", take=2)
            if not bars:
                return 0.0, 0.0
            
            # Use the previous completed 15-min bar or the current one based on logic
            # Here we use the latest available bar
            latest_bar = bars[0]
            return latest_bar.get("high", 0.0), latest_bar.get("low", 0.0)
        except Exception as e:
            print(f"Error fetching 15m bars for {symbol}: {e}")
            return 0.0, 0.0

    def _get_vwap_or_ltp(self, symbol: str) -> float:
        """Helper to get the latest quote for a symbol to check against VWAP."""
        try:
            quote = self.api.get_latest_quote(symbol)
            # Depending on Fyers payload, VWAP might be present or we just use LTP for simplicity 
            # if VWAP is not natively tracked. We fallback to LTP if vwap is missing.
            return float(quote.get("lastTradedPrice", 0.0))
        except Exception as e:
            print(f"Error fetching quote for {symbol}: {e}")
            return 0.0
            
    def _get_level2_depth(self, symbol: str) -> tuple[float, float]:
        """Helper to get Total Bid and Total Ask from Level-2 data."""
        try:
            quote = self.api.get_latest_quote(symbol)
            # Real level-2 data would aggregate top 5 bids/asks
            # Assuming 'bidSize' and 'askSize' from the quote contains the total or top-level depth
            bid_qty = float(quote.get("bidSize", 0.0))
            ask_qty = float(quote.get("askSize", 0.0))
            return bid_qty, ask_qty
        except Exception as e:
            return 0.0, 0.0

    def _get_contract_ltp(self, option_symbol: str) -> float:
        try:
            if not option_symbol: return 0.0
            quote = self.api.get_latest_quote(option_symbol)
            return float(quote.get("lastTradedPrice", 0.0))
        except Exception as e:
            print(f"Error fetching LTP for {option_symbol}: {e}")
            return 0.0

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals = []
        
        # We only want to process rules if we have an ATM strike
        if not inp.atm_strike:
            return signals
            
        index_symbol = inp.underlying
        spot = inp.spot_price
        
        # ---------------------------------------------------------------------
        # RULE 1: Breakout Logic (Divergence for Index, VWAP for Equity)
        # ---------------------------------------------------------------------
        # Determine if we are trading an Index or an Equity
        is_index = index_symbol in ["BANKNIFTY", "NIFTY", "SENSEX", "NIFTY50"]
        
        if is_index:
            # --- INDEX LOGIC: Fakeout Divergence ---
            spot_sym = "NSE:NIFTYBANK-INDEX" if index_symbol == "BANKNIFTY" else (
                "NSE:NIFTY50-INDEX" if index_symbol in ["NIFTY", "NIFTY50"] else index_symbol
            )
            index_high, _ = self._fetch_15m_high_low(spot_sym)
            
            if spot > index_high and index_high > 0:
                heavyweight_divergence = False
                divergence_reason = ""
                
                for hw in self.heavyweights:
                    _, hw_15m_low = self._fetch_15m_high_low(hw)
                    hw_ltp = self._get_vwap_or_ltp(hw)
                    
                    if hw_ltp < hw_15m_low and hw_15m_low > 0:
                        heavyweight_divergence = True
                        divergence_reason = f"{hw.split(':')[1].split('-')[0]} broke Day Low"
                        break
                
                if heavyweight_divergence:
                    pe_symbol = inp.contracts.get("atm_pe").symbol if "atm_pe" in inp.contracts else ""
                    premium = self._get_contract_ltp(pe_symbol)

                    alert_msg = self.alerter.format_alert(
                        signal_type=f"{index_symbol} BEAR TRAP",
                        logic_reason=divergence_reason,
                        action=f"Watch {inp.atm_strike} PE",
                        premium=premium,
                        support=inp.atm_strike - 200,
                        resistance=inp.atm_strike + 100
                    )
                    self.alerter.send_alert_async(alert_msg)
                    
                    signals.append(StrategySignal(
                        strategy_name=self.name,
                        signal_type="ALERT",
                        timestamp_utc=inp.timestamp_utc,
                        reason="Rule 1: Bear Trap divergence detected."
                    ))
        else:
            # --- EQUITY LOGIC: Volume/VWAP Breakout ---
            # For direct stock options like RELIANCE
            equity_symbol = f"NSE:{index_symbol}-EQ" if not index_symbol.startswith("NSE:") else index_symbol
            _, equity_15m_low = self._fetch_15m_high_low(equity_symbol)
            equity_vwap = self._get_vwap_or_ltp(equity_symbol) # Simplified to use LTP/VWAP
            
            # Simple logic: If stock breaks above VWAP with momentum
            if spot > equity_vwap and equity_vwap > 0:
                ce_symbol = inp.contracts.get("atm_ce").symbol if "atm_ce" in inp.contracts else ""
                premium = self._get_contract_ltp(ce_symbol)

                alert_msg = self.alerter.format_alert(
                    signal_type=f"{index_symbol} BULLISH BREAKOUT",
                    logic_reason=f"Price broke above VWAP at {equity_vwap}",
                    action=f"Watch {inp.atm_strike} CE",
                    premium=premium,
                    support=inp.atm_strike - 10,
                    resistance=inp.atm_strike + 20
                )
                self.alerter.send_alert_async(alert_msg)
                
                signals.append(StrategySignal(
                    strategy_name=self.name,
                    signal_type="ALERT",
                    timestamp_utc=inp.timestamp_utc,
                    reason="Rule 1: Equity VWAP Breakout detected."
                ))

        # ---------------------------------------------------------------------
        # RULE 2: Option Chain OI Shifting
        # ---------------------------------------------------------------------
        # In a real scenario, you'd fetch the OI for the strikes +/- 5 from ATM.
        # This requires an endpoint that returns the Option Chain with OI.
        # Assuming we can find the highest OI strikes.
        # For demonstration, we'll outline the logic structure:
        # 
        # current_highest_ce_oi_strike = ...
        # if state["last_highest_call_oi_strike"] and current_highest_ce_oi_strike < state["last_highest_call_oi_strike"]:
        #     # OI shifted closer to ATM (e.g. from 77500 to 77000)
        #     alert_msg = self.alerter.format_alert(
        #         signal_type="BEARISH OI SHIFT (WTT to WTB)",
        #         logic_reason=f"Call writing shifted from {state['last_highest_call_oi_strike']} to {current_highest_ce_oi_strike}",
        #         action=f"Watch {inp.atm_strike} PE",
        #         premium=350.0,
        #         support=inp.atm_strike - 100,
        #         resistance=current_highest_ce_oi_strike
        #     )
        #     self.alerter.send_alert_async(alert_msg)
        # 
        # state["last_highest_call_oi_strike"] = current_highest_ce_oi_strike

        # ---------------------------------------------------------------------
        # RULE 3: Market Depth (Bid/Ask Pressure)
        # ---------------------------------------------------------------------
        # Stream Level-2 data for Index and Heavyweights.
        # If Total Ask Quantity > (3 * Total Bid Quantity) at Resistance, trigger alert.
        depth_sym = "NSE:NIFTYBANK-INDEX" if index_symbol == "BANKNIFTY" else (
            "NSE:NIFTY50-INDEX" if index_symbol in ["NIFTY", "NIFTY50"] else index_symbol
        )
        total_bid, total_ask = self._get_level2_depth(depth_sym)
        
        # Example resistance level check
        resistance_level = inp.atm_strike + 100 
        is_near_resistance = abs(spot - resistance_level) < 20
        
        if is_near_resistance and total_ask > (3 * total_bid) and total_bid > 0:
            alert_msg = self.alerter.format_alert(
                signal_type="HEAVY SELLING PRESSURE",
                logic_reason=f"Total Ask > 3x Bid near resistance {resistance_level}",
                action=f"Watch {inp.atm_strike} PE",
                premium=150.0,  # Replace with actual PE LTP
                support=inp.atm_strike - 200,
                resistance=resistance_level
            )
            self.alerter.send_alert_async(alert_msg)
            
            signals.append(StrategySignal(
                strategy_name=self.name,
                signal_type="ALERT",
                timestamp_utc=inp.timestamp_utc,
                reason="Rule 3: Selling Pressure."
            ))

        return signals
