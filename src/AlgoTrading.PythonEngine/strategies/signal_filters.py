"""
strategies/signal_filters.py

A market-context gate that sits between a strategy's signal and the order.

Why a gate and not a library every strategy calls: the filters have to apply to
strategies that already exist and to ones not written yet, without editing any
of them. A strategy answers "is my setup here?"; this answers "is this a market
worth taking it in?" - and those are different questions with different
lifetimes. The setup belongs to the strategy; the context belongs to the run.

So it is configured PER RUN, in parametersJson:

    "filters": {
      "resolution": "5m",
      "trade_window_ist": ["09:20", "15:05"],
      "min_volume_zscore": 1.0,
      "min_atr_percent": 0.05,
      "require_vwap_side": true,
      "ema_period": 20,
      "require_ema_side": true,
      "require_pattern": "auto"
    }

The same function runs in the live runner and in the replay, so a filter can be
measured before it is trusted: run the backtest with the block and without it
and compare. That is the whole point of putting it here rather than inside a
strategy - a filter you cannot switch off is a filter you cannot evaluate.

Every rejection carries the rule that rejected it and the numbers behind it, so
"why did it not trade today" has an answer that is not a guess.

Absent inputs never silently block. An index reports no volume, so a volume
rule on index bars would reject every signal forever; a rule that cannot be
evaluated stands aside and says so.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from datetime import time as dtime
from typing import Any, Dict, List, Optional, Sequence

from strategies import indicators


# ------------------------------------------------------------------ result --

@dataclass
class FilterVerdict:
    """Whether a signal may proceed, and the arithmetic that decided it."""
    allowed: bool
    blocked_by: Optional[str] = None
    reason: str = ""
    details: Dict[str, Any] = field(default_factory=dict)

    @property
    def blocked(self) -> bool:
        return not self.allowed


ALLOW = FilterVerdict(allowed=True, reason="no filters configured")


# ------------------------------------------------------------------ config --

@dataclass
class FilterConfig:
    """The rules of one run. Every field None/empty means "not enforced"."""
    resolution: Optional[str] = None
    bars_key: str = "index"

    trade_window_ist: Optional[tuple] = None      # ("09:20", "15:05")
    block_open_minutes: Optional[int] = None
    min_volume_zscore: Optional[float] = None
    volume_lookback: int = 20
    min_atr_percent: Optional[float] = None
    max_atr_percent: Optional[float] = None
    atr_period: int = 14
    require_vwap_side: bool = False
    ema_period: Optional[int] = None
    require_ema_side: bool = False
    rsi_period: int = 14
    min_rsi: Optional[float] = None
    max_rsi: Optional[float] = None
    require_pattern: Optional[Any] = None         # "auto" | name | [names]

    @property
    def any_rule(self) -> bool:
        return any([
            self.trade_window_ist, self.block_open_minutes,
            self.min_volume_zscore is not None,
            self.min_atr_percent is not None, self.max_atr_percent is not None,
            self.require_vwap_side,
            self.require_ema_side and self.ema_period,
            self.min_rsi is not None, self.max_rsi is not None,
            self.require_pattern,
        ])


def _as_float(value: Any) -> Optional[float]:
    try:
        return None if value is None else float(value)
    except (TypeError, ValueError):
        return None


def _as_int(value: Any, default: Optional[int] = None) -> Optional[int]:
    try:
        return default if value is None else int(value)
    except (TypeError, ValueError):
        return default


def parse_filters(run_params: Optional[Dict[str, Any]]) -> Optional[FilterConfig]:
    """
    Reads the run's "filters" object. Returns None when the run configures
    none, so the gate costs nothing for every strategy that does not use it.
    """
    raw = (run_params or {}).get("filters")

    # A filters object typed into the console's parameter grid can arrive as
    # JSON TEXT rather than an object, depending on who wrote the caller. Read
    # it either way: the alternative is a run that starts, trades, and enforces
    # nothing while the operator believes it is filtered.
    if isinstance(raw, str):
        try:
            raw = json.loads(raw)
        except (ValueError, TypeError):
            print(f"[FILTER] ignoring unreadable filters parameter: {raw!r}", flush=True)
            return None

    if not isinstance(raw, dict) or not raw:
        return None

    window = raw.get("trade_window_ist")
    parsed_window = None
    if isinstance(window, (list, tuple)) and len(window) == 2:
        parsed_window = (str(window[0]), str(window[1]))

    config = FilterConfig(
        resolution=str(raw["resolution"]) if raw.get("resolution") else None,
        bars_key=str(raw.get("bars_key") or "index"),
        trade_window_ist=parsed_window,
        block_open_minutes=_as_int(raw.get("block_open_minutes")),
        min_volume_zscore=_as_float(raw.get("min_volume_zscore")),
        volume_lookback=_as_int(raw.get("volume_lookback"), 20) or 20,
        min_atr_percent=_as_float(raw.get("min_atr_percent")),
        max_atr_percent=_as_float(raw.get("max_atr_percent")),
        atr_period=_as_int(raw.get("atr_period"), 14) or 14,
        require_vwap_side=bool(raw.get("require_vwap_side")),
        ema_period=_as_int(raw.get("ema_period")),
        require_ema_side=bool(raw.get("require_ema_side")),
        rsi_period=_as_int(raw.get("rsi_period"), 14) or 14,
        min_rsi=_as_float(raw.get("min_rsi")),
        max_rsi=_as_float(raw.get("max_rsi")),
        require_pattern=raw.get("require_pattern") or None,
    )
    return config if config.any_rule else None


# --------------------------------------------------------------- direction --

def signal_direction(sig: Any) -> Optional[str]:
    """
    "bullish", "bearish", or None when the signal does not express a view.

    Taken from metadata when the strategy said so, otherwise inferred from the
    legs: buying a call or selling a put is bullish, the mirror is bearish. A
    multi-leg signal whose legs disagree (a straddle, a hedge) has no direction,
    and direction-aware rules stand aside rather than guess one.
    """
    direction = str((sig.metadata or {}).get("direction") or "").upper()
    if direction == "BUY":
        return "bullish"
    if direction == "SELL":
        return "bearish"

    views = set()
    for leg in (sig.legs or []):
        symbol = str(leg.get("symbol") or "").upper()
        side = str(leg.get("side") or "").upper()
        if symbol.endswith("CE"):
            kind = "CE"
        elif symbol.endswith("PE"):
            kind = "PE"
        else:
            continue
        if side == "BUY":
            views.add("bullish" if kind == "CE" else "bearish")
        elif side == "SELL":
            views.add("bearish" if kind == "CE" else "bullish")

    return views.pop() if len(views) == 1 else None


# ---------------------------------------------------------------- the gate --

def _pick_bars(config: FilterConfig, inp: Any) -> tuple:
    """The bar series the rules run on, and the resolution it came from."""
    by_resolution = inp.bars or {}
    if config.resolution and config.resolution in by_resolution:
        chosen = config.resolution
    else:
        # The strategy's own timeframe, whichever it declared, rather than a
        # number invented here.
        chosen = next(iter(by_resolution), None)
    if chosen is None:
        return [], None
    return list((by_resolution.get(chosen) or {}).get(config.bars_key) or []), chosen


def evaluate(
    config: Optional[FilterConfig],
    sig: Any,
    inp: Any,
    newest_bar_is_forming: bool,
) -> FilterVerdict:
    """
    Judge one opening signal against the run's market-context rules.

    `newest_bar_is_forming` is passed by the caller rather than guessed: the
    live runner is handed a bar that is still being built, the replay is handed
    bars only up to the moment it is replaying. Getting this wrong would judge
    a signal on a candle that has not happened yet, which is the classic way a
    backtest flatters itself.

    Only OPEN_GROUP is gated. A CLOSE_GROUP must never be blocked - refusing to
    let a strategy out of a position because the market looks quiet is how a
    filter turns into a loss.
    """
    if config is None or not config.any_rule:
        return ALLOW

    if str(getattr(sig, "signal_type", "")).upper() != "OPEN_GROUP":
        return FilterVerdict(True, reason="not an opening signal")

    bars, resolution = _pick_bars(config, inp)
    if newest_bar_is_forming and bars:
        bars = bars[:-1]

    if not bars:
        return FilterVerdict(
            False, "no_bars",
            f"no closed {config.resolution or resolution or ''} bars to judge the market on",
            {"resolution": resolution},
        )

    last = bars[-1]
    direction = signal_direction(sig)
    details: Dict[str, Any] = {"resolution": resolution, "direction": direction, "bars": len(bars)}

    for rule in (_rule_window, _rule_volume, _rule_atr, _rule_vwap, _rule_ema, _rule_rsi, _rule_pattern):
        verdict = rule(config, bars, last, direction, details)
        if verdict is not None:
            verdict.details = {**details, **verdict.details}
            return verdict

    return FilterVerdict(True, reason="passed every configured filter", details=details)


# ------------------------------------------------------------------ rules --
# Each returns a blocking verdict, or None to let the signal continue. A rule
# whose inputs are missing returns None: it cannot judge, so it does not.

def _rule_window(config, bars, last, direction, details) -> Optional[FilterVerdict]:
    if not config.trade_window_ist and config.block_open_minutes is None:
        return None

    moment = indicators.ist_of(last)
    if moment is None:
        return None
    details["bar_ist"] = moment.strftime("%H:%M")

    if config.trade_window_ist:
        try:
            start_h, start_m = (int(x) for x in config.trade_window_ist[0].split(":"))
            end_h, end_m = (int(x) for x in config.trade_window_ist[1].split(":"))
        except (ValueError, AttributeError):
            return None
        start, end = dtime(start_h, start_m), dtime(end_h, end_m)
        if not (start <= moment.time() <= end):
            return FilterVerdict(
                False, "trade_window_ist",
                f"{moment:%H:%M} IST is outside the trading window "
                f"{config.trade_window_ist[0]}-{config.trade_window_ist[1]}",
                {},
            )

    if config.block_open_minutes:
        session = indicators.session_bars(bars)
        if session:
            opened = indicators.ist_of(session[0])
            if opened is not None:
                elapsed = (moment - opened).total_seconds() / 60.0
                if elapsed < config.block_open_minutes:
                    return FilterVerdict(
                        False, "block_open_minutes",
                        f"only {elapsed:.0f} min into the session; the first "
                        f"{config.block_open_minutes} are skipped",
                        {"minutes_into_session": round(elapsed, 1)},
                    )
    return None


def _rule_volume(config, bars, last, direction, details) -> Optional[FilterVerdict]:
    if config.min_volume_zscore is None:
        return None
    z = indicators.volume_zscore(bars, config.volume_lookback)
    if z is None:
        # An index carries no volume. Saying so beats blocking every signal.
        details["volume_zscore"] = None
        return None
    details["volume_zscore"] = round(z, 2) if z != float("inf") else "inf"
    if z < config.min_volume_zscore:
        return FilterVerdict(
            False, "min_volume_zscore",
            f"volume is {z:.2f} SD above its {config.volume_lookback}-bar average, "
            f"below the {config.min_volume_zscore} required",
            {},
        )
    return None


def _rule_atr(config, bars, last, direction, details) -> Optional[FilterVerdict]:
    if config.min_atr_percent is None and config.max_atr_percent is None:
        return None
    value = indicators.atr_percent(bars, config.atr_period)
    if value is None:
        return None
    details["atr_percent"] = round(value, 4)
    if config.min_atr_percent is not None and value < config.min_atr_percent:
        return FilterVerdict(
            False, "min_atr_percent",
            f"range is {value:.3f}% of price, below the {config.min_atr_percent}% "
            f"needed for the move to be worth its costs",
            {},
        )
    if config.max_atr_percent is not None and value > config.max_atr_percent:
        return FilterVerdict(
            False, "max_atr_percent",
            f"range is {value:.3f}% of price, above the {config.max_atr_percent}% ceiling",
            {},
        )
    return None


def _rule_vwap(config, bars, last, direction, details) -> Optional[FilterVerdict]:
    if not config.require_vwap_side or direction is None:
        return None
    value = indicators.vwap(indicators.session_bars(bars))
    if value is None:
        return None
    close = float(last.close)
    details["vwap"] = round(value, 2)
    wrong = (direction == "bullish" and close < value) or (direction == "bearish" and close > value)
    if wrong:
        return FilterVerdict(
            False, "require_vwap_side",
            f"{direction} signal with close {close:.2f} on the wrong side of VWAP {value:.2f}",
            {},
        )
    return None


def _rule_ema(config, bars, last, direction, details) -> Optional[FilterVerdict]:
    if not (config.require_ema_side and config.ema_period) or direction is None:
        return None
    value = indicators.ema(indicators.closes(bars), config.ema_period)
    if value is None:
        return None
    close = float(last.close)
    details[f"ema{config.ema_period}"] = round(value, 2)
    wrong = (direction == "bullish" and close < value) or (direction == "bearish" and close > value)
    if wrong:
        return FilterVerdict(
            False, "require_ema_side",
            f"{direction} signal with close {close:.2f} on the wrong side of the "
            f"{config.ema_period} EMA {value:.2f}",
            {},
        )
    return None


def _rule_rsi(config, bars, last, direction, details) -> Optional[FilterVerdict]:
    if config.min_rsi is None and config.max_rsi is None:
        return None
    value = indicators.rsi(indicators.closes(bars), config.rsi_period)
    if value is None:
        return None
    details["rsi"] = round(value, 1)
    if config.min_rsi is not None and value < config.min_rsi:
        return FilterVerdict(False, "min_rsi", f"RSI {value:.1f} below the {config.min_rsi} floor", {})
    if config.max_rsi is not None and value > config.max_rsi:
        return FilterVerdict(False, "max_rsi", f"RSI {value:.1f} above the {config.max_rsi} ceiling", {})
    return None


def _rule_pattern(config, bars, last, direction, details) -> Optional[FilterVerdict]:
    if not config.require_pattern:
        return None

    wanted: Sequence[str]
    if config.require_pattern == "auto":
        if direction is None:
            return None
        wanted = indicators.BULLISH_PATTERNS if direction == "bullish" else indicators.BEARISH_PATTERNS
    elif isinstance(config.require_pattern, str):
        wanted = [config.require_pattern]
    else:
        wanted = [str(x) for x in config.require_pattern]

    found = [name for name in wanted if indicators.PATTERNS.get(name, lambda _b: False)(bars)]
    details["patterns"] = found
    if not found:
        return FilterVerdict(
            False, "require_pattern",
            f"no confirming candle pattern on the last bar (wanted one of {', '.join(wanted)})",
            {},
        )
    return None
