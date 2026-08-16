import requests
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

def monitor_live_prices(group_name: str, interval: int):
    url = f"{API_BASE_URL}/api/Equities/live/latest/group"
    
    while True:
        try:
            response = requests.get(url, params={"groupName": group_name}, verify=False)
            response.raise_for_status()
            
            data = response.json()
            members = data.get('members', [])
            
            clear_screen()
            print(f"=== Live Monitor: {data.get('displayName', group_name)} ===")
            print(f"Total Members: {data.get('totalMembers', 0)} | With Live Data: {data.get('membersWithLiveData', 0)}")
            print("-" * 75)
            print(f"{'Symbol':<20} | {'Status':<12} | {'LTP':>10} | {'Vol':>10} | {'Updated At':>20}")
            print("-" * 75)
            
            for member in members:
                symbol = member.get('symbol', 'Unknown')
                has_live_data = member.get('hasLiveData', False)
                ltp = member.get('lastTradedPrice')
                vol = member.get('volume')
                updated_at = member.get('updatedUtc', '')
                
                status = "LIVE" if has_live_data else "NO DATA"
                ltp_str = f"{ltp:,.2f}" if ltp is not None else "N/A"
                vol_str = f"{vol:,}" if vol is not None else "N/A"
                updated_str = updated_at[:19].replace('T', ' ') if updated_at else "N/A"
                
                print(f"{symbol:<20} | {status:<12} | {ltp_str:>10} | {vol_str:>10} | {updated_str:>20}")
                
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
    parser = argparse.ArgumentParser(description="Continuously monitor live prices for an equity group")
    parser.add_argument(
        "--group", 
        type=str, 
        default="BANKNIFTY_CONSTITUENTS", 
        help="The name of the equity group (e.g., 'BANKNIFTY_CONSTITUENTS', 'NIFTY50_CONSTITUENTS')"
    )
    parser.add_argument(
        "--interval",
        type=int,
        default=2,
        help="Polling interval in seconds (default: 2)"
    )
    args = parser.parse_args()
    
    try:
        monitor_live_prices(args.group, args.interval)
    except KeyboardInterrupt:
        print("\nMonitoring stopped by user.")
