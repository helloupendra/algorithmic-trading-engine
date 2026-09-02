import sys
import os
import argparse
import pandas as pd
from datetime import datetime, timezone

# Ensure we can import from core
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..')))
from core.api_client import build_session
from core.config import API_BASE_URL, VERIFY_SSL, require_app_id

def get_active_session():
    """Fetch the active Fyers session token from the local API."""
    url = f"{API_BASE_URL}/api/auth/session"
    http = build_session()
    
    print(f"Fetching session from {url}...")
    response = http.get(url, verify=VERIFY_SSL, timeout=10)
    response.raise_for_status()
    data = response.json()
    
    if data.get("isAuthenticated") and data.get("accessToken"):
        return data["accessToken"]
    
    raise RuntimeError("C# API is not authenticated with Fyers. Please login via the dashboard.")

def download_historical_data(symbol, resolution, start_date, end_date):
    """
    Download historical OHLCV data from Fyers API using the unified DataEngine.
    resolution: '1', '5', '10', '15', '30', '60', '1D'
    start_date, end_date: 'YYYY-MM-DD'
    """
    from core.data_engine import DataEngine
    
    print(f"Requesting data for {symbol} ({resolution}m) from {start_date} to {end_date} via DataEngine...")
    engine = DataEngine()
    bars = engine.get_historical_bars(symbol, resolution, start_date, end_date)
    
    if not bars:
        print("No data returned for the given range.")
        return
        
    print(f"Received {len(bars)} candles.")
    
    # Convert to DataFrame
    data = []
    for b in bars:
        data.append({
            'datetime': b.timestamp_start,
            'open': b.open,
            'high': b.high,
            'low': b.low,
            'close': b.close,
            'volume': b.volume
        })
        
    df = pd.DataFrame(data)
    
    # Ensure data directory exists
    save_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..', 'data', 'historical_bars'))
    os.makedirs(save_dir, exist_ok=True)
    
    # Save to CSV
    filename = f"{symbol.replace(':', '_').replace('-', '_')}_{resolution}m_{start_date}_to_{end_date}.csv"
    filepath = os.path.join(save_dir, filename)
    
    df.to_csv(filepath, index=False)
    print(f"Data saved successfully to: {filepath}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Download Historical Data from FYERS")
    parser.add_argument("--symbol", required=True, help="e.g., NSE:NIFTYBANK-INDEX or BSE:SENSEX-INDEX")
    parser.add_argument("--res", required=True, help="Resolution: 1, 5, 15, 30, 60, 1D")
    parser.add_argument("--start", required=True, help="Start date (YYYY-MM-DD)")
    parser.add_argument("--end", required=True, help="End date (YYYY-MM-DD)")
    
    args = parser.parse_args()
    
    try:
        download_historical_data(args.symbol, args.res, args.start, args.end)
    except Exception as e:
        print(f"Error: {e}")
