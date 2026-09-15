"""
research/regime.py

A pure, rule-based intraday market regime classifier.

Input: intraday bars of ONE underlying across sessions, oldest first. Anything
with `timestamp_utc` (bar START, UTC) and open/high/low/close/volume works,
including `strategies.base_strategy.BarFrame`. The bars should already be
in-session (NSE 09:15-15:30 IST); `research.data` filters them.

Output: one `RegimeReading` per bar with a label from
    TREND_UP, TREND_DOWN, RANGE, VOLATILE_CHOP
and every number the label was decided from, plus `summarize_sessions` for a
day-level view (dominant regime, when it was first called).

Why this exists: an option buyer pays theta, IV crush and the spread on every
trade. Only a fast, sustained move pays for that, so the first question before
any entry is "is this a trend day, and which way". A reversal buy into a trend
day (2026-09-15: calls bought into a NIFTY down-trend) is the failure this
answers.

No look-ahead, by construction: the classifier is one forward pass. Every value
at bar i is computed from bars[0..i] only - indicator recursions, the running
session VWAP, the opening range, and a volatility baseline built from sessions
that have already FINISHED. The tests append future bars and check that no
earlier reading changes.

No ML and no fitted weights: five directional checks vote, and the label is a
readable rule over the votes (see `_label`). Thresholds live in
`RegimeConfig`, each with the reason it has the value it has.

"Not known yet" is never rendered as a regime: until the indicators and the
volatility baseline are warm the label is None and `ready` is False.
"""

from __future__ import annotations

from collections import Counter, deque
from dataclasses import asdict, dataclass, field
from datetime import date, datetime, time, timedelta
from typing import Any, Deque, Dict, List, Optional, Sequence

from strategies.indicators import ist_of

from research.ta import directional_series, ema_series, median

TREND_UP = "TREND_UP"
TREND_DOWN = "TREND_DOWN"
RANGE = "RANGE"
VOLATILE_CHOP = "VOLATILE_CHOP"
LABELS = (TREND_UP, TREND_DOWN, RANGE, VOLATILE_CHOP)

# Opening range states.
OR_FORMING = "FORMING"          # the first `opening_range_minutes` are not over yet
OR_INSIDE = "INSIDE"            # closed between the range's high and low
OR_ABOVE = "ABOVE"              # closed above the range high
OR_BELOW = "BELOW"              # closed below the range low
OR_UNAVAILABLE = "UNAVAILABLE"  # the session has no bars inside the opening window

VOL_EXPANDING = "EXPANDING"
VOL_NORMAL = "NORMAL"
VOL_CONTRACTING = "CONTRACTING"
VOL_UNKNOWN = "UNKNOWN"


