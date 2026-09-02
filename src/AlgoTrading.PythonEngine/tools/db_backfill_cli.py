import sys
import os
import argparse
from datetime import datetime, timezone, timedelta

# Ensure we can import from core
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))
from core.api_client import PlatformApiClient
from core.config import API_BASE_URL, VERIFY_SSL

def trigger_single_symbol_backfill(api: PlatformApiClient, symbol: str, res: str, days: int):
    """
    Backfills historical data for a single specific symbol (Equity, Index, or specific Option).
    Uses the /api/Backfill/history endpoint.
    """
    to_date = datetime.now()
    from_date = to_date - timedelta(days=days)
    
    payload = {
        "symbol": symbol,
        "resolution": res,
        "fromDate": from_date.strftime("%Y-%m-%d"),
        "toDate": to_date.strftime("%Y-%m-%d"),
        "dateFormat": 1,
        "contFlag": 1
    }
    
    print(f"\n[SINGLE SYMBOL] Triggering Backfill for {symbol} (Past {days} days, {res} resolution)")
    
    try:
        response = api.http.post(
            f"{API_BASE_URL}/api/Backfill/history",
            json=payload,
            timeout=300
        )
        response.raise_for_status()
        
        result = response.json()
        print("\n✅ Backfill Completed Successfully!")
        print(f"Total Bars Inserted: {result.get('totalBarsInserted', 'N/A')}")
        print(f"Status: {result.get('status', 'Success')}")
            
    except Exception as e:
        print(f"\n❌ Single Symbol Backfill Failed: {e}")
        if hasattr(e, 'response') and e.response is not None:
            print(f"Response Body: {e.response.text}")


def trigger_option_chain_backfill(api: PlatformApiClient, underlying: str, days: int, strikes: int, res: str):
    """
    Backfills historical data for an entire Option Chain based on the underlying.
    Uses the /api/Options/history/backfill endpoint.
    """
    from core.data_engine import DataEngine
    
    print(f"\n[OPTION CHAIN] Fetching live spot price for {underlying} to determine ATM strike...")
    try:
        engine = DataEngine()
        if underlying == 'BANKNIFTY':
            spot_symbol = "NSE:NIFTYBANK-INDEX"
        elif underlying == 'NIFTY':
            spot_symbol = "NSE:NIFTY50-INDEX"
        elif underlying == 'SENSEX':
            spot_symbol = "BSE:SENSEX-INDEX"
        else:
            raise Exception("Unsupported underlying")
        tick = engine.get_latest_quote(spot_symbol)
        if not tick:
            raise Exception(f"Could not fetch spot price for {spot_symbol}")
        spot_price = tick.last_traded_price
        print(f"       -> Live Spot Price: {spot_price}")
    except Exception as e:
        print(f"\n❌ Failed to fetch live price for {underlying}: {e}")
        return

    to_utc = datetime.now(timezone.utc)
    from_utc = to_utc - timedelta(days=days)
    
    payload = {
        "exchange": "BSE" if underlying == "SENSEX" else "NSE",
        "underlying": underlying,
        "expiryDate": None,
        "underlyingPrice": spot_price,
        "atmStrike": None,
        "strikeCountEachSide": strikes,
        "strikeStep": 100 if "BANK" in underlying else 50,
        "resolution": res,
        "fromUtc": from_utc.isoformat().replace("+00:00", "Z"),
        "toUtc": to_utc.isoformat().replace("+00:00", "Z"),
        "includeCalls": True,
        "includePuts": True
    }
    
    print(f"\n[OPTION CHAIN] Triggering Chain Backfill for {underlying} (Past {days} days, {res} resolution, +/- {strikes} strikes)")
    
    try:
        response = api.http.post(
            f"{API_BASE_URL}/api/Options/history/backfill",
            json=payload,
            timeout=300
        )
        response.raise_for_status()
        
        result = response.json()
        print("\n[SUCCESS] Option Chain Backfill Request Processed!")
        print(f"Message: {result.get('message', '')}")
        print(f"Total Contracts Fetched: {result.get('totalContractsFetched', 0)}")
        print(f"Total Bars Inserted: {result.get('totalCandlesInserted', 0)}")
        print("Symbols Processed:")
        symbols = result.get('symbols', [])
        if not symbols:
            print("  (None)")
        for sym in symbols:
            print(f"  - {sym}")
            
    except Exception as e:
        print(f"\n[ERROR] Option Chain Backfill Failed: {e}")
        if hasattr(e, 'response') and e.response is not None:
            print(f"Response Body: {e.response.text}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Unified Database Backfill CLI for AlgoTrading Engine",
        formatter_class=argparse.RawTextHelpFormatter
    )
    
    subparsers = parser.add_subparsers(dest="mode", required=True, help="Mode of operation")
    
    # 1. Single Symbol Mode
    parser_single = subparsers.add_parser("single", help="Backfill a single specific symbol (Equity/Index/Option)")
    parser_single.add_argument("--symbol", type=str, required=True, help="Exact broker symbol (e.g., NSE:RELIANCE-EQ or NSE:BANKNIFTY24MAY50000CE)")
    parser_single.add_argument("--days", type=int, default=5, help="Number of days to backfill (default: 5)")
    parser_single.add_argument("--res", type=str, default="1m", help="Resolution (e.g., 1m, 5m, 15m, 1D) (default: 1m)")
    
    # 2. Option Chain Mode
    parser_chain = subparsers.add_parser("chain", help="Backfill an entire Option Chain for an underlying index")
    parser_chain.add_argument("--underlying", type=str, required=True, help="Underlying Index Name (e.g., BANKNIFTY, NIFTY, SENSEX)")
    parser_chain.add_argument("--strikes", type=int, default=3, help="Number of strikes on each side of ATM (default: 3)")
    parser_chain.add_argument("--days", type=int, default=5, help="Number of days to backfill (default: 5)")
    parser_chain.add_argument("--res", type=str, default="5m", help="Resolution (e.g., 1m, 5m, 15m) (default: 5m)")

    args = parser.parse_args()
    
    # Initialize the core API Client (handles auth/sessions automatically)
    api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)
    
    if args.mode == "single":
        trigger_single_symbol_backfill(api, args.symbol, args.res, args.days)
    elif args.mode == "chain":
        trigger_option_chain_backfill(api, args.underlying, args.days, args.strikes, args.res)
