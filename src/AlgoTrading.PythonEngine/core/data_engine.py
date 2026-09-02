import os
import time
import requests
import pandas as pd
from typing import List, Dict, Optional
from datetime import datetime, timezone
from fyers_apiv3 import fyersModel

from core.api_client import build_session
from core.config import API_BASE_URL, VERIFY_SSL, require_app_id, FYERS_LOG_PATH
from core.data_models import BarData, OptionChainSnapshot, OptionContractLive, TickData, OptionGreeks
from core.greeks_calculator import calculate_greeks

class DataEngine:
    """
    Unified Data Engine for Historical, Live, and Option Chain data.
    Abstracts all Fyers API complexities into simple, type-safe Python methods.
    """
    def __init__(self):
        self.http = build_session()
        self._access_token = None
        self._fyers = None

    def _get_active_session(self) -> str:
        """Fetches the active Fyers session token from the local C# API."""
        if self._access_token:
            return self._access_token
            
        url = f"{API_BASE_URL}/api/auth/session"
        response = self.http.get(url, verify=VERIFY_SSL, timeout=10)
        response.raise_for_status()
        data = response.json()
        
        if data.get("isAuthenticated") and data.get("accessToken"):
            self._access_token = data["accessToken"]
            return self._access_token
            
        raise RuntimeError("API is not authenticated with Broker. Please login.")

    def _get_fyers_client(self):
        if not self._fyers:
            token = self._get_active_session()
            client_id = require_app_id()
            self._fyers = fyersModel.FyersModel(client_id=client_id, is_async=False, token=token, log_path=FYERS_LOG_PATH)
        return self._fyers

    def get_historical_bars(self, symbol: str, resolution: str, start_date: str, end_date: str) -> List[BarData]:
        """
        Download historical OHLCV data and map it to BarData models.
        resolution: '1', '5', '15', '1D', etc. (automatically handles '1m' format)
        """
        fyers = self._get_fyers_client()
        
        # Fyers expects '5' not '5m', but '1D' is fine
        fyers_res = resolution.replace("m", "") if resolution.endswith("m") else resolution
        
        data_req = {
            "symbol": symbol,
            "resolution": fyers_res,
            "date_format": "1", # YYYY-MM-DD
            "range_from": start_date,
            "range_to": end_date,
            "cont_flag": "1" 
        }
        
        response = fyers.history(data=data_req)
        
        if response.get("s") != "ok":
            raise Exception(f"Failed to fetch data: {response.get('message')}")
            
        candles = response.get("candles", [])
        bars = []
        for candle in candles:
            epoch, o, h, l, c, v = candle
            dt = datetime.fromtimestamp(epoch, tz=timezone.utc)
            bars.append(BarData(
                symbol=symbol,
                resolution=resolution + "m" if resolution.isdigit() else resolution,
                timestamp_start=dt,
                open=o,
                high=h,
                low=l,
                close=c,
                volume=v
            ))
            
        return bars

    def get_latest_quote(self, symbol: str) -> Optional[TickData]:
        """Fetch the immediate LTP for a single symbol using Fyers Quotes API"""
        fyers = self._get_fyers_client()
        response = fyers.quotes({"symbols": symbol})
        if response.get("s") != "ok" or not response.get("d"):
            return None
            
        data = response["d"][0]["v"]
        
        return TickData(
            symbol=symbol,
            market_type="EQUITY",
            timestamp=datetime.now(timezone.utc),
            last_traded_price=data.get("lp", 0.0),
            last_traded_qty=0,
            average_trade_price=0.0,
            volume=data.get("volume", 0)
        )

    def get_option_chain(self, underlying_symbol: str, expiry_date: str) -> OptionChainSnapshot:
        """
        Builds a real-time Option Chain for the given underlying.
        Example: get_option_chain("NSE:NIFTYBANK-INDEX", "26SEP")
        """
        spot_tick = self.get_latest_quote(underlying_symbol)
        if not spot_tick:
            raise Exception(f"Could not fetch spot price for {underlying_symbol}")
            
        spot_price = spot_tick.last_traded_price
        
        # Determine step size (e.g. BankNifty = 100)
        step_size = 100 if "BANK" in underlying_symbol else 50
        base_strike = round(spot_price / step_size) * step_size
        
        # Generate Strikes (10 ITM, 1 ATM, 10 OTM)
        strikes = [base_strike + (i * step_size) for i in range(-10, 11)]
        
        # Construct Symbols (e.g. NSE:BANKNIFTY26SEP57000CE)
        prefix = "NSE:BANKNIFTY" if "BANK" in underlying_symbol else "NSE:NIFTY"
        
        symbols_to_fetch = []
        for strike in strikes:
            symbols_to_fetch.append(f"{prefix}{expiry_date}{strike}CE")
            symbols_to_fetch.append(f"{prefix}{expiry_date}{strike}PE")
            
        # Fetch Quotes for all symbols in blocks of 50
        fyers = self._get_fyers_client()
        
        contracts = {}
        for i in range(0, len(symbols_to_fetch), 50):
            batch = symbols_to_fetch[i:i+50]
            symbols_str = ",".join(batch)
            quotes_response = fyers.quotes({"symbols": symbols_str})
            
            if quotes_response.get("s") == "ok":
                for q in quotes_response.get("d", []):
                    sym = q["n"]
                    v = q["v"]
                    ltp = v.get("lp", 0.0)
                    
                    import re
                    m = re.search(r'NSE:([A-Z]+).*?(\d+)(CE|PE)$', sym)
                    if m and ltp > 0:
                        strike = float(m.group(2))
                        opt_type = m.group(3)
                        
                        tick = TickData(
                            symbol=sym,
                            market_type="FNO",
                            timestamp=datetime.now(timezone.utc),
                            last_traded_price=ltp,
                            last_traded_qty=0,
                            average_trade_price=0.0,
                            volume=v.get("volume", 0)
                        )
                        
                        greeks = calculate_greeks(
                            spot=spot_price,
                            strike=strike,
                            tte_years=7.0/365.0, # Mock 7 days to expiry
                            option_type=opt_type,
                            option_price=ltp
                        )
                        
                        contracts[sym] = OptionContractLive(
                            symbol=sym,
                            strike=strike,
                            option_type=opt_type,
                            expiry=datetime.now(timezone.utc).date(),
                            underlying_price=spot_price,
                            tick=tick,
                            greeks=greeks
                        )
                        
        return OptionChainSnapshot(
            underlying_symbol=underlying_symbol,
            timestamp=datetime.now(timezone.utc),
            contracts=contracts
        )