@dataclass(frozen=True)
class RegimeConfig:
    """
    Every threshold the classifier uses, with the reason for its value.

    The values were chosen from how the indicators behave, not by optimising
    any trading outcome; the regime report shows how often each label fires on
    real data so a bad threshold is visible rather than hidden.
    """

    # --- trend strength ----------------------------------------------------
    #: Wilder's own period, the one every charting package defaults to.
    adx_period: int = 14
    #: ADX below 20 is conventionally "no trend" (Wilder; most practitioners use
    #: 20-25). The DI vote only counts when ADX clears this, so a +DI/-DI
    #: crossing inside a flat market does not vote for a direction.
    adx_trend: float = 20.0

    # --- trend direction ---------------------------------------------------
    #: 21 bars of 5m is ~1h45m: slow enough to ignore a single push, fast enough
    #: to turn within a morning.
    ema_period: int = 21
    #: Slope = (EMA now - EMA `slope_lookback` bars ago) / ATR now.
    slope_lookback: int = 5
    #: Half an ATR of EMA travel over 5 bars (0.1 ATR per bar). A driftless
    #: market moves its 21-EMA far less than that over 25 minutes; NIFTY's
    #: ordinary trend days move it more. Normalising by ATR makes the same
    #: number work for NIFTY and BANKNIFTY.
    slope_min_atr: float = 0.5

    # --- session VWAP ------------------------------------------------------
    #: Closes within 0.3 ATR of VWAP are "at VWAP": that band is about one
    #: bar's ordinary noise, so a close inside it says nothing about side.
    vwap_min_atr: float = 0.3
    #: Share of the session's closes on one side of the running VWAP that
    #: counts as one-sided. 65% means roughly two of every three bars held that
    #: side; a balanced range day sits near 50%.
    side_fraction_min: float = 0.65
    #: The share is meaningless on 2 bars; wait for 30 minutes of 5m bars.
    side_min_bars: int = 6
    #: "auto": time-weighted typical price for index symbols (an index trades
    #: no volume of its own, and the number some feeds put in the volume field
    #: is not the index's), volume-weighted otherwise when volume is reported.
    #: "time" / "volume" force one.
    vwap_mode: str = "auto"

    # --- opening range -----------------------------------------------------
    session_open_ist: time = time(9, 15)
    #: The first 15 minutes: long enough to absorb the opening auction noise,
    #: short enough to leave the day to trade.
    opening_range_minutes: int = 15
    #: Bar length, used only to tell when the opening range is complete.
    bar_minutes: int = 5

    # --- volatility --------------------------------------------------------
    #: ATR% at this bar is compared with the median ATR% at the SAME time of
    #: day over the last 20 finished sessions (about a trading month). Same
    #: time of day, because intraday ATR is U-shaped: the open is always more
    #: volatile than lunch, and comparing it to an all-day median would call
    #: every open "expanding".
    vol_lookback_sessions: int = 20
    #: Fewer than 5 comparable sessions is not a baseline; the label waits.
    vol_min_sessions: int = 5
    #: 25% above the usual ATR% for this time of day: clearly more than the
    #: ordinary day-to-day spread of that ratio, which on NIFTY 5m sits mostly
    #: within about 0.8-1.2.
    vol_expanding: float = 1.25
    vol_contracting: float = 0.8

    # --- the rule ----------------------------------------------------------
    #: Of the five directional checks, how many must agree (with none against)
    #: to call a trend.
    trend_votes: int = 4
    #: Consecutive bars a label must hold for the session summary to call it
    #: "confirmed".
    confirm_bars: int = 3
    #: Session summary: a day is a trend day when one trend label holds at
    #: least 40% of its ready bars and the opposite trend at most a third as
    #: many. Trends rarely start at the open, so a plain "most bars" count
    #: would call a day that trended from 11:00 to the close a RANGE day.
    trend_day_share: float = 0.40


@dataclass(frozen=True)
class RegimeReading:
    """The regime at one bar and the numbers behind it."""
    index: int
    timestamp_utc: str
    session: str                      # IST date, the session key
    session_bar: int                  # 0 = first bar of the session
    label: Optional[str]              # one of LABELS, None while not ready
    ready: bool
    reason: str

    close: float
    adx: Optional[float]
    plus_di: Optional[float]
    minus_di: Optional[float]
    atr: Optional[float]
    atr_pct: Optional[float]
    ema: Optional[float]
    ema_slope_atr: Optional[float]

    vwap: float
    vwap_weighting: str               # "time" | "volume"
    vwap_distance_atr: Optional[float]
    frac_above_vwap: float
    frac_below_vwap: float

    or_high: Optional[float]
    or_low: Optional[float]
    or_state: str
    or_broke_up: bool                 # a close above the range high happened this session
    or_broke_down: bool

    vol_ratio: Optional[float]
    vol_state: str

    up_votes: int
    down_votes: int
    votes: Dict[str, int] = field(default_factory=dict)   # check -> +1 up / -1 down / 0

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


def _is_index_symbol(bars: Sequence[Any]) -> bool:
    symbol = str(getattr(bars[0], "symbol", "") or "").upper() if bars else ""
    return symbol.endswith("-INDEX") or symbol in ("NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX")


def _vote(value: Optional[float], threshold: float) -> int:
    if value is None:
        return 0
    if value >= threshold:
        return 1
    if value <= -threshold:
        return -1
    return 0


