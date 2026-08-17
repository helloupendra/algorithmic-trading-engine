import requests
from core.api_client import api_get, api_post, api_delete, api_put
import argparse
import urllib3
import os
import csv
from datetime import datetime
import json

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL

def get_spot_price(symbol: str) -> float:
    url = f"{API_BASE_URL}/api/LiveData/latest?symbol={symbol}"
    try:
        response = api_get(url, verify=False)
        response.raise_for_status()
        data = response.json()
        ltp = data.get('lastTradedPrice')
        if ltp is None or ltp <= 0:
            raise ValueError(f"Spot price for {symbol} is {ltp}")
        return float(ltp)
    except Exception as e:
        print(f"Warning: Failed to fetch live spot price for {symbol}: {e}")
        # Default fallback values for testing
        if "BANKNIFTY" in symbol:
            return 51000.0
        elif "NIFTY50" in symbol:
            return 23000.0
        elif "SENSEX" in symbol:
            return 78000.0
        return 0.0

DEFAULT_CSV_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "NSE_FO.csv")

def find_option_chain(index_prefix: str, spot_price: float, strikes_count: int, csv_path: str = DEFAULT_CSV_PATH):
    options = []
    
    # Prefix usually NIFTY or BANKNIFTY
    if not os.path.exists(csv_path):
        print(f"Error: {csv_path} not found.")
        return []

    print(f"Loading {csv_path} to find nearest expiry options for {index_prefix} around {spot_price}...")
    
    current_time = datetime.now().timestamp()
    
    # Store all valid options
    valid_options = []
    
    with open(csv_path, 'r', encoding='utf-8') as f:
        reader = csv.reader(f)
        for row in reader:
            if len(row) < 17:
                continue
                
            fyers_symbol = row[9]
            # Must be an option and start with NSE:{index_prefix}
            if not fyers_symbol.startswith(f"NSE:{index_prefix}"):
                continue
                
            opt_type = row[16]
            if opt_type not in ["CE", "PE"]:
                continue
                
            try:
                expiry_ts = int(row[8])
                strike = float(row[15])
            except ValueError:
                continue
                
            # Filter out expired options
            if expiry_ts < current_time:
                continue
                
            valid_options.append({
                "symbol": fyers_symbol,
                "expiry": expiry_ts,
                "strike": strike,
                "type": opt_type
            })

    if not valid_options:
        print("No valid options found for the given index.")
        return []

    # Find the nearest expiry timestamp
    nearest_expiry = min(opt['expiry'] for opt in valid_options)
    print(f"Nearest Expiry Timestamp found: {nearest_expiry} ({datetime.fromtimestamp(nearest_expiry).strftime('%Y-%m-%d')})")
    
    # Filter by nearest expiry
    nearest_opts = [opt for opt in valid_options if opt['expiry'] == nearest_expiry]
    
    # Group by strike
    strikes = list(set(opt['strike'] for opt in nearest_opts))
    strikes.sort(key=lambda x: abs(x - spot_price))
    
    # Take the closest `strikes_count` strikes
    selected_strikes = strikes[:strikes_count]
    selected_strikes.sort() # Sort in ascending order
    
    print(f"Selected Strikes: {selected_strikes}")
    
    final_symbols = []
    for opt in nearest_opts:
        if opt['strike'] in selected_strikes:
            final_symbols.append(opt['symbol'])
            
    return sorted(list(set(final_symbols)))

def add_symbols_to_watchlist(symbols: list):
    print(f"Injecting {len(symbols)} symbols into the live watchlist...")
    
    success_count = 0
    for sym in symbols:
        url = f"{API_BASE_URL}/api/LiveData/watchlist"
        payload = {
            "Symbol": sym,
            "DataType": "symbolUpdate"
        }
        try:
            res = api_post(url, json=payload, verify=False)
            if res.status_code == 200:
                success_count += 1
        except Exception as e:
            pass
            
    print(f"\nSuccess! Added {success_count}/{len(symbols)} symbols.")
    print("The Python Ingestor will dynamically sync these via Redis.")

def main():
    parser = argparse.ArgumentParser(description="Add Option Chain to live watchlist")
    parser.add_argument("--index", type=str, required=True, help="Underlying index (e.g. BANKNIFTY, NIFTY)")
    parser.add_argument("--strikes", type=int, default=15, help="Number of strikes to track (default: 15)")
    args = parser.parse_args()
    
    index_prefix = args.index.upper()
    
    # Determine the spot symbol
    spot_symbol_map = {
        "BANKNIFTY": "NSE:NIFTYBANK-INDEX",
        "NIFTY": "NSE:NIFTY50-INDEX",
        "SENSEX": "BSE:SENSEX-INDEX"
    }
    
    spot_symbol = spot_symbol_map.get(index_prefix, f"NSE:{index_prefix}-INDEX")
    spot_price = get_spot_price(spot_symbol)
    
    print(f"Index: {index_prefix} | Spot Symbol: {spot_symbol} | Spot Price: {spot_price}")
    
    symbols_to_add = find_option_chain(index_prefix, spot_price, args.strikes)
    if symbols_to_add:
        add_symbols_to_watchlist(symbols_to_add)

if __name__ == "__main__":
    main()
