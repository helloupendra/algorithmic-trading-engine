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
      "macd": {"fast": 12, "slow": 26, "signal": 9},
      "psar": {"step": 0.02, "max": 0.2},
      "heikin_ashi": true,
      "donchian_side": 20,
      "bollinger_expanding": {"period": 20, "stdev": 2, "lookback": 3},
      "cpr_max_width_pct": 0.25,
      "cpr_side": true,
      "rsi_side": 50,
      "breadth_above_vwap_pct": 60,
      "heavyweights_min": 4,
      "require_pattern": "auto"
    }

Most indicator rules block a signal that fights their reading: a MACD histogram
on the other side of zero, a close on the wrong side of the Parabolic SAR, of
the previous N bars' Donchian midline or of the previous session's central
pivot range (above TC for bullish, below BC for bearish), a Heikin-Ashi candle
of the other colour, or RSI past a symmetric level (50: bullish needs RSI >= 50,
bearish <= 50; 40: bullish >= 40, bearish <= 60). Two read the regime and block
either direction: Bollinger bands no wider than `lookback` bars ago, and a CPR
wider than its ceiling (a narrow CPR tends to come before a trending day).

Two read the index's stocks rather than the index, from `metadata.breadth`
({"above_vwap_pct", "heavy_up", "heavy_down", "heavy_total"}), which the caller
fills from the constituents' bars: a bullish signal needs at least
`breadth_above_vwap_pct` % of the stocks above their session VWAP (a bearish one
at most 100 minus it), and at least `heavyweights_min` of the heavyweights on its
side of their VWAP. A run without breadth data is not blocked by them.
`true` takes an indicator's textbook settings, the ones shown above.

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
from typing import Any, Dict, List, Optional, Sequence, Tuple