def _label(up: int, down: int, vol_ratio: Optional[float], config: RegimeConfig) -> tuple:
    """
    The rule, in order:
      1. TREND_UP   when at least `trend_votes` checks vote up and none votes down;
      2. TREND_DOWN the mirror;
      3. VOLATILE_CHOP when there is no trend and ATR% is at least
         `vol_expanding` times its usual level for this time of day - big bars
         with no direction, where a bought option is stopped both ways;
      4. RANGE otherwise.
    """
    if up >= config.trend_votes and down == 0:
        return TREND_UP, f"{up}/5 checks up, none down"
    if down >= config.trend_votes and up == 0:
        return TREND_DOWN, f"{down}/5 checks down, none up"
    if vol_ratio is not None and vol_ratio >= config.vol_expanding:
        return VOLATILE_CHOP, f"no trend ({up} up / {down} down) and ATR% {vol_ratio:.2f}x usual"
    return RANGE, f"no trend ({up} up / {down} down), volatility not expanding"


def classify(bars: Sequence[Any], config: Optional[RegimeConfig] = None) -> List[RegimeReading]:
    """One `RegimeReading` per bar; see the module docstring for the guarantees."""
    config = config or RegimeConfig()
    n = len(bars)
    if n == 0:
        return []

    moments: List[datetime] = []
    for bar in bars:
        moment = ist_of(bar)
        if moment is None:
            raise ValueError(f"bar without a readable timestamp_utc: {bar!r}")
        moments.append(moment)
    sessions = [m.strftime("%Y-%m-%d") for m in moments]

    closes = [float(b.close) for b in bars]
    directional = directional_series(bars, config.adx_period, session_keys=sessions)
    emas = ema_series(closes, config.ema_period)

    if config.vwap_mode == "time":
        volume_weighted = False
    elif config.vwap_mode == "volume":
        volume_weighted = True
    elif config.vwap_mode == "auto":
        volume_weighted = not _is_index_symbol(bars)
    else:
        raise ValueError(f"vwap_mode must be auto, time or volume, not {config.vwap_mode!r}")

    or_window_end = (datetime.combine(date(2000, 1, 1), config.session_open_ist)
                     + timedelta(minutes=config.opening_range_minutes)).time()

    finished_sessions: Deque[Dict[str, float]] = deque(maxlen=config.vol_lookback_sessions)
    current_slots: Dict[str, float] = {}

    readings: List[RegimeReading] = []
    session_bar = 0
    pv_sum = weight_sum = 0.0
    above = below = 0
    or_high: Optional[float] = None
    or_low: Optional[float] = None
    or_complete = False
    broke_up = broke_down = False

    for i, bar in enumerate(bars):
        if i == 0 or sessions[i] != sessions[i - 1]:
            if i > 0:
                finished_sessions.append(current_slots)
            current_slots = {}
            session_bar = 0
            pv_sum = weight_sum = 0.0
            above = below = 0
            or_high = or_low = None
            or_complete = False
            broke_up = broke_down = False
        else:
            session_bar += 1

        high, low, close = float(bar.high), float(bar.low), closes[i]
        moment = moments[i]
        clock = moment.time()

        # Session VWAP, running.
        typical = (high + low + close) / 3.0
        volume = float(getattr(bar, "volume", 0.0) or 0.0)
        weight = volume if volume_weighted else 1.0
        if volume_weighted and volume <= 0:
            weight = 0.0
        pv_sum += typical * weight
        weight_sum += weight
        if weight_sum > 0:
            session_vwap = pv_sum / weight_sum
            weighting = "volume" if volume_weighted else "time"
        else:
            # Volume-weighted asked for but nothing has traded yet this session:
            # fall back to the bar's typical price and say which it is.
            session_vwap = typical
            weighting = "time"

        if close > session_vwap:
            above += 1
        elif close < session_vwap:
            below += 1
        count = session_bar + 1
        frac_above = above / count
        frac_below = below / count

        # Opening range: the bars starting inside [open, open + N minutes).
        # The bar that completes the range is INSIDE by definition (it helped
        # set the high and low); breaks are judged on closes from the next bar.
        # If the last opening bar is missing, the range completes at the first
        # bar after the window, from whatever opening bars did arrive.
        if config.session_open_ist <= clock < or_window_end:
            or_high = high if or_high is None else max(or_high, high)
            or_low = low if or_low is None else min(or_low, low)
            if (moment + timedelta(minutes=config.bar_minutes)).time() >= or_window_end:
                or_complete = True
            or_state = OR_INSIDE if or_complete else OR_FORMING
        elif clock >= or_window_end:
            if or_high is None or or_low is None:
                or_state = OR_UNAVAILABLE
            else:
                or_complete = True
                if close > or_high:
                    or_state = OR_ABOVE
                    broke_up = True
                elif close < or_low:
                    or_state = OR_BELOW
                    broke_down = True
                else:
                    or_state = OR_INSIDE
        else:
            or_state = OR_FORMING

        # Indicators.
        point = directional[i]
        atr = point.atr
        ema_now = emas[i]
        slope = None
        if atr and ema_now is not None and i >= config.slope_lookback and emas[i - config.slope_lookback] is not None:
            slope = (ema_now - emas[i - config.slope_lookback]) / atr
        atr_pct = (atr / close * 100.0) if (atr is not None and close) else None
        distance = (close - session_vwap) / atr if atr else None

        # Volatility against the same time of day in finished sessions.
        slot = clock.strftime("%H:%M")
        vol_ratio = None
        vol_state = VOL_UNKNOWN
        if atr_pct is not None:
            history = [s[slot] for s in finished_sessions if slot in s]
            if len(history) >= config.vol_min_sessions:
                usual = median(history)
                if usual:
                    vol_ratio = atr_pct / usual
                    if vol_ratio >= config.vol_expanding:
                        vol_state = VOL_EXPANDING
                    elif vol_ratio <= config.vol_contracting:
                        vol_state = VOL_CONTRACTING
                    else:
                        vol_state = VOL_NORMAL
            current_slots[slot] = atr_pct

        # The five checks.
        di_vote = 0
        if point.adx is not None and point.adx >= config.adx_trend and point.plus_di is not None:
            if point.plus_di > point.minus_di:
                di_vote = 1
            elif point.minus_di > point.plus_di:
                di_vote = -1
        slope_vote = _vote(slope, config.slope_min_atr)
        vwap_vote = _vote(distance, config.vwap_min_atr)
        side_vote = 0
        if count >= config.side_min_bars:
            if frac_above >= config.side_fraction_min:
                side_vote = 1
            elif frac_below >= config.side_fraction_min:
                side_vote = -1
        or_vote = 1 if or_state == OR_ABOVE else (-1 if or_state == OR_BELOW else 0)
        votes = {"adx_di": di_vote, "ema_slope": slope_vote, "vwap_distance": vwap_vote,
                 "vwap_side": side_vote, "opening_range": or_vote}
        up = sum(1 for v in votes.values() if v > 0)
        down = sum(1 for v in votes.values() if v < 0)

        missing = []
        if or_state == OR_FORMING:
            # Three of the five checks cannot vote yet, which makes a trend
            # call impossible; calling these bars RANGE would be a structural
            # bias, not an observation.
            missing.append(f"opening range ({config.opening_range_minutes} min) still forming")
        if point.adx is None:
            missing.append(f"ADX({config.adx_period}) warming up")
        if slope is None:
            missing.append(f"EMA({config.ema_period}) slope warming up")
        if vol_ratio is None:
            missing.append(f"volatility baseline needs {config.vol_min_sessions} finished sessions")
        ready = not missing
        if ready:
            label, reason = _label(up, down, vol_ratio, config)
        else:
            label, reason = None, "not ready: " + "; ".join(missing)

        readings.append(RegimeReading(
            index=i,
            timestamp_utc=str(bar.timestamp_utc),
            session=sessions[i],
            session_bar=session_bar,
            label=label,
            ready=ready,
            reason=reason,
            close=close,
            adx=point.adx,
            plus_di=point.plus_di,
            minus_di=point.minus_di,
            atr=atr,
            atr_pct=atr_pct,
            ema=ema_now,
            ema_slope_atr=slope,
            vwap=session_vwap,
            vwap_weighting=weighting,
            vwap_distance_atr=distance,
            frac_above_vwap=frac_above,
            frac_below_vwap=frac_below,
            or_high=or_high if or_complete else None,
            or_low=or_low if or_complete else None,
            or_state=or_state,
            or_broke_up=broke_up,
            or_broke_down=broke_down,
            vol_ratio=vol_ratio,
            vol_state=vol_state,
            up_votes=up,
            down_votes=down,
            votes=votes,
        ))
    return readings


