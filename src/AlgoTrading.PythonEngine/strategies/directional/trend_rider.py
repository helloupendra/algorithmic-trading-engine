"""
strategies/directional/trend_rider.py

Trend Rider: buys an option in the direction of a day that is trending and stays in while it
does. Built from the Ghost research of 13 Nov 2026: Ghost's own entries did no better than random
ones, its 20-point target cut every trend day short, and most of its losses came on sideways days.
So this strategy:

  - buys the strike `itm_steps` strikes in the money (1 by default; 0 is ATM): on recorded
    premiums (Jan 2024 - Sep 2026) every step deeper paid more a trade, as less of the premium is
    time value;

  - enters when a closed 5-minute candle breaks the first `or_minutes` range and sits on the same
    side of the day's open (CE above it, PE below), between `first_entry` and `last_entry`;
  - skips the days that tend to go sideways: the day after a wide one (yesterday's range above
    `skip_after_wide` x the average range of the last `adr_days` sessions), a break while today's
    range is still below `min_range_frac` of that average, a gap of more than
    `max_gap_against_pct` against the trade and, when `max_prev_range` is set, any day whose
    previous session was not narrower than that fraction. That last filter keeps it out of
    every day of a fast, wide-range market (it took no trade in the 28 Oct - 12 Nov 2026 rally);
    `max_prev_range: 0` turns it off;
  - has no target: it closes when a candle closes back across the day's open;
  - after an exit, enters again in the same direction only on a new day high (CE) or low (PE),
    up to `max_entries` a day, one position at a time.

No premium stop-loss is advised: on 1-minute premiums a 10-point stop cut the trend days it
lives on (research of 13-16 Nov 2026). A run can still set risk.leg.stopLossPoints and
targetPoints. The run's end-of-day square-off closes what is still open.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from functools import lru_cache
from typing import Any, Dict, List, Optional, Tuple

from strategies.base_strategy import (BarFrame, BaseStrategy, ContractRequirement, DataRequirement, StrategyInput,
                                      StrategySignal)
from strategies.index_exits import IndexExitRules

IST = timezone(timedelta(hours=5, minutes=30))
#: The default candle; `timeframe` picks another (TIMEFRAMES).
BAR_MINUTES = 5
TIMEFRAMES = (1, 2, 3, 5, 10, 15)
SESSION_FIRST, SESSION_CLOSE = "09:15", "15:30"
SESSION_LAST = "15:25"          # the last 5-minute candle; other timeframes compute their own
SESSION_MINUTES = 375
#: A remembered session counts towards the average range only when this share of its candles (60 of 75 at
#: 5 minutes) were seen; a window that starts mid-day would otherwise report a range that never happened.
FULL_SESSION_SHARE = 0.8
FULL_SESSION_BARS = 60


@lru_cache(maxsize=65536)
def _ist_text(stamp: str) -> Optional[datetime]:
    try:
        return datetime.fromisoformat(stamp.replace("Z", "+00:00")).astimezone(IST)
    except ValueError:
        return None


def _ist(stamp: Any) -> Optional[datetime]:
    # Cached: every call re-reads the whole window of candles, and parsing their stamps was most of its cost.
    return _ist_text(str(stamp))


def _compact(stamp: Any) -> str:
    return str(stamp).replace(":", "").replace("-", "")[:15]


class TrendRiderStrategy(BaseStrategy):
    """Buys the trend day's option on an opening-range break, holds while the day's open holds."""

    name = "TrendRider"
    requires_position_sync = True
    description = (
        "Directional option buying for trend days. On the 5-minute NIFTY chart it buys a call one strike in "
        "the money when a candle closes above the first 30 minutes' range and above the day's open, or the "
        "put on the mirror break, and holds with no target until a candle closes back across the day's open. "
        "It skips days likely to go sideways (after a wide day, before the range expands, against the gap, "
        "and by default unless yesterday was narrow; set max_prev_range to 0 to also trade fast, wide-range "
        "markets such as the Nov 2026 rally) and re-enters only on a new day extreme, at most 3 times a day. "
        "No premium stop-loss is advised: a 10-point stop cut the trend days it lives on. Research on recorded "
        "premiums (Jan 2024 - Sep 2026, 1-minute) was positive in each period but not proven (t below 1) and "
        "rests on a few big days; validate on paper before trusting it."
    )
    category = "Directional"
    legs_summary = ("Buy CE 1 strike ITM on an up-trend day, or Buy PE 1 strike ITM on a down-trend day "
                    "(itm_steps); one position at a time")
    default_lots = 1
    default_params: Dict[str, Any] = {
        # Strikes in the money for the option bought; 0 is ATM.
        "itm_steps": 1,
        "or_minutes": 30,
        "first_entry": "09:45",
        "last_entry": "14:30",
        "max_entries": 3,
        "adr_days": 10,
        "skip_after_wide": 1.3,
        "min_range_frac": 0.40,
        "max_gap_against_pct": 0.2,
        # Only trade after a session narrower than this fraction of the average range; 0 turns it off.
        "max_prev_range": 0.7,
        # The candle the rules read: 1m, 2m, 3m, 5m, 10m or 15m.
        "timeframe": "5m",
        # Exits on the index (strategies/index_exits.py): none by default.
        "index_exits": None,
    }
    # Twelve sessions of 5-minute candles: the average range needs ten complete ones before today.
    warmup_bars = 75 * 12

    def __init__(self, params: Dict[str, Any] = None):
        params = dict(params or {})
        p = {**self.default_params, **{k: v for k, v in params.items() if k in self.default_params}}
        self.params = {**params, **p}
        tf = str(p.get("timeframe") or "5m").strip().lower().rstrip("m")
        if not tf.isdigit() or int(tf) not in TIMEFRAMES:
            raise ValueError(f"timeframe must be one of {', '.join(f'{m}m' for m in TIMEFRAMES)}")
        self.bar_minutes = int(tf)
        self.resolution = f"{self.bar_minutes}m"
        self.session_last = (datetime(2000, 1, 1, 15, 30) - timedelta(minutes=self.bar_minutes)).strftime("%H:%M")
        self.full_session_bars = int(FULL_SESSION_SHARE * SESSION_MINUTES / self.bar_minutes)
        self.warmup_bars = (SESSION_MINUTES // self.bar_minutes) * 12
        if int(p["or_minutes"]) % self.bar_minutes:
            raise ValueError(f"or_minutes must be a whole number of {self.resolution} candles")
        self.index_exits = IndexExitRules.parse(p.get("index_exits"))
        try:
            self.or_bars = int(p["or_minutes"]) // self.bar_minutes
            self.max_entries = int(p["max_entries"])
            self.adr_days = int(p["adr_days"])
            self.skip_after_wide = float(p["skip_after_wide"])
            self.min_range_frac = float(p["min_range_frac"])
            self.max_gap_against_pct = float(p["max_gap_against_pct"])
            self.max_prev_range = float(p["max_prev_range"])
            self.itm_steps = int(p["itm_steps"])
        except (TypeError, ValueError) as exc:
            raise ValueError(f"TrendRider parameters: {exc}") from exc
        self.first_entry, self.last_entry = str(p["first_entry"]), str(p["last_entry"])
        if self.or_bars < 1 or self.max_entries < 1 or self.adr_days < 1:
            raise ValueError("or_minutes must be at least 5, max_entries and adr_days at least 1")
        if not 0 <= self.itm_steps <= 5:
            raise ValueError("itm_steps must be between 0 (ATM) and 5")
        self.lots = self.lots_from(params, self.default_lots)

    def get_data_requirements(self=None) -> List[DataRequirement]:
        # The catalogue asks the class; the runners ask a configured instance, which may read another timeframe.
        return [DataRequirement(symbol_type="index", resolution=getattr(self, "resolution", None) or "5m")]

    @classmethod
    def get_contract_requirements(cls, params: Optional[Dict[str, Any]] = None) -> List[ContractRequirement]:
        """The call and the put `itm_steps` strikes in the money (0: ATM), read as buy_ce / buy_pe."""
        raw = (params or {}).get("itm_steps", cls.default_params["itm_steps"])
        try:
            steps = int(raw)
        except (TypeError, ValueError):
            steps = int(cls.default_params["itm_steps"])
        moneyness = "itm" if steps > 0 else "atm"
        return [ContractRequirement(key="buy_ce", option_type="CE", moneyness=moneyness, steps=steps),
                ContractRequirement(key="buy_pe", option_type="PE", moneyness=moneyness, steps=steps)]

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "days": {},             # past sessions: date -> [open, high, low, close, candles seen]
            "today": None,
            "count": 0,             # entries today
            "last_side": None,
            "extreme": None,        # the day high (CE) / low (PE) when the last entry was made
            "busy_until": None,     # no new entry before this candle (HH:MM) after an exit
            "position": None,       # {"group_id", "direction", "symbol", "day"}
            "opened": 0,
            "last_eval": None,
            "skipped": {},
        }

    # ------------------------------------------------------------------ read --
    def _closed(self, inp: StrategyInput) -> List[Tuple[datetime, BarFrame]]:
        """Session candles that have closed: a replay hands only those, live hands a forming one too."""
        now = None if inp.mode == "OfflineReplay" else _ist(inp.timestamp_utc)
        out = []
        for frame in (inp.bars.get(self.resolution) or {}).get("index") or []:
            at = _ist(getattr(frame, "timestamp_utc", None))
            if at is None or not SESSION_FIRST <= at.strftime("%H:%M") <= self.session_last:
                continue
            if now is not None and at + timedelta(minutes=self.bar_minutes) > now:
                continue
            out.append((at, frame))
        return out

    def _remember_days(self, state: Dict[str, Any], closed: List[Tuple[datetime, BarFrame]], today: str) -> None:
        seen: Dict[str, List[float]] = {}
        for at, b in closed:
            d = at.date().isoformat()
            if d >= today:
                continue
            x = seen.get(d)
            if x is None:
                seen[d] = [b.open, b.high, b.low, b.close, 1]
            else:
                x[1] = max(x[1], b.high); x[2] = min(x[2], b.low); x[3] = b.close; x[4] += 1
        for d, row in seen.items():
            if d not in state["days"] or row[4] >= state["days"][d][4]:
                state["days"][d] = row
        for d in sorted(state["days"])[:-(self.adr_days + 5)]:
            del state["days"][d]

    def _context(self, state: Dict[str, Any], today: str) -> Tuple[Optional[float], Optional[List[float]]]:
        """The average range of the last `adr_days` complete sessions, and yesterday's session."""
        past = [d for d in sorted(state["days"]) if d < today]
        full = [d for d in past if state["days"][d][4] >= self.full_session_bars][-self.adr_days:]
        if len(full) < self.adr_days or not past:
            return None, None
        adr = sum(state["days"][d][1] - state["days"][d][2] for d in full) / len(full)
        return (adr if adr > 0 else None), state["days"][past[-1]]

    def _skip(self, state: Dict[str, Any], why: str) -> None:
        state["skipped"][why] = state["skipped"].get(why, 0) + 1

    # ------------------------------------------------------------------ run ---
    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        exit_now = self._tick_exit(state, inp)
        if exit_now is not None:
            return [exit_now]
        closed = self._closed(inp)
        if not closed:
            return []
        at, bar = closed[-1]
        today, hhmm = at.date().isoformat(), at.strftime("%H:%M")
        if state["today"] != today:
            state.update(today=today, count=0, last_side=None, extreme=None, busy_until=None)
        self._remember_days(state, closed, today)
        self._sync(state, inp, at)
        # Once per closed candle: live calls on every tick of the forming one.
        if state["last_eval"] == f"{today} {hhmm}" or (inp.metadata or {}).get("source") == "warmup":
            state["last_eval"] = f"{today} {hhmm}"
            return []
        state["last_eval"] = f"{today} {hhmm}"

        day = [b for a, b in closed if a.date().isoformat() == today]
        day_open = day[0].open
        position = state["position"]
        if position is not None and self.index_exits is not None and self.index_exits.bar_rules:
            why = self.index_exits.check_bar(position, [b for _, b in closed[-300:]], inp.underlying or "the index")
            if why is not None:
                state["position"] = None
                state["busy_until"] = (at + timedelta(minutes=self.bar_minutes)).strftime("%H:%M")
                return [self._close(position, inp, f"{self.name} {why}")]
        if position is not None:
            against = bar.close < day_open if position["direction"] == "BUY" else bar.close > day_open
            if not against:
                return []
            state["position"] = None
            state["busy_until"] = (at + timedelta(minutes=self.bar_minutes)).strftime("%H:%M")
            side = "call" if position["direction"] == "BUY" else "put"
            return [self._close(position, inp, f"NIFTY closed {bar.close:g} back across the day's open {day_open:g}: "
                                               f"the trend the {side} rode has given way")]
        return self._enter(state, inp, at, bar, day, day_open)

    def _tick_exit(self, state: Dict[str, Any], inp: StrategyInput) -> Optional[StrategySignal]:
        """The index stop, trail and time exit (index_exits), on every call: live, every tick of the spot."""
        rules = self.index_exits
        position = state.get("position")
        if rules is None or position is None or position.get("entry_spot") is None:
            return None
        meta = inp.metadata or {}
        if meta.get("source") == "warmup":
            return None
        open_groups = meta.get("open_groups")
        if open_groups is not None and position["group_id"] not in open_groups:
            return None                     # the run closed it; _sync frees the strategy on the next candle
        now = _ist(inp.timestamp_utc)
        why = rules.check_tick(position, float(inp.spot_price or 0), now, inp.underlying or "the index")
        if why is None:
            return None
        state["position"] = None
        if now is not None:
            # No new entry from the candle this exit falls in: the same wait as after the strategy's own exit.
            start = now.replace(minute=now.minute - now.minute % self.bar_minutes, second=0, microsecond=0)
            state["busy_until"] = (start + timedelta(minutes=self.bar_minutes)).strftime("%H:%M")
        return self._close(position, inp, f"{self.name} {why}")

    def _sync(self, state: Dict[str, Any], inp: StrategyInput, at: datetime) -> None:
        """A position the run closed (its stop-loss, the square-off) frees the strategy."""
        position = state["position"]
        if position is None:
            return
        open_groups = (inp.metadata or {}).get("open_groups")
        if open_groups is not None and position["group_id"] not in open_groups:
            state["position"] = None
            state["busy_until"] = (at + timedelta(minutes=self.bar_minutes)).strftime("%H:%M")

    def _enter(self, state: Dict[str, Any], inp: StrategyInput, at: datetime, bar: BarFrame,
               day: List[BarFrame], day_open: float) -> List[StrategySignal]:
        hhmm = at.strftime("%H:%M")
        if state["count"] >= self.max_entries or not self.first_entry <= hhmm <= self.last_entry:
            return []
        if state["busy_until"] and hhmm < state["busy_until"]:
            return []
        if len(day) <= self.or_bars:
            return []
        or_hi = max(b.high for b in day[:self.or_bars])
        or_lo = min(b.low for b in day[:self.or_bars])
        side = ("BUY" if bar.close > or_hi and bar.close > day_open else
                "SELL" if bar.close < or_lo and bar.close < day_open else None)
        if side is None:
            return []
        buy = side == "BUY"
        day_hi, day_lo = max(b.high for b in day), min(b.low for b in day)
        hi_lo = day_hi if buy else day_lo
        extreme = state["extreme"]
        fresh = state["last_side"] != side or extreme is None or (hi_lo > extreme if buy else hi_lo < extreme)
        new_extreme = bar.high >= day_hi if buy else bar.low <= day_lo
        if not (fresh and (state["count"] == 0 or state["last_side"] != side or new_extreme)):
            return []

        adr, prev = self._context(state, at.date().isoformat())
        if adr is None:
            self._skip(state, "no_history")
            return []
        prev_range = (prev[1] - prev[2]) / adr
        gap = (day_open - prev[3]) / prev[3] * 100 * (1 if buy else -1)
        if prev_range > self.skip_after_wide:
            self._skip(state, "after_wide_day")
            return []
        if self.max_prev_range and prev_range >= self.max_prev_range:
            self._skip(state, "yesterday_not_narrow")
            return []
        if (day_hi - day_lo) / adr < self.min_range_frac:
            self._skip(state, "range_too_small")
            return []
        if gap < -self.max_gap_against_pct:
            self._skip(state, "gap_against")
            return []

        contract = (inp.contracts or {}).get("buy_ce" if buy else "buy_pe")
        if (contract is None or not contract.symbol or contract.option_type.upper() != ("CE" if buy else "PE")
                or str(contract.expiry_date)[:10] < at.date().isoformat()):
            self._skip(state, "no_contract")
            return []
        state["opened"] += 1
        group_id = f"TR_{_compact(inp.timestamp_utc)}_{state['opened']:03d}"
        state["position"] = {"group_id": group_id, "direction": side, "symbol": contract.symbol, "day": at.date().isoformat(),
                             "entry_spot": float(bar.close), "adr": float(adr), "best": 0.0}
        state["count"] += 1
        state["last_side"] = side
        state["extreme"] = hi_lo
        context = {"or_high": or_hi, "or_low": or_lo, "day_open": day_open, "adr": round(adr, 2),
                   "prev_range_adr": round(prev_range, 3), "range_adr": round((day_hi - day_lo) / adr, 3),
                   "gap_pct": round(gap, 3), "entry_of_day": state["count"]}
        return [StrategySignal(
            strategy_name=self.name,
            signal_type="OPEN_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=(f"Trend Rider: NIFTY {bar.close:g} closed {'above' if buy else 'below'} the opening range "
                    f"({or_hi:g}/{or_lo:g}) and the day's open {day_open:g}; buying the {'call' if buy else 'put'}"),
            legs=[{"symbol": contract.symbol, "side": "BUY", "quantity": self.lots}],
            metadata={"group_id": group_id, "direction": side, "trend_rider": context},
        )]

    def _close(self, position: Dict[str, Any], inp: StrategyInput, reason: str) -> StrategySignal:
        return StrategySignal(
            strategy_name=self.name,
            signal_type="CLOSE_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=reason,
            legs=[{"symbol": position["symbol"], "side": "SELL", "quantity": self.lots}],
            metadata={"group_id": position["group_id"]},
        )
