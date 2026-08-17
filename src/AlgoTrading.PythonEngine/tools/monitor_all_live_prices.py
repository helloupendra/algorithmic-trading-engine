import requests
from core.api_client import api_get, api_post, api_delete, api_put
import time
import argparse
import urllib3
import os
import sys

# Disable SSL warnings for localhost
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL

def clear_screen():
    os.system('cls' if os.name == 'nt' else 'clear')

def monitor_all_live_prices(interval: int):
    watchlist_url = f"{API_BASE_URL}/api/LiveData/watchlist"
    latest_url = f"{API_BASE_URL}/api/LiveData/latest/all"
    
    while True:
        try:
            # 1. Get Watchlist
            watchlist_resp = api_get(watchlist_url, verify=False)
            watchlist_resp.raise_for_status()
            watchlist_data = watchlist_resp.json()
            tracked_symbols = {item['symbol'] for item in watchlist_data if item.get('isActive', True)}
            
            # 2. Get Latest Prices
            prices_resp = api_get(latest_url, verify=False)
            prices_resp.raise_for_status()
            prices_data = {item['symbol']: item for item in prices_resp.json()}
            
            clear_screen()
            print(f"=== Universal Live Monitor ===")
            print(f"Total Symbols Tracked: {len(tracked_symbols)}")
            print("-" * 75)
            print(f"{'Symbol':<30} | {'LTP':>10} | {'Vol':>10} | {'Updated At':>15}")
            print("-" * 75)
            
            # Sort data by symbol for consistent viewing
            for symbol in sorted(tracked_symbols):
                item = prices_data.get(symbol, {})
                ltp = item.get('lastTradedPrice')
                vol = item.get('volume')
                updated_at = item.get('updatedUtc', '')
                
                ltp_str = f"{ltp:,.2f}" if ltp is not None else "N/A"
                vol_str = f"{vol:,}" if vol is not None else "N/A"
                
                # Format time nicely (extract just HH:MM:SS)
                if updated_at:
                    time_str = updated_at.split('T')[-1][:8]
                else:
                    time_str = "N/A"
                
                print(f"{symbol:<30} | {ltp_str:>10} | {vol_str:>10} | {time_str:>15}")
                
            print("-" * 75)
            print(f"Press Ctrl+C to stop. Updating every {interval}s...")
            
            time.sleep(interval)
            
        except requests.exceptions.RequestException as err:
            clear_screen()
            print(f"Error fetching data: {err}")
            print("Retrying in 5 seconds...")
            time.sleep(5)
        except KeyboardInterrupt:
            print("\nMonitoring stopped by user.")
            sys.exit(0)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Continuously monitor all active live prices")
    parser.add_argument(
        "--interval",
        type=int,
        default=2,
        help="Polling interval in seconds (default: 2)"
    )
    args = parser.parse_args()
    
    try:
        monitor_all_live_prices(args.interval)
    except KeyboardInterrupt:
        print("\nMonitoring stopped by user.")
