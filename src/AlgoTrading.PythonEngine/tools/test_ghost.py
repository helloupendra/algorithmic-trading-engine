import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))
from core.api_client import PlatformApiClient
from core.config import API_BASE_URL
from core.data_engine import DataEngine
from datetime import datetime, timedelta
from strategies.ghost_tangent_crossings import GhostTangentCrossingsStrategy
from strategies.base_strategy import StrategyInput, BarFrame

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

warmup_bars = bars[-500:] if len(bars) > 500 else bars

strategy = GhostTangentCrossingsStrategy({"pivot_forward": 25})
state = strategy.initialize_state()

cumulative_frames = []
for b in warmup_bars:
    frame = BarFrame(
        symbol=b.symbol,
        resolution=b.resolution,
        timestamp_utc=b.timestamp_start.isoformat().replace("+00:00", "Z"),
        open=b.open,
        high=b.high,
        low=b.low,
        close=b.close,
        volume=b.volume
    )
    cumulative_frames.append(frame)
    inp = StrategyInput(
        mode="LivePaper",
        timestamp_utc=frame.timestamp_utc,
        underlying="BANKNIFTY",
        spot_price=frame.close,
        atm_strike=int(round(frame.close / 100) * 100),
        contracts={},
        bars={"5m": {"index": list(cumulative_frames)}},
        metadata={"source": "warmup"}
    )
    strategy.on_bar(state, inp)

print(f"Final state polarity: {state.get('polarity')}")
print(f"Final state target_buy: {state.get('target_buy_trigger')}")
print(f"Final state target_sell: {state.get('target_sell_trigger')}")
print(f"Final ph_current: {state.get('ph_current')}, pl_current: {state.get('pl_current')}")

# Simulate live tick
raw_idx = api.get_recent_bars("NSE:NIFTYBANK-INDEX", "5m", 500)
live_bars = [BarFrame(
    symbol=b.get("symbol"),
    resolution=b.get("resolution"),
    timestamp_utc=str(b.get("barStartUtc", "")),
    open=float(b.get("open", 0.0)),
    high=float(b.get("high", 0.0)),
    low=float(b.get("low", 0.0)),
    close=float(b.get("close", 0.0)),
    volume=float(b.get("volumeDelta", 0.0))
) for b in raw_idx]

last_processed = state.get("last_processed_bar_time")
current = live_bars[-1].timestamp_utc
print(f"Warmup last processed time: {last_processed}")
print(f"Live current bar time: {current}")

inp_live = StrategyInput(
    mode="LivePaper",
    timestamp_utc="test",
    underlying="BANKNIFTY",
    spot_price=1000,
    atm_strike=1000,
    contracts={},
    bars={"5m": {"index": live_bars}},
    metadata={"source": "live"}
)
strategy.on_bar(state, inp_live)

print(f"After live tick -> target_sell: {state.get('target_sell_trigger')}")
