import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))
from core.api_client import PlatformApiClient
from core.config import API_BASE_URL
from core.data_engine import DataEngine
from datetime import datetime, timedelta

api = PlatformApiClient(API_BASE_URL, verify_ssl=False)
engine = DataEngine()

end_time = datetime.now()
start_time = end_time - timedelta(days=15)

bars = engine.get_historical_bars(
    symbol="NSE:NIFTYBANK-INDEX",
    resolution="5",
    start_date=start_time.strftime("%Y-%m-%d"),
    end_date=end_time.strftime("%Y-%m-%d")
)
print(f"Engine got {len(bars) if bars else 0} bars for res 5")

bars_5m = engine.get_historical_bars(
    symbol="NSE:NIFTYBANK-INDEX",
    resolution="5m",
    start_date=start_time.strftime("%Y-%m-%d"),
    end_date=end_time.strftime("%Y-%m-%d")
)
print(f"Engine got {len(bars_5m) if bars_5m else 0} bars for res 5m")