# ------------------------------------------------------------ session view --

@dataclass(frozen=True)
class SessionRegime:
    """
    A session at a glance.

    `dominant` is a trend label when that trend held at least
    `trend_day_share` of the ready bars and the opposite trend at most a third
    as many; otherwise the label held on the most ready bars (ties go to the
    label called first). `first_called_ist` is when the first bar carrying it
    closed and `confirmed_ist` when it had held for `confirm_bars` consecutive
    bars. Both are bar CLOSE times in IST - the moment the call was actually
    known - so the 09:25-09:30 bar reads "09:30".

    `open`/`close`/`change_pct` are hindsight columns for sanity-checking the
    labels; nothing in the classifier reads them.
    """
    session: str
    bars: int
    ready_bars: int
    counts: Dict[str, int]
    dominant: Optional[str]
    first_called_ist: Optional[str]
    confirmed_ist: Optional[str]
    first_trend: Optional[str]
    first_trend_ist: Optional[str]
    open: float
    high: float
    low: float
    close: float
    change_pct: float

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


def _known_at(reading: RegimeReading, bar_minutes: int) -> str:
    """The IST clock time the reading became known: its bar's CLOSE, not its start."""
    moment = ist_of(reading)
    return (moment + timedelta(minutes=bar_minutes)).strftime("%H:%M") if moment else "?"


