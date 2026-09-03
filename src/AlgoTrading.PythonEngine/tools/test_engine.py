import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))
from core.data_engine import DataEngine
from datetime import datetime, timedelta

engine = DataEngine()
end_time = datetime.now()
start_time = end_time - timedelta(days=1)
bars = engine.get_historical_bars("NSE:NIFTYBANK-INDEX", "5m", start_time.strftime("%Y-%m-%d"), end_time.strftime("%Y-%m-%d"))
if bars:
    print(f"Engine first bar: {bars[0].timestamp_start}")
    print(f"Engine last bar: {bars[-1].timestamp_start}")