#: A trading window as ("09:20", "11:00") in IST.
Window = Tuple[str, str]

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
    rsi_cross_up: Optional[float] = None          # a bullish signal needs RSI to have just crossed above this
    rsi_cross_down: Optional[float] = None        # the mirror for a bearish one
    require_pattern: Optional[Any] = None         # "auto" | name | [names]
    min_candle_body_ratio: Optional[float] = None  # body / range of the signal bar

    # When may the run trade at all
    windows_ist: List[Window] = field(default_factory=list)
    weekdays: Optional[frozenset] = None           # {"Mon", "Tue", ...}
    expiry_day: Optional[str] = None               # "only" | "skip"
    expiry_cutoff_ist: Optional[str] = None        # no entry after this time on an expiry day
    min_gap_percent: Optional[float] = None        # |open vs the previous close|
    max_gap_percent: Optional[float] = None
    min_vix: Optional[float] = None
    max_vix: Optional[float] = None
    min_vix_change_percent: Optional[float] = None  # VIX against the session's first VIX open
    max_vix_change_percent: Optional[float] = None

    # Trend rules: each one blocks a signal that fights the trend it measures
    ema_fast: Optional[int] = None
    ema_slow: Optional[int] = None
    supertrend: Optional[Tuple[int, float]] = None
    adx_min: Optional[float] = None
    adx_period: int = 14
    max_move_from_open_percent: Optional[float] = None
    opening_range_minutes: Optional[int] = None
    against_return_bars: Optional[int] = None
    against_return_percent: Optional[float] = None
    vote_min_against: Optional[int] = None

    # Indicator readings. All but the two regime rules (Bollinger width, CPR
    # width) block only a signal that fights them.
    macd: Optional[Tuple[int, int, int]] = None                 # (fast, slow, signal)
    psar: Optional[Tuple[float, float]] = None                  # (step, max)
    heikin_ashi: bool = False
    donchian_side: Optional[int] = None                         # bars before the signal bar
    bollinger_expanding: Optional[Tuple[int, float, int]] = None  # (period, stdev, lookback)
    cpr_max_width_pct: Optional[float] = None
    cpr_side: bool = False
    rsi_side: Optional[float] = None                            # bullish needs RSI >= it, bearish <= 100 - it
    breadth_above_vwap_pct: Optional[float] = None              # metadata.breadth: share of stocks above VWAP
    heavyweights_min: Optional[int] = None                      # metadata.breadth: heavyweights on the signal's side

    @property
    def needs_vix(self) -> bool:
        """True when a rule reads INDIA VIX, so the caller knows to load that series."""
        return any(value is not None for value in (self.min_vix, self.max_vix,
                                                   self.min_vix_change_percent, self.max_vix_change_percent))

    @property
    def any_rule(self) -> bool:
        return any([
            self.trade_window_ist, self.windows_ist, self.block_open_minutes,
            self.min_volume_zscore is not None,
            self.min_atr_percent is not None, self.max_atr_percent is not None,
            self.require_vwap_side,
            self.require_ema_side and self.ema_period,
            self.min_rsi is not None, self.max_rsi is not None,
            self.rsi_cross_up is not None, self.rsi_cross_down is not None,
            self.require_pattern, self.min_candle_body_ratio is not None,
            self.weekdays, self.expiry_day, self.expiry_cutoff_ist,
            self.min_gap_percent is not None, self.max_gap_percent is not None,
            self.min_vix is not None, self.max_vix is not None,
            self.min_vix_change_percent is not None, self.max_vix_change_percent is not None,
            self.ema_fast and self.ema_slow, self.supertrend, self.adx_min is not None,
            self.max_move_from_open_percent is not None, self.opening_range_minutes,
            self.against_return_bars and self.against_return_percent is not None,
            self.vote_min_against,
            self.macd, self.psar, self.heikin_ashi, self.donchian_side, self.bollinger_expanding,
            self.cpr_max_width_pct is not None, self.cpr_side, self.rsi_side is not None,
            self.breadth_above_vwap_pct is not None, self.heavyweights_min is not None,
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
        rsi_cross_up=_as_float(raw.get("rsi_cross_up")),
        rsi_cross_down=_as_float(raw.get("rsi_cross_down")),
        require_pattern=raw.get("require_pattern") or None,
        min_candle_body_ratio=_as_float(raw.get("min_candle_body_ratio")),
        windows_ist=_windows(raw.get("windows_ist")),
        weekdays=_weekdays(raw.get("weekdays")),
        expiry_day=_expiry_day(raw.get("expiry_day")),
        expiry_cutoff_ist=str(raw["expiry_cutoff_ist"]) if raw.get("expiry_cutoff_ist") else None,
        min_gap_percent=_as_float(raw.get("min_gap_percent")),
        max_gap_percent=_as_float(raw.get("max_gap_percent")),
        min_vix=_as_float(raw.get("min_vix")),
        max_vix=_as_float(raw.get("max_vix")),
        min_vix_change_percent=_as_float(raw.get("min_vix_change_percent")),
        max_vix_change_percent=_as_float(raw.get("max_vix_change_percent")),
        ema_fast=_as_int(raw.get("ema_fast")),
        ema_slow=_as_int(raw.get("ema_slow")),
        supertrend=_supertrend(raw.get("supertrend")),
        adx_min=_as_float(raw.get("adx_min")),
        adx_period=_as_int(raw.get("adx_period"), 14) or 14,
        max_move_from_open_percent=_as_float(raw.get("max_move_from_open_percent")),
        opening_range_minutes=_as_int(raw.get("opening_range_minutes")),
        against_return_bars=_as_int(raw.get("against_return_bars")),
        against_return_percent=_as_float(raw.get("against_return_percent")),
        vote_min_against=_as_int(raw.get("vote_min_against")),
        macd=_macd(raw.get("macd")),
        psar=_psar(raw.get("psar")),
        heikin_ashi=bool(raw.get("heikin_ashi")),
        donchian_side=_period("donchian_side", raw.get("donchian_side")),
        bollinger_expanding=_bollinger(raw.get("bollinger_expanding")),
        cpr_max_width_pct=_as_float(raw.get("cpr_max_width_pct")),
        cpr_side=bool(raw.get("cpr_side")),
        rsi_side=_as_float(raw.get("rsi_side")),
        breadth_above_vwap_pct=_as_float(raw.get("breadth_above_vwap_pct")),
        heavyweights_min=_as_int(raw.get("heavyweights_min")),
    )
    return config if config.any_rule else None


WEEKDAYS = ("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")


def _windows(raw: Any) -> List[Window]:
    """[["09:20","11:00"], ["13:00","15:15"]] -> pairs; a single pair is accepted too."""
    if not isinstance(raw, (list, tuple)) or not raw:
        return []
    items = [raw] if len(raw) == 2 and all(isinstance(x, str) for x in raw) else raw
    return [(str(item[0]), str(item[1])) for item in items
            if isinstance(item, (list, tuple)) and len(item) == 2]


def _weekdays(raw: Any) -> Optional[frozenset]:
    if not raw:
        return None
    names = [raw] if isinstance(raw, str) else list(raw)
    wanted = {str(name).strip()[:3].title() for name in names}
    chosen = frozenset(day for day in WEEKDAYS if day in wanted)
    return chosen or None


def _expiry_day(raw: Any) -> Optional[str]:
    value = str(raw or "").strip().lower()
    return value if value in ("only", "skip") else None


def _supertrend(raw: Any) -> Optional[tuple]:
    if isinstance(raw, (list, tuple)) and len(raw) == 2:
        period, multiple = _as_int(raw[0]), _as_float(raw[1])
        return (period, multiple) if period and multiple else None
    if isinstance(raw, dict):
        period, multiple = _as_int(raw.get("period"), 10), _as_float(raw.get("multiple"))
        return (period or 10, multiple or 3.0)
    return (10, 3.0) if raw is True else None


def _ignored(key: str, raw: Any, why: str) -> None:
    """Says so when a rule was asked for but cannot be read: a run must not believe it is filtered."""
    print(f"[FILTER] ignoring {key}={raw!r}: {why}", flush=True)


def _period(key: str, raw: Any) -> Optional[int]:
    """A positive whole number of bars. `true` is not a period, so it is refused rather than read as 1."""
    if raw is None or raw is False:
        return None
    value = None if isinstance(raw, bool) else _as_int(raw)
    if value is None or value < 1:
        _ignored(key, raw, "it needs a whole number of bars, 1 or more")
        return None
    return value


def _macd(raw: Any) -> Optional[Tuple[int, int, int]]:
    """`true` or {"fast", "slow", "signal"}, each defaulting to 12/26/9."""
    if raw is None or raw is False:
        return None
    if raw is True:
        return (12, 26, 9)
    if isinstance(raw, dict):
        fast, slow, signal = (_as_int(raw.get("fast", 12)), _as_int(raw.get("slow", 26)),
                              _as_int(raw.get("signal", 9)))
        if fast is not None and slow is not None and signal is not None and 0 < fast < slow and signal > 0:
            return (fast, slow, signal)
    _ignored("macd", raw, 'it takes true or {"fast", "slow", "signal"} with 0 < fast < slow')
    return None


def _psar(raw: Any) -> Optional[Tuple[float, float]]:
    """`true` or {"step", "max"}, defaulting to Wilder's 0.02 and 0.2."""
    if raw is None or raw is False:
        return None
    if raw is True:
        return (0.02, 0.2)
    if isinstance(raw, dict):
        step, maximum = _as_float(raw.get("step", 0.02)), _as_float(raw.get("max", 0.2))
        if step is not None and maximum is not None and 0 < step <= maximum:
            return (step, maximum)
    _ignored("psar", raw, 'it takes true or {"step", "max"} with 0 < step <= max')
    return None


def _bollinger(raw: Any) -> Optional[Tuple[int, float, int]]:
    """`true` or {"period", "stdev", "lookback"}, defaulting to 20 bars, 2 SD, 3 bars back."""
    if raw is None or raw is False:
        return None
    if raw is True:
        return (20, 2.0, 3)
    if isinstance(raw, dict):
        period, stdev = _as_int(raw.get("period", 20)), _as_float(raw.get("stdev", 2.0))
        lookback = _as_int(raw.get("lookback", 3))
        if period is not None and period >= 2 and stdev is not None and stdev > 0 \
                and lookback is not None and lookback >= 1:
            return (period, stdev, lookback)
    _ignored("bollinger_expanding", raw,
             'it takes true or {"period", "stdev", "lookback"} with period >= 2, stdev > 0, lookback >= 1')
    return None


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

    context = _context(config, inp, newest_bar_is_forming)
    for rule in (_rule_window, _rule_weekday, _rule_expiry_day, _rule_gap, _rule_vix,
                 _rule_volume, _rule_atr, _rule_candle_body,
                 _rule_vwap, _rule_ema, _rule_ema_order, _rule_supertrend, _rule_adx,
                 _rule_open_move, _rule_opening_range, _rule_recent_return, _rule_vote,
                 _rule_macd, _rule_psar, _rule_heikin_ashi, _rule_donchian, _rule_bollinger,
                 _rule_cpr, _rule_rsi_side, _rule_breadth, _rule_heavyweights,
                 _rule_rsi, _rule_rsi_cross, _rule_pattern):
        verdict = rule(config, bars, last, direction, details, context)
        if verdict is not None:
            verdict.details = {**details, **verdict.details}
            return verdict

    return FilterVerdict(True, reason="passed every configured filter", details=details)


# ------------------------------------------------------------------ rules --
# Each returns a blocking verdict, or None to let the signal continue. A rule
# whose inputs are missing returns None: it cannot judge, so it does not.

def _in_window(window: Window, moment) -> Optional[bool]:
    try:
        start_h, start_m = (int(x) for x in window[0].split(":"))
        end_h, end_m = (int(x) for x in window[1].split(":"))
    except (ValueError, AttributeError):
        return None
    return dtime(start_h, start_m) <= moment.time() <= dtime(end_h, end_m)


def _rule_window(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    windows = config.windows_ist or ([config.trade_window_ist] if config.trade_window_ist else [])
    if not windows and config.block_open_minutes is None:
        return None

    moment = indicators.ist_of(last)
    if moment is None:
        return None
    details["bar_ist"] = moment.strftime("%H:%M")

    if windows:
        checks = [_in_window(w, moment) for w in windows]
        if any(check is True for check in checks):
            pass
        elif all(check is None for check in checks):
            return None
        else:
            spelled = " or ".join(f"{w[0]}-{w[1]}" for w in windows)
            return FilterVerdict(
                False, "trade_window_ist",
                f"{moment:%H:%M} IST is outside the trading window(s) {spelled}",
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


def _context(config, inp, newest_bar_is_forming: bool) -> Dict[str, Any]:
    """
    What the rules need beyond the index bars: the day's expiry, and the INDIA VIX
    series when the run loaded one. Absent pieces stay None and their rules stand
    aside, which is how a filter behaves when it cannot judge.
    """
    metadata = getattr(inp, "metadata", None) or {}
    vix: List[Any] = []
    for series in (getattr(inp, "bars", None) or {}).values():
        found = (series or {}).get("vix")
        if found:
            vix = list(found)
            break
    if newest_bar_is_forming and vix:
        vix = vix[:-1]
    breadth = metadata.get("breadth")
    return {"expiry_date": str(metadata.get("expiry_date") or "")[:10], "vix": vix,
            "breadth": breadth if isinstance(breadth, dict) else None}


def _against(direction: Optional[str], view: Optional[str]) -> bool:
    """True when a signal's direction fights the view an indicator takes."""
    return bool(direction and view and direction != view)


def _rule_weekday(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not config.weekdays:
        return None
    moment = indicators.ist_of(last)
    if moment is None:
        return None
    day = moment.strftime("%a")
    details["weekday"] = day
    if day not in config.weekdays:
        return FilterVerdict(False, "weekdays", f"{day} is not in the run's trading days", {})
    return None


def _rule_expiry_day(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not (config.expiry_day or config.expiry_cutoff_ist):
        return None
    moment = indicators.ist_of(last)
    expiry = context.get("expiry_date")
    if moment is None or not expiry:
        return None
    is_expiry = moment.strftime("%Y-%m-%d") == expiry
    details["expiry_day"] = is_expiry
    if config.expiry_day == "only" and not is_expiry:
        return FilterVerdict(False, "expiry_day", f"{moment:%d %b} is not an expiry day", {})
    if config.expiry_day == "skip" and is_expiry:
        return FilterVerdict(False, "expiry_day", f"{moment:%d %b} is an expiry day", {})
    if is_expiry and config.expiry_cutoff_ist:
        inside = _in_window(("00:00", config.expiry_cutoff_ist), moment)
        if inside is False:
            return FilterVerdict(
                False, "expiry_cutoff_ist",
                f"{moment:%H:%M} IST is past {config.expiry_cutoff_ist} on an expiry day", {})
    return None


def _rule_gap(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if config.min_gap_percent is None and config.max_gap_percent is None:
        return None
    gap = indicators.gap_percent(bars)
    if gap is None:
        return None
    details["gap_percent"] = round(gap, 3)
    size = abs(gap)
    if config.min_gap_percent is not None and size < config.min_gap_percent:
        return FilterVerdict(False, "min_gap_percent",
                             f"the session gapped {size:.2f}%, below the {config.min_gap_percent}% wanted", {})
    if config.max_gap_percent is not None and size > config.max_gap_percent:
        return FilterVerdict(False, "max_gap_percent",
                             f"the session gapped {size:.2f}%, above the {config.max_gap_percent}% ceiling", {})
    return None


def _rule_vix(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    wants_level = config.min_vix is not None or config.max_vix is not None
    wants_change = config.min_vix_change_percent is not None or config.max_vix_change_percent is not None
    if not (wants_level or wants_change):
        return None
    vix_bars = context.get("vix") or []
    if not vix_bars:
        return None
    level = float(vix_bars[-1].close)
    details["vix"] = round(level, 2)
    if config.min_vix is not None and level < config.min_vix:
        return FilterVerdict(False, "min_vix", f"INDIA VIX {level:.2f} is below {config.min_vix}", {})
    if config.max_vix is not None and level > config.max_vix:
        return FilterVerdict(False, "max_vix", f"INDIA VIX {level:.2f} is above {config.max_vix}", {})
    if wants_change:
        session = indicators.session_bars(vix_bars)
        if not session:
            return None
        first = float(session[0].open)
        if first <= 0:
            return None
        change = 100.0 * (level - first) / first
        details["vix_change_percent"] = round(change, 2)
        if config.min_vix_change_percent is not None and change < config.min_vix_change_percent:
            return FilterVerdict(False, "min_vix_change_percent",
                                 f"INDIA VIX is {change:+.1f}% on the day, below {config.min_vix_change_percent}%", {})
        if config.max_vix_change_percent is not None and change > config.max_vix_change_percent:
            return FilterVerdict(False, "max_vix_change_percent",
                                 f"INDIA VIX is {change:+.1f}% on the day, above {config.max_vix_change_percent}%", {})
    return None


def _rule_candle_body(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if config.min_candle_body_ratio is None:
        return None
    candle = indicators.candle(last)
    if candle.span <= 0:
        return None
    ratio = candle.body / candle.span
    details["body_ratio"] = round(ratio, 2)
    if ratio < config.min_candle_body_ratio:
        return FilterVerdict(False, "min_candle_body_ratio",
                             f"the signal candle's body is {ratio:.0%} of its range, "
                             f"under the {config.min_candle_body_ratio:.0%} wanted", {})
    return None


def _ema_order_view(config, bars) -> Optional[str]:
    fast = indicators.ema(indicators.closes(bars), int(config.ema_fast))
    slow = indicators.ema(indicators.closes(bars), int(config.ema_slow))
    if fast is None or slow is None:
        return None
    return "bullish" if fast >= slow else "bearish"


def _rule_ema_order(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not (config.ema_fast and config.ema_slow) or direction is None:
        return None
    view = _ema_order_view(config, bars)
    if view is None:
        return None
    details[f"ema{config.ema_fast}_vs_{config.ema_slow}"] = view
    if _against(direction, view):
        return FilterVerdict(False, "ema_order",
                             f"EMA {config.ema_fast} is {'below' if view == 'bearish' else 'above'} EMA "
                             f"{config.ema_slow}, against this {direction} signal", {})
    return None


def _supertrend_view(config, bars) -> Optional[str]:
    if not config.supertrend:
        return None
    period, multiple = config.supertrend
    reading = indicators.supertrend(bars, int(period), float(multiple))
    return None if reading is None else str(reading["direction"])


def _rule_supertrend(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not config.supertrend or direction is None:
        return None
    view = _supertrend_view(config, bars)
    if view is None:
        return None
    details["supertrend"] = view
    if _against(direction, view):
        period, multiple = config.supertrend
        return FilterVerdict(False, "supertrend",
                             f"Supertrend({period:g},{multiple:g}) is {view}, against this {direction} signal", {})
    return None


def _rule_adx(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if config.adx_min is None or direction is None:
        return None
    reading = indicators.adx(bars, config.adx_period)
    if reading is None:
        return None
    details["adx"] = round(reading["adx"], 1)
    view = "bullish" if reading["plus_di"] >= reading["minus_di"] else "bearish"
    details["adx_view"] = view
    if reading["adx"] >= config.adx_min and _against(direction, view):
        return FilterVerdict(False, "adx_min",
                             f"ADX {reading['adx']:.0f} with the {view} DI ahead "
                             f"(+DI {reading['plus_di']:.0f}, −DI {reading['minus_di']:.0f}), "
                             f"against this {direction} signal", {})
    return None


def _rule_open_move(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if config.max_move_from_open_percent is None or direction is None:
        return None
    move = indicators.move_from_open_percent(bars)
    if move is None:
        return None
    details["move_from_open_percent"] = round(move, 3)
    view = "bullish" if move >= 0 else "bearish"
    if abs(move) >= config.max_move_from_open_percent and _against(direction, view):
        return FilterVerdict(False, "max_move_from_open_percent",
                             f"the day is {move:+.2f}% from its open, against this {direction} signal", {})
    return None


def _opening_range_view(config, bars) -> Optional[str]:
    if not config.opening_range_minutes:
        return None
    window = indicators.opening_range(bars, int(config.opening_range_minutes))
    if window is None:
        return None
    close = float(bars[-1].close)
    if close > window["high"]:
        return "bullish"
    if close < window["low"]:
        return "bearish"
    return None


def _rule_opening_range(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not config.opening_range_minutes or direction is None:
        return None
    view = _opening_range_view(config, bars)
    if view is None:
        return None
    details["opening_range"] = view
    if _against(direction, view):
        return FilterVerdict(False, "opening_range",
                             f"price is {'above' if view == 'bullish' else 'below'} the first "
                             f"{config.opening_range_minutes} minutes' range, against this {direction} signal", {})
    return None


def _rule_recent_return(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not config.against_return_bars or config.against_return_percent is None or direction is None:
        return None
    move = indicators.return_percent(bars, int(config.against_return_bars))
    if move is None:
        return None
    details["recent_return_percent"] = round(move, 3)
    view = "bullish" if move >= 0 else "bearish"
    if abs(move) >= config.against_return_percent and _against(direction, view):
        return FilterVerdict(False, "against_return",
                             f"the last {config.against_return_bars} bars moved {move:+.2f}%, "
                             f"against this {direction} signal", {})
    return None


#: What the vote reads when the run does not name its own periods. The rule is
#: deliberately self-contained: asking for a vote should not also switch on each
#: reading as a rule of its own.
VOTE_EMA_PERIOD = 20
VOTE_SUPERTREND = (10, 3.0)
VOTE_OPENING_RANGE_MINUTES = 15


def _rule_vote(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    """Blocks when enough of the trend readings disagree with the signal at once."""
    if not config.vote_min_against or direction is None:
        return None
    close = float(last.close)
    views: Dict[str, Optional[str]] = {}
    vwap = indicators.vwap(indicators.session_bars(bars))
    views["vwap"] = None if vwap is None else ("bullish" if close >= vwap else "bearish")
    value = indicators.ema(indicators.closes(bars), int(config.ema_period or VOTE_EMA_PERIOD))
    views["ema"] = None if value is None else ("bullish" if close >= value else "bearish")
    if config.ema_fast and config.ema_slow:
        views["ema_order"] = _ema_order_view(config, bars)
    period, multiple = config.supertrend or VOTE_SUPERTREND
    reading = indicators.supertrend(bars, int(period), float(multiple))
    views["supertrend"] = None if reading is None else str(reading["direction"])
    window = indicators.opening_range(bars, int(config.opening_range_minutes or VOTE_OPENING_RANGE_MINUTES))
    if window is not None:
        views["opening_range"] = ("bullish" if close > window["high"]
                                  else "bearish" if close < window["low"] else None)
    move = indicators.move_from_open_percent(bars)
    views["from_open"] = None if move is None else ("bullish" if move >= 0 else "bearish")

    against = [name for name, view in views.items() if _against(direction, view)]
    readable = {name: view for name, view in views.items() if view}
    details["votes"] = readable
    details["votes_against"] = len(against)
    if len(against) >= config.vote_min_against:
        return FilterVerdict(False, "vote_min_against",
                             f"{len(against)} of {len(readable)} trend readings are against this "
                             f"{direction} signal ({', '.join(against)})", {})
    return None


#: Differences smaller than this, in price points, are floating-point noise and
#: not a reading: a perfectly steady trend leaves a MACD histogram of -1.8e-15.
NOISE = 1e-6


def _side_of(value: float, level: float) -> Optional[str]:
    """The view a value takes of a level: above is bullish, below bearish, on it (within NOISE) none."""
    return "bullish" if value > level + NOISE else "bearish" if value < level - NOISE else None


def _rule_macd(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not config.macd or direction is None:
        return None
    fast, slow, signal = config.macd
    reading = indicators.macd(indicators.closes(bars), fast, slow, signal)
    if reading is None:
        return None
    histogram = reading["histogram"]
    details["macd_histogram"] = round(histogram, 4)
    if _against(direction, _side_of(histogram, 0.0)):
        return FilterVerdict(False, "macd",
                             f"MACD({fast},{slow},{signal}) histogram {histogram:.2f} is against this "
                             f"{direction} signal", {})
    return None


def _rule_psar(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not config.psar or direction is None:
        return None
    step, maximum = config.psar
    reading = indicators.parabolic_sar(bars, step, maximum)
    if reading is None:
        return None
    sar, close = float(reading["sar"]), float(last.close)
    details["psar"] = round(sar, 2)
    view = _side_of(close, sar)
    if _against(direction, view):
        return FilterVerdict(False, "psar",
                             f"close {close:.2f} is {'below' if view == 'bearish' else 'above'} the Parabolic "
                             f"SAR({step:g},{maximum:g}) {sar:.2f}, against this {direction} signal", {})
    return None


def _rule_heikin_ashi(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if not config.heikin_ashi or direction is None:
        return None
    ha = indicators.heikin_ashi(bars)
    if ha is None:
        return None
    view = _side_of(ha["close"], ha["open"])
    colour = {"bullish": "green", "bearish": "red"}.get(view or "", "flat")
    details["heikin_ashi"] = colour
    if _against(direction, view):
        return FilterVerdict(False, "heikin_ashi",
                             f"the last Heikin-Ashi candle is {colour} (open {ha['open']:.2f}, "
                             f"close {ha['close']:.2f}), against this {direction} signal", {})
    return None


def _rule_donchian(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    """The signal bar against the channel of the bars before it, so it cannot set its own midline."""
    if not config.donchian_side or direction is None:
        return None
    channel = indicators.donchian(bars[:-1], config.donchian_side)
    if channel is None:
        return None
    close, middle = float(last.close), channel["middle"]
    details["donchian_middle"] = round(middle, 2)
    view = _side_of(close, middle)
    if _against(direction, view):
        return FilterVerdict(False, "donchian_side",
                             f"close {close:.2f} is {'below' if view == 'bearish' else 'above'} {middle:.2f}, "
                             f"the midline of the previous {config.donchian_side} bars' range "
                             f"{channel['lower']:.2f}-{channel['upper']:.2f}, against this {direction} signal", {})
    return None


def _rule_bollinger(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    """Either direction: a breakout into bands that are not widening is a range, not a move."""
    if not config.bollinger_expanding:
        return None
    period, stdev, lookback = config.bollinger_expanding
    values = indicators.closes(bars)
    now = indicators.bollinger(values, period, stdev)
    before = indicators.bollinger(values[:-lookback], period, stdev)
    if now is None or before is None:
        return None
    details["bollinger_width"] = round(now["width"], 2)
    details["bollinger_width_before"] = round(before["width"], 2)
    if now["width"] <= before["width"] + NOISE:
        return FilterVerdict(False, "bollinger_expanding",
                             f"Bollinger({period},{stdev:g}) width {now['width']:.2f} is not above its "
                             f"{before['width']:.2f} of {lookback} bars ago; the bands are not expanding", {})
    return None


def _rule_cpr(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    if config.cpr_max_width_pct is None and not config.cpr_side:
        return None
    levels = indicators.cpr(bars)
    if levels is None:
        return None
    pivot, bc, tc, width = levels["pivot"], levels["bc"], levels["tc"], levels["width_percent"]
    details["cpr"] = {"pivot": round(pivot, 2), "bc": round(bc, 2), "tc": round(tc, 2),
                      "width_percent": round(width, 4), "from_session": levels["session"]}
    if config.cpr_max_width_pct is not None and width > config.cpr_max_width_pct:
        return FilterVerdict(False, "cpr_max_width_pct",
                             f"today's CPR is {width:.3f}% wide (BC {bc:.2f} to TC {tc:.2f}, pivot {pivot:.2f}, "
                             f"from {levels['session']}), above the {config.cpr_max_width_pct}% ceiling", {})
    if config.cpr_side and direction is not None:
        close = float(last.close)
        if direction == "bullish" and not close > tc:
            return FilterVerdict(False, "cpr_side",
                                 f"bullish signal with close {close:.2f} not above the CPR top (TC) {tc:.2f}", {})
        if direction == "bearish" and not close < bc:
            return FilterVerdict(False, "cpr_side",
                                 f"bearish signal with close {close:.2f} not below the CPR bottom (BC) {bc:.2f}", {})
    return None


def _rule_rsi_side(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    """min_rsi/max_rsi apply one floor and ceiling to every signal; this mirrors the level by direction."""
    if config.rsi_side is None or direction is None:
        return None
    value = indicators.rsi(indicators.closes(bars), config.rsi_period)
    if value is None:
        return None
    details["rsi"] = round(value, 1)
    floor, ceiling = config.rsi_side, 100.0 - config.rsi_side
    if direction == "bullish" and value < floor:
        return FilterVerdict(False, "rsi_side",
                             f"RSI({config.rsi_period}) {value:.1f} is below {floor:g}, against this bullish signal", {})
    if direction == "bearish" and value > ceiling:
        return FilterVerdict(False, "rsi_side",
                             f"RSI({config.rsi_period}) {value:.1f} is above {ceiling:g}, against this bearish signal", {})
    return None


def _rule_breadth(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    """The index's stocks must lean the signal's way: a share of them above their own session VWAP."""
    breadth = context.get("breadth")
    if config.breadth_above_vwap_pct is None or direction is None or not breadth:
        return None
    pct = _as_float(breadth.get("above_vwap_pct"))
    if pct is None:
        return None
    details["breadth_above_vwap_pct"] = round(pct, 1)
    need = config.breadth_above_vwap_pct
    if direction == "bullish" and pct < need:
        return FilterVerdict(False, "breadth_above_vwap_pct",
                             f"only {pct:.0f}% of the stocks are above their VWAP; a bullish signal needs {need:g}%", {})
    if direction == "bearish" and pct > 100.0 - need:
        return FilterVerdict(False, "breadth_above_vwap_pct",
                             f"{pct:.0f}% of the stocks are above their VWAP; a bearish signal needs at most {100.0 - need:g}%", {})
    return None


def _rule_heavyweights(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    """The biggest stocks move the index: enough of them must be on the signal's side of their VWAP."""
    breadth = context.get("breadth")
    if config.heavyweights_min is None or direction is None or not breadth:
        return None
    key = "heavy_up" if direction == "bullish" else "heavy_down"
    count = _as_int(breadth.get(key))
    if count is None:
        return None
    total = _as_int(breadth.get("heavy_total"))
    details[key] = count
    if count < config.heavyweights_min:
        side = "above" if direction == "bullish" else "below"
        return FilterVerdict(False, "heavyweights_min",
                             f"{count}{f' of {total}' if total else ''} heavyweights are {side} their VWAP; "
                             f"a {direction} signal needs {config.heavyweights_min}", {})
    return None


def _rule_rsi_cross(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
    """The momentum spike: RSI must have crossed the level on the signal bar itself."""
    if (config.rsi_cross_up is None and config.rsi_cross_down is None) or direction is None:
        return None
    closes = indicators.closes(bars)
    now = indicators.rsi(closes, config.rsi_period)
    before = indicators.rsi(closes[:-1], config.rsi_period)
    if now is None or before is None:
        return None
    details["rsi"] = round(now, 1)
    details["rsi_before"] = round(before, 1)
    if direction == "bullish" and config.rsi_cross_up is not None:
        if not (before <= config.rsi_cross_up < now):
            return FilterVerdict(False, "rsi_cross_up",
                                 f"RSI went {before:.1f} → {now:.1f} without crossing above "
                                 f"{config.rsi_cross_up}", {})
    if direction == "bearish" and config.rsi_cross_down is not None:
        if not (before >= config.rsi_cross_down > now):
            return FilterVerdict(False, "rsi_cross_down",
                                 f"RSI went {before:.1f} → {now:.1f} without crossing below "
                                 f"{config.rsi_cross_down}", {})
    return None


def _rule_volume(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
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


def _rule_atr(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
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


def _rule_vwap(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
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


def _rule_ema(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
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


def _rule_rsi(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
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


def _rule_pattern(config, bars, last, direction, details, context) -> Optional[FilterVerdict]:
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