def summarize_sessions(bars: Sequence[Any], readings: Sequence[RegimeReading],
                       config: Optional[RegimeConfig] = None) -> List[SessionRegime]:
    """One `SessionRegime` per session, in order."""
    config = config or RegimeConfig()
    if len(bars) != len(readings):
        raise ValueError("bars and readings must be the same length")

    groups: Dict[str, List[int]] = {}
    order: List[str] = []
    for i, reading in enumerate(readings):
        if reading.session not in groups:
            groups[reading.session] = []
            order.append(reading.session)
        groups[reading.session].append(i)

    out: List[SessionRegime] = []
    for session in order:
        idx = groups[session]
        ready = [readings[i] for i in idx if readings[i].ready and readings[i].label]
        counts = Counter(r.label for r in ready)
        first_seen: Dict[str, int] = {}
        for position, r in enumerate(ready):
            first_seen.setdefault(r.label, position)
        dominant = None
        if counts:
            up, down = counts.get(TREND_UP, 0), counts.get(TREND_DOWN, 0)
            leader, other = (TREND_UP, down) if up >= down else (TREND_DOWN, up)
            if counts.get(leader, 0) >= config.trend_day_share * len(ready) and other * 3 <= counts[leader]:
                dominant = leader
            else:
                dominant = sorted(counts, key=lambda lab: (-counts[lab], first_seen[lab]))[0]

        first_called = confirmed = None
        if dominant:
            streak = 0
            for r in ready:
                if r.label == dominant:
                    if first_called is None:
                        first_called = _known_at(r, config.bar_minutes)
                    streak += 1
                    if streak >= config.confirm_bars and confirmed is None:
                        confirmed = _known_at(r, config.bar_minutes)
                else:
                    streak = 0

        first_trend = first_trend_ist = None
        for r in ready:
            if r.label in (TREND_UP, TREND_DOWN):
                first_trend, first_trend_ist = r.label, _known_at(r, config.bar_minutes)
                break

        session_bars = [bars[i] for i in idx]
        day_open = float(session_bars[0].open)
        day_close = float(session_bars[-1].close)
        out.append(SessionRegime(
            session=session,
            bars=len(idx),
            ready_bars=len(ready),
            counts={label: counts.get(label, 0) for label in LABELS},
            dominant=dominant,
            first_called_ist=first_called,
            confirmed_ist=confirmed,
            first_trend=first_trend,
            first_trend_ist=first_trend_ist,
            open=day_open,
            high=max(float(b.high) for b in session_bars),
            low=min(float(b.low) for b in session_bars),
            close=day_close,
            change_pct=((day_close - day_open) / day_open * 100.0) if day_open else 0.0,
        ))
    return out
