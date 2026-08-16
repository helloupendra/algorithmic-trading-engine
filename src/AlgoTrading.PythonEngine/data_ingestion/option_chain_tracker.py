import requests
import time
import argparse
import urllib3
import os

# Disable SSL warnings for localhost
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL
POLL_INTERVAL_SECONDS = 300  # Check every 5 minutes
STRIKE_INTERVAL = 100        # Nifty Bank interval
NUM_STRIKES = 15             # +/- 15 strikes

class OptionChainRecorder:
    def __init__(self, spot_symbol: str, underlying: str):
        self.spot_symbol = spot_symbol
        self.underlying = underlying
        self.active_subscriptions = set()

    def get_spot_price(self) -> float:
        resp = requests.get(f"{API_BASE_URL}/api/LiveData/latest", params={"symbol": self.spot_symbol}, verify=False)
        resp.raise_for_status()
        return float(resp.json()["lastTradedPrice"])

    def get_nearest_expiry(self) -> str:
        resp = requests.get(f"{API_BASE_URL}/api/Instruments/derivatives/expiries", params={"underlying": self.underlying}, verify=False)
        resp.raise_for_status()
        expiries = resp.json()
        if not expiries:
            raise RuntimeError(f"No expiries found for {self.underlying}")
        return str(expiries[0]["expiryDate"])

    def resolve_symbol(self, expiry: str, strike: int, option_type: str) -> str:
        try:
            resp = requests.get(
                f"{API_BASE_URL}/api/Instruments/derivatives/contract",
                params={
                    "underlying": self.underlying,
                    "expiry": expiry,
                    "strike": strike,
                    "optionType": option_type,
                },
                verify=False
            )
            resp.raise_for_status()
            data = resp.json()
            if "symbol" in data:
                return data["symbol"]
        except Exception as e:
            print(f"Failed to resolve {self.underlying} {expiry} {strike} {option_type}: {e}")
        return None

    def upsert_watchlist(self, symbol: str, priority: int = 20):
        try:
            payload = {
                "symbol": symbol,
                "dataType": "symbolUpdate",
                "isActive": True,
                "priority": priority
            }
            resp = requests.post(f"{API_BASE_URL}/api/LiveData/watchlist", json=payload, verify=False)
            resp.raise_for_status()
        except Exception as e:
            print(f"Failed to upsert {symbol} to watchlist: {e}")

    def run(self):
        print(f"Starting Option Chain Tracker for {self.underlying}...")
        while True:
            try:
                spot = self.get_spot_price()
                expiry = self.get_nearest_expiry()
                
                atm = int(round(spot / STRIKE_INTERVAL) * STRIKE_INTERVAL)
                print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] Spot: {spot:.2f} | ATM: {atm} | Expiry: {expiry}")
                
                required_symbols = set()
                
                # Add spot
                required_symbols.add(self.spot_symbol)
                
                # Add strikes
                for i in range(-NUM_STRIKES, NUM_STRIKES + 1):
                    strike = atm + (i * STRIKE_INTERVAL)
                    ce = self.resolve_symbol(expiry, strike, "CE")
                    pe = self.resolve_symbol(expiry, strike, "PE")
                    if ce: required_symbols.add(ce)
                    if pe: required_symbols.add(pe)
                
                # Add to watchlist
                for sym in required_symbols:
                    if sym not in self.active_subscriptions:
                        self.upsert_watchlist(sym)
                        self.active_subscriptions.add(sym)
                
                print(f"Tracking {len(self.active_subscriptions)} total symbols.")
                
            except Exception as e:
                print(f"Error in Option Chain Tracker loop: {e}")
                
            time.sleep(POLL_INTERVAL_SECONDS)

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--spot', default="NSE:NIFTYBANK-INDEX")
    parser.add_argument('--underlying', default="BANKNIFTY")
    args = parser.parse_args()
    
    tracker = OptionChainRecorder(args.spot, args.underlying)
    tracker.run()