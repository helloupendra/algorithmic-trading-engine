import sys
import os
import argparse
from datetime import datetime, timezone, timedelta

# Ensure we can import from core
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))
from core.api_client import build_session, PlatformApiClient
from core.config import API_BASE_URL, VERIFY_SSL

def trigger_options_backfill(underlying: str, days: int, strike_count: int, resolution: str):
    """
    Triggers the C# API's Options History Backfill endpoint.
    It automatically hits Fyers and stores the data in the Postgres/Timescale database.
    """
    api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)
    
    # Calculate date range
    to_utc = datetime.now(timezone.utc)
    from_utc = to_utc - timedelta(days=days)
    
    payload = {
        "exchange": "NSE",
        "underlying": underlying,
        "expiryDate": None, # Null means the C# API will auto-resolve the current active expiry
        "underlyingPrice": None, # Null means it will auto-fetch the live spot price
        "atmStrike": None,
        "strikeCountEachSide": strike_count,
        "strikeStep": 100 if "BANK" in underlying else 50,
        "resolution": resolution,
        "fromUtc": from_utc.isoformat().replace("+00:00", "Z"),
        "toUtc": to_utc.isoformat().replace("+00:00", "Z"),
        "includeCalls": True,
        "includePuts": True
    }
    
    print(f"Triggering Backfill for {underlying} (Past {days} days, {resolution} resolution, +/- {strike_count} strikes)")
    
    try:
        # The PlatformApiClient automatically handles the admin login and Bearer token!
        response = api.http.post(
            f"{API_BASE_URL}/api/Options/history/backfill",
            json=payload,
            timeout=300 # Backfill might take a while, so give it a long timeout
        )
        response.raise_for_status()
        
        result = response.json()
        print("\nBackfill Completed Successfully!")
        print(f"Total Requests Made: {result.get('totalRequestsMade')}")
        print(f"Total Bars Inserted: {result.get('totalBarsInserted')}")
        print("Symbols Processed:")
        for sym in result.get('processedSymbols', []):
            print(f"  - {sym}")
            
    except Exception as e:
        print(f"Backfill Failed: {e}")
        if hasattr(e, 'response') and e.response is not None:
            print(f"Response: {e.response.text}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Trigger Options History Backfill via C# API")
    parser.add_argument("--underlying", type=str, default="BANKNIFTY", help="e.g. BANKNIFTY or NIFTY")
    parser.add_argument("--days", type=int, default=5, help="Number of days to backfill")
    parser.add_argument("--strikes", type=int, default=5, help="Number of strikes on each side of ATM")
    parser.add_argument("--res", type=str, default="1m", help="Resolution (e.g. 1m, 5m, 15m)")
    
    args = parser.parse_args()
    trigger_options_backfill(args.underlying, args.days, args.strikes, args.res)
