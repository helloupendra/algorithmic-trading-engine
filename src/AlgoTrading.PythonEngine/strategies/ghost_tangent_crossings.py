import math
from typing import Any, Dict, List, Optional, Tuple

from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal, DataRequirement


class GhostTangentCrossingsStrategy(BaseStrategy):
    """
    Port of the "Ghost Tangent Crossings" Pine script: ZigZag pivots joined by an
    ellipse whose tangent becomes a dynamic trigger line; a close through that
    line fires a directional signal.
    """
    name = "GhostTangentCrossings"
    description = (
        "Directional breakout strategy that tracks ZigZag pivots on the 5-minute index chart and draws an "
        "ellipse-tangent trigger line from the last swing; a confirmed or early ('ghost') close through the "
        "line buys the ATM call on an upside break or the ATM put on a downside break. Profits when the "
        "break follows through; there is no built-in exit, so use the run's stop-loss/target. Needs "
        "5-minute index bars (about 15 days of history for warmup) plus live spot ticks."
    )
    category = "Directional"
    legs_summary = "Buy ATM CE on an up-break, or Buy ATM PE on a down-break"
    default_lots = 1
    default_params: Dict[str, Any] = {
        "pivot_forward": 25,
        "pivot_type": "Wick",
        "use_ghost_signals": True,
    }

    @classmethod
    def get_data_requirements(cls) -> List[DataRequirement]:
        return [
            DataRequirement(symbol_type="index", resolution="5m")
        ]

    def __init__(self, params: Dict[str, Any] = None):
        params = params or {}
        self.params = params
        self.pivot_forward = int(params.get("pivot_forward", self.default_params["pivot_forward"]))
        self.pivot_type = str(params.get("pivot_type", self.default_params["pivot_type"]))
        self.use_ghost_signals = bool(params.get("use_ghost_signals", self.default_params["use_ghost_signals"]))
        # Lots per leg; the runner converts BUY/SELL into a one-leg OPEN_GROUP of this size.
        self.lots = self.lots_from(params, self.default_lots)

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "ph_back": self.pivot_forward,
            "pl_back": self.pivot_forward,
            "last_up_start": None,
            "last_up_end": None,
            "last_down_start": None,
            "last_down_end": None,
            "last_high": None,
            "last_low": None,
            "polarity": None,
            
            # Pivot tracking
            "ph_current": None,
            "ph_current_idx": None,
            "pl_current": None,
            "pl_current_idx": None,
            
            "bar_index": 0,
            
            # Track when we fired signals to avoid duplicates
            "last_signal_idx": None
        }

    def _get_high(self, b: Any) -> float:
        return b.high if self.pivot_type == "Wick" else max(b.open, b.close)

    def _get_low(self, b: Any) -> float:
        return b.low if self.pivot_type == "Wick" else min(b.open, b.close)

    def _get_pivot(self, bars: List[Any], bar_index: int, back: int, forward: int, is_high: bool) -> Tuple[Optional[float], Optional[int], bool]:
        if len(bars) <= back + forward:
            return None, None, False
            
        pivot_idx = len(bars) - 1 - forward
        
        if is_high:
            pivot_val = self._get_high(bars[pivot_idx])
            for i in range(1, back + 1):
                if self._get_high(bars[pivot_idx - i]) > pivot_val:
                    return None, None, False
            for i in range(1, forward + 1):
                if self._get_high(bars[pivot_idx + i]) >= pivot_val:
                    return None, None, False
        else:
            pivot_val = self._get_low(bars[pivot_idx])
            for i in range(1, back + 1):
                if self._get_low(bars[pivot_idx - i]) < pivot_val:
                    return None, None, False
            for i in range(1, forward + 1):
                if self._get_low(bars[pivot_idx + i]) <= pivot_val:
                    return None, None, False
                    
        absolute_idx = bar_index - forward
        return pivot_val, absolute_idx, True

    def _generate_ellipse(self, start_x: int, end_x: int, start_y: float, end_y: float) -> List[Dict[str, Any]]:
        points = []
        a = end_x - start_x
        b = end_y - start_y
        x_prev = None
        
        if a > 1:
            for i in range(91):  # 0 to 90 degrees
                rad = math.radians(i)
                try:
                    new_x = int(a * math.cos(rad))
                    y = b * math.sin(rad)
                except TypeError as e:
                    print(f"DEBUG TypeError in _generate_ellipse:")
                    print(f"a: {type(a)} {a}")
                    print(f"b: {type(b)} {b}")
                    raise e
                if x_prev != new_x:
                    points.append({"index": start_x + new_x, "price": start_y + y})
                x_prev = new_x
            points.append({"index": start_x, "price": end_y})
        else:
            points.insert(0, {"index": end_x, "price": start_y})
            points.append({"index": start_x, "price": end_y})
            
        return points

    def _ellipse_slope(self, start_x: int, end_x: int, start_y: float, end_y: float, ellipse_points: List[Dict[str, Any]]) -> Tuple[Dict[str, Any], float]:
        if len(ellipse_points) > 2:
            dy = []
            for i in range(len(ellipse_points) - 1):
                delta = abs(ellipse_points[i + 1]["price"] - ellipse_points[i]["price"])
                dy.append(delta)
                
            centers = []
            idx = []
            for i in range(1, len(dy)):
                idx.append(i)
                left = sum(dy[:i + 1])
                right = sum(dy[i - 1:])
                centers.append(abs(left - right))
                
            if not centers:
                 return ellipse_points[0], 0.0
                 
            min_center = min(centers)
            min_index = centers.index(min_center)
            mid = idx[min_index]
            tangent = ellipse_points[mid]
            
            a = end_x - start_x
            b = end_y - start_y
            x = tangent["index"] - start_x
            y = tangent["price"] - start_y
            
            if y != 0 and a != 0:
                slope = -( (b**2) * x ) / ( (a**2) * y )
            else:
                slope = 0.0
            return tangent, slope
        else:
            if not ellipse_points:
                 return {"index": start_x, "price": start_y}, 0.0
            tangent = ellipse_points[0]
            slope = -(ellipse_points[-1]["price"] - ellipse_points[0]["price"])
            return tangent, slope

    def _check_b(self, bar_index: int, bars: List[Any], start_x: int, start_y: float, forward_length: int, slope: float, polarity: bool) -> Tuple[bool, Optional[float], Optional[int]]:
        i = forward_length - 1
        found = False
        
        start_idx = bar_index - start_x
        current_idx = start_idx
        end_price = None
        found_idx = None
        
        while not found and current_idx >= 0:
            i += 1
            try:
                check_price = start_y + slope * i
            except TypeError as e:
                print(f"DEBUG TypeError in _check_b:")
                print(f"start_y: {type(start_y)} {start_y}")
                print(f"slope: {type(slope)} {slope}")
                print(f"i: {type(i)} {i}")
                raise e
            current_idx = start_idx - i
            
            if current_idx < 0 or current_idx >= len(bars):
                break
                
            # bars array where 0 is oldest, -1 is newest
            # `current_idx` in Pine is 0 for current bar, 1 for previous.
            # So `len(bars) - 1 - current_idx` gives the actual list index.
            list_idx = len(bars) - 1 - current_idx
            if list_idx < 0:
                break
                
            bar = bars[list_idx]
            
            if polarity:
                end_price = bar.close
                if end_price < check_price:
                    found = True
            else:
                end_price = bar.close
                if end_price > check_price:
                    found = True
                    
            if found:
                found_idx = bar_index - current_idx
                
        return found, end_price, found_idx

    def _evaluate_zig_zag(self, state: Dict[str, Any], bars: List[Any], bar_index: int, start_x: int, end_x: int, start_y: float, end_y: float, polarity: bool, inp: StrategyInput, is_ghost: bool = False) -> Optional[StrategySignal]:
        ellipse_points = self._generate_ellipse(start_x, end_x, start_y, end_y)
        tangent, slope = self._ellipse_slope(start_x, end_x, start_y, end_y, ellipse_points)
        
        current_trigger = tangent["price"] + slope * (bar_index - tangent["index"])
        if polarity:
            state["target_sell_trigger"] = current_trigger
        else:
            state["target_buy_trigger"] = current_trigger
        
        length = end_x - start_x
        back_length = tangent["index"] - start_x
        forward_length = length - back_length
        
        found, found_price, found_idx = self._check_b(bar_index, bars, tangent["index"], tangent["price"], forward_length, slope, polarity)
        
        if found:
            # Avoid firing the exact same signal multiple times
            if state["last_signal_idx"] == found_idx:
                return None
            state["last_signal_idx"] = found_idx
            
            # Polarity True (Bullish ZigZag) -> break is bearish -> SHORT signal
            # Polarity False (Bearish ZigZag) -> break is bullish -> LONG signal
            signal_type = "SELL" if polarity else "BUY"
            reason = f"Ghost Tangent Break {'(Ghost)' if is_ghost else '(Confirmed)'} detected at bar {found_idx} for {inp.underlying}"
            
            return StrategySignal(
                strategy_name=self.name,
                signal_type=signal_type,
                timestamp_utc=inp.timestamp_utc,
                reason=reason,
                symbol=inp.underlying,
                price=found_price
            )
        return None

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals = []
        
        bars = inp.bars.get("5m", {}).get("index", [])
        if not bars:
            return signals

        # Only increment bar_index if it's a new bar!
        current_bar_time = getattr(bars[-1], "timestamp_utc", None)
        last_processed_time = state.get("last_processed_bar_time")
        
        if current_bar_time != last_processed_time:
            state["bar_index"] += 1
            state["last_processed_bar_time"] = current_bar_time
            # Clear targets on a new bar, they will be recalculated if conditions are met
            state["target_buy_trigger"] = None
            state["target_sell_trigger"] = None
            
        bar_index = state["bar_index"]

        if len(bars) < self.pivot_forward * 2 + 1:
            return signals

        ph_back = state["ph_back"]
        pl_back = state["pl_back"]

        ph_val, ph_idx, new_ph = self._get_pivot(bars, bar_index, ph_back, self.pivot_forward, True)
        pl_val, pl_idx, new_pl = self._get_pivot(bars, bar_index, pl_back, self.pivot_forward, False)

        if new_ph:
            state["ph_current"] = ph_val
            state["ph_current_idx"] = ph_idx
        if new_pl:
            state["pl_current"] = pl_val
            state["pl_current_idx"] = pl_idx
            
        ph_c_idx = state["ph_current_idx"]
        pl_c_idx = state["pl_current_idx"]
        
        polarity_up = (ph_c_idx is not None and pl_c_idx is not None and ph_c_idx > pl_c_idx)
        polarity_down = (ph_c_idx is not None and pl_c_idx is not None and ph_c_idx < pl_c_idx)
        
        up_wait = not state["polarity"] if state["polarity"] is not None else True
        down_wait = state["polarity"] if state["polarity"] is not None else True

        # Process confirmed High Pivot
        if new_ph and polarity_up and (state["last_up_start"] is None or state["last_up_start"] < pl_c_idx) and up_wait:
            connect = (state["last_down_end"] is not None) and (pl_c_idx == state["last_down_end"])
            if state["last_down_end"] is None:
                connect = True
                
            start_x = pl_c_idx if connect else state["last_down_end"]
            end_x = ph_c_idx
            start_y = state["ph_current"]
            end_y = state["pl_current"] if connect else state["last_low"]
            
            state["last_up_start"] = start_x
            state["last_up_end"] = end_x
            state["last_high"] = start_y
            state["polarity"] = True
            
            sig = self._evaluate_zig_zag(state, bars, bar_index, start_x, end_x, start_y, end_y, True, inp)
            if sig: signals.append(sig)

        # Process confirmed Low Pivot
        if new_pl and polarity_down and (state["last_down_start"] is None or state["last_down_start"] < ph_c_idx) and down_wait:
            connect = (state["last_up_end"] is not None) and (ph_c_idx == state["last_up_end"])
            if state["last_up_end"] is None:
                connect = True
                
            start_x = ph_c_idx if connect else state["last_up_end"]
            end_x = pl_c_idx
            start_y = state["pl_current"]
            end_y = state["ph_current"] if connect else state["last_high"]
            
            state["last_down_start"] = start_x
            state["last_down_end"] = end_x
            state["last_low"] = start_y
            state["polarity"] = False
            
            sig = self._evaluate_zig_zag(state, bars, bar_index, start_x, end_x, start_y, end_y, False, inp)
            if sig: signals.append(sig)

        # Update dynamic lookbacks (capped between 5 and 500 per Pine script)
        if state["last_down_end"] is not None:
            state["ph_back"] = max(min((bar_index - state["last_down_end"] - pl_back + 1), 500), 5)
        if state["last_up_end"] is not None:
            state["pl_back"] = max(min((bar_index - state["last_up_end"] - ph_back + 1), 500), 5)

        # --- Ghost Signal Logic ---
        if self.use_ghost_signals:
            if up_wait and not new_ph and state["pl_current_idx"] is not None:
                ghost_up_connect = (state["last_down_end"] is not None) and (state["pl_current_idx"] == state["last_down_end"])
                if state["last_down_end"] is None:
                    ghost_up_connect = True
                    
                ghost_up_start_x = state["pl_current_idx"] if ghost_up_connect else state["last_down_end"]
                
                # Find max price since last pivot low
                bars_since_pl = bar_index - state["pl_current_idx"]
                if bars_since_pl > 0 and bars_since_pl < len(bars):
                    recent_bars = bars[-bars_since_pl:]
                    max_bar = max(recent_bars, key=lambda b: self._get_high(b))
                    ghost_up_start_y = self._get_high(max_bar)
                    
                    ghost_up_since = recent_bars.index(max_bar)
                    idx_in_full = (len(bars) - bars_since_pl) + ghost_up_since
                    ghost_up_end_x = bar_index - (len(bars) - 1 - idx_in_full)
                    
                    ghost_up_end_y = state["pl_current"] if ghost_up_connect else state["last_low"]
                    
                    if ghost_up_start_x < ghost_up_end_x:
                        sig = self._evaluate_zig_zag(state, bars, bar_index, ghost_up_start_x, ghost_up_end_x, ghost_up_start_y, ghost_up_end_y, True, inp, is_ghost=True)
                        if sig: signals.append(sig)
                        
            if down_wait and not new_pl and state["ph_current_idx"] is not None:
                ghost_down_connect = (state["last_up_end"] is not None) and (state["ph_current_idx"] == state["last_up_end"])
                if state["last_up_end"] is None:
                    ghost_down_connect = True
                    
                ghost_down_start_x = state["ph_current_idx"] if ghost_down_connect else state["last_up_end"]
                
                bars_since_ph = bar_index - state["ph_current_idx"]
                if bars_since_ph > 0 and bars_since_ph < len(bars):
                    recent_bars = bars[-bars_since_ph:]
                    min_bar = min(recent_bars, key=lambda b: self._get_low(b))
                    ghost_down_start_y = self._get_low(min_bar)
                    
                    ghost_down_since = recent_bars.index(min_bar)
                    idx_in_full = (len(bars) - bars_since_ph) + ghost_down_since
                    ghost_down_end_x = bar_index - (len(bars) - 1 - idx_in_full)
                    
                    ghost_down_end_y = state["ph_current"] if ghost_down_connect else state["last_high"]
                    
                    if ghost_down_start_x < ghost_down_end_x:
                        sig = self._evaluate_zig_zag(state, bars, bar_index, ghost_down_start_x, ghost_down_end_x, ghost_down_start_y, ghost_down_end_y, False, inp, is_ghost=True)
                        if sig: signals.append(sig)

        return signals
