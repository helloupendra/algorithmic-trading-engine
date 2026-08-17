import requests
from core.api_client import api_get, api_post, api_delete, api_put
import argparse
import urllib3
import os

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL

def add_group_to_watchlist(group_name: str):
    print(f"Injecting group '{group_name}' into the live watchlist...")
    url = f"{API_BASE_URL}/api/Equities/live/watchlist/group"
    
    payload = {
        "GroupName": group_name,
        "DataType": "symbolUpdate" 
    }
    
    try:
        response = api_post(url, json=payload, verify=False)
        response.raise_for_status()
        
        result = response.json()
        print("\nSuccess! The C# API processed the group request and sent a Redis broadcast.")
        print(f"Total Symbols Found: {result.get('totalMemberResolved')}")
        print(f"Successfully Added: {result.get('upserted')}")
        print(f"Symbols: {', '.join(result.get('symbols', []))}")
            
    except requests.exceptions.HTTPError as err:
        print(f"HTTP Error: {response.text}")
    except Exception as e:
        print(f"An error occurred: {e}")

def add_symbol_to_watchlist(symbol: str):
    print(f"Injecting single symbol '{symbol}' into the live watchlist...")
    url = f"{API_BASE_URL}/api/LiveData/watchlist"
    
    payload = {
        "Symbol": symbol,
        "DataType": "symbolUpdate"
    }
    
    try:
        response = api_post(url, json=payload, verify=False)
        response.raise_for_status()
        
        result = response.json()
        print("\nSuccess! The C# API processed the single symbol request and sent a Redis broadcast.")
        print(f"Added Symbol: {result.get('symbol')}")
            
    except requests.exceptions.HTTPError as err:
        print(f"HTTP Error: {response.text}")
    except Exception as e:
        print(f"An error occurred: {e}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Inject single stocks or equity groups into the live terminal")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument(
        "--group", 
        type=str, 
        help="The name of the equity group to inject (e.g., BANKNIFTY_CONSTITUENTS)"
    )
    group.add_argument(
        "--symbol",
        type=str,
        help="The specific symbol to inject (e.g., NSE:RELIANCE-EQ, NSE:NIFTYBANK-INDEX)"
    )
    
    args = parser.parse_args()
    
    if args.group:
        add_group_to_watchlist(args.group)
    elif args.symbol:
        add_symbol_to_watchlist(args.symbol)
