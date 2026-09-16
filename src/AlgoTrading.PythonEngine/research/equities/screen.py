"""
Live mover screen (stock picker blueprint, L2): which universe stocks are
trading unusually right now, with their levels.

What is tested and what is not
------------------------------
R2 (design period Sep 2024 - Dec 2025, both halves) found one selection that
works: the 10 universe stocks with the highest relative volume in the first 15
minutes moved 1.57x / 1.77x as much as an average stock from 09:30 to 15:15, and
1.53x / 1.69x their own normal. `ScreenState` freezes exactly that list at the
first quote at or after 09:30.

The "relative volume now" ranking carries the same idea through the day. It has
NOT been tested, and the screen says so.

The screen shows levels, never buy or sell calls: R3 found no entry rule on
these stocks that pays after costs.

Inputs
------
- A baseline per stock, built after the close from the bhavcopy (levels, ATR,
  universe rules) and the stock's own 5-minute bars (volume by time of day over
  its last 20 sessions). See `baseline_from_sessions`.
- Dhan market quotes (`POST /v2/marketfeed/quote`, up to 1,000 instruments a
  call, 1 call a second per account). The quote's `ohlc.close` is the previous
  close during the session but today's close after it, so the previous close
  always comes from the baseline, never from the quote.
"""

from __future__ import annotations

import html
import math
from urllib.parse import quote as urlquote
from dataclasses import asdict, dataclass, field
from datetime import datetime, time as dtime
from typing import Dict, Iterable, List, Optional, Sequence

import numpy as np

from research.equities.setups import OR_BARS, SessionBars

OPEN = dtime(9, 15)
FREEZE_AT = dtime(9, 30)
FREEZE_UNTIL = dtime(9, 45)    # a list frozen later than this is no longer the one R2 tested
CLOSE = dtime(15, 30)
SLOTS = 75                     # 5-minute bars 09:15 ... 15:25
LOOKBACK = 20
MIN_SESSIONS = 10
FROZEN_SIZE = 10


def slot_of(hhmm: str) -> Optional[int]:
    """09:15 -> 0, 09:20 -> 1, ... 15:25 -> 74; None outside the session."""
    h, m = int(hhmm[:2]), int(hhmm[3:5])
    minutes = (h * 60 + m) - (9 * 60 + 15)
    if minutes < 0 or minutes % 5 or minutes // 5 >= SLOTS:
        return None
    return minutes // 5


# ----------------------------------------------------------------- baseline --

@dataclass
class Baseline:
    symbol: str
    security_id: str
    isin: str
    basis_date: str               # the last session the numbers describe
    prev_close: float
    prev_high: float
    prev_low: float
    atr_pct: float
    median_turnover: float
    or15_volume: Optional[float]  # average first-15-minute volume, same rule as R2
    cum_volume: List[float]       # average cumulative volume at the end of each 5-minute slot
    sessions: int
    asm_gsm: str = ""


def baseline_from_sessions(sessions: Dict[str, SessionBars], before: str, lookback: int = LOOKBACK,
                           min_sessions: int = MIN_SESSIONS) -> Dict[str, object]:
    """
    Volume facts from a stock's sessions strictly before `before` (YYYY-MM-DD).

    or15_volume follows R2's rvol15 exactly: the last `lookback` sessions, of
    which only those whose first three bars are 09:15, 09:20 and 09:25 count,
    and at least `min_sessions` of them must. cum_volume averages, over the same
    sessions, the running volume at the end of every 5-minute slot; a bar the
    exchange never printed adds nothing.
    """
    days = sorted(d for d in sessions if d < before)[-lookback:]
    full = [sessions[d] for d in days if sessions[d].times[:OR_BARS] == ["09:15", "09:20", "09:25"]]
    if len(full) < min_sessions:
        return {"or15_volume": None, "cum_volume": [], "sessions": len(full)}
    or15 = float(np.mean([s.volume[:OR_BARS].sum() for s in full]))
    curves = []
    for s in full:
        per_slot = np.zeros(SLOTS)
        for t, v in zip(s.times, s.volume):
            k = slot_of(t)
            if k is not None:
                per_slot[k] += v
        curves.append(np.cumsum(per_slot))
    cum = np.mean(curves, axis=0)
    return {"or15_volume": or15, "cum_volume": [round(float(x), 1) for x in cum], "sessions": len(full)}


def expected_volume(cum_volume: Sequence[float], now: datetime) -> Optional[float]:
    """The stock's usual running volume at `now`, interpolated inside the 5-minute slot."""
    if not cum_volume:
        return None
    minutes = (now.hour * 60 + now.minute + now.second / 60.0) - (9 * 60 + 15)
    if minutes <= 0:
        return None
    k, into = divmod(minutes, 5.0)
    k = int(k)
    if k >= len(cum_volume):
        return float(cum_volume[-1])
    before = float(cum_volume[k - 1]) if k >= 1 else 0.0
    return before + (float(cum_volume[k]) - before) * into / 5.0


# ------------------------------------------------------------------- quotes --

@dataclass
class Quote:
    security_id: str
    ltp: float
    open: float
    high: float
    low: float
    volume: float
    upper_circuit: Optional[float]
    lower_circuit: Optional[float]
    last_trade_time: str


def _f(value) -> Optional[float]:
    try:
        x = float(value)
    except (TypeError, ValueError):
        return None
    return x if math.isfinite(x) else None


def parse_quotes(payload: dict, segment: str = "NSE_EQ") -> Dict[str, Quote]:
    """Dhan's `{"data": {"NSE_EQ": {"2885": {...}}}, "status": "success"}` -> Quote per security id."""
    out: Dict[str, Quote] = {}
    data = (payload or {}).get("data") or {}
    for sid, q in (data.get(segment) or {}).items():
        if not isinstance(q, dict):
            continue
        ohlc = q.get("ohlc") or {}
        ltp = _f(q.get("last_price"))
        if not ltp or ltp <= 0:
            continue
        out[str(sid)] = Quote(str(sid), ltp, _f(ohlc.get("open")) or 0.0, _f(ohlc.get("high")) or 0.0,
                              _f(ohlc.get("low")) or 0.0, _f(q.get("volume")) or 0.0,
                              _f(q.get("upper_circuit_limit")), _f(q.get("lower_circuit_limit")),
                              str(q.get("last_trade_time") or ""))
    return out


# -------------------------------------------------------------------- screen --

@dataclass
class Row:
    symbol: str
    security_id: str
    ltp: float
    change_pct: float
    move_atr: Optional[float]
    gap_pct: Optional[float]
    range_pct: Optional[float]
    volume: float
    rvol_now: Optional[float]
    prev_high: float
    prev_low: float
    flags: List[str] = field(default_factory=list)


@dataclass
class FrozenRow:
    symbol: str
    security_id: str
    rvol15: float
    price_0930: float
    or_high: float
    or_low: float
    prev_high: float
    prev_low: float
    ltp: Optional[float] = None
    since_0930_pct: Optional[float] = None
    flags: List[str] = field(default_factory=list)


@dataclass
class Snapshot:
    at: str
    rows: List[Row]
    frozen: Optional[List[FrozenRow]]
    frozen_note: str
    quoted: int
    universe: int
    basis_date: str


def _flags(b: Baseline, q: Quote) -> List[str]:
    flags = []
    if q.upper_circuit and q.ltp >= q.upper_circuit * 0.9995:
        flags.append("upper circuit")
    if q.lower_circuit and q.ltp <= q.lower_circuit * 1.0005:
        flags.append("lower circuit")
    if q.ltp > b.prev_high:
        flags.append("above prev high")
    elif q.ltp < b.prev_low:
        flags.append("below prev low")
    if b.asm_gsm and b.asm_gsm.upper() not in ("N", "NA", ""):
        flags.append("ASM/GSM")
    return flags


class ScreenState:
    """Baselines plus what the day has fixed so far (the 09:30 list)."""

    def __init__(self, baselines: Iterable[Baseline]):
        self.baselines: Dict[str, Baseline] = {b.security_id: b for b in baselines}
        self.frozen: Optional[List[FrozenRow]] = None
        self.frozen_note = "The 09:30 list is taken from the first quote at or after 09:30."

    def update(self, quotes: Dict[str, Quote], now: datetime) -> Snapshot:
        rows: List[Row] = []
        for sid, b in self.baselines.items():
            q = quotes.get(sid)
            if q is None or not b.prev_close:
                continue
            change = (q.ltp / b.prev_close - 1.0) * 100.0
            expected = expected_volume(b.cum_volume, now)
            rows.append(Row(
                b.symbol, sid, q.ltp, change,
                change / b.atr_pct if b.atr_pct else None,
                (q.open / b.prev_close - 1.0) * 100.0 if q.open else None,
                (q.high - q.low) / b.prev_close * 100.0 if q.high and q.low else None,
                q.volume,
                q.volume / expected if expected else None,
                b.prev_high, b.prev_low, _flags(b, q)))
        rows.sort(key=lambda r: -(r.rvol_now if r.rvol_now is not None else -1.0))
        self._maybe_freeze(quotes, now)
        if self.frozen is not None:
            for f in self.frozen:
                q = quotes.get(f.security_id)
                if q is not None:
                    f.ltp = q.ltp
                    f.since_0930_pct = (q.ltp / f.price_0930 - 1.0) * 100.0 if f.price_0930 else None
                    f.flags = _flags(self.baselines[f.security_id], q)
        return Snapshot(now.isoformat(timespec="seconds"), rows, self.frozen, self.frozen_note, len(quotes),
                        len(self.baselines), max((b.basis_date for b in self.baselines.values()), default=""))

    def _maybe_freeze(self, quotes: Dict[str, Quote], now: datetime) -> None:
        if self.frozen is not None or now.time() < FREEZE_AT:
            return
        if now.time() > FREEZE_UNTIL:
            if self.frozen is None:
                self.frozen = []
                self.frozen_note = ("No 09:30 list today: the first quotes came after 09:45, and a list taken later "
                                    "is not the one R2 tested.")
            return
        picks = []
        for sid, b in self.baselines.items():
            q = quotes.get(sid)
            if q is None or not b.or15_volume:
                continue
            picks.append(FrozenRow(b.symbol, sid, q.volume / b.or15_volume, q.ltp, q.high, q.low,
                                   b.prev_high, b.prev_low))
        picks.sort(key=lambda f: -f.rvol15)
        self.frozen = picks[:FROZEN_SIZE]
        self.frozen_note = (f"Frozen at {now:%H:%M:%S}: the {len(self.frozen)} universe stocks with the highest "
                            "first-15-minute volume against their own last 20 sessions. OR high/low = the day's "
                            "high/low at that moment.")


# -------------------------------------------------------------------- output --

def record(snapshot: Snapshot, quotes: Dict[str, Quote]) -> dict:
    """One JSON line a poll: every quote in compact form, so the day can be studied later."""
    return {
        "at": snapshot.at,
        "basis_date": snapshot.basis_date,
        "quotes": [[q.security_id, q.ltp, q.volume, q.high, q.low, q.open] for q in quotes.values()],
        "frozen": [f.symbol for f in snapshot.frozen] if snapshot.frozen else None,
        "top_now": [r.symbol for r in snapshot.rows[:20]],
    }


def _n(x: Optional[float], digits: int = 2, suffix: str = "") -> str:
    return "–" if x is None else f"{x:,.{digits}f}{suffix}"


def _signed(x: Optional[float], digits: int = 2, suffix: str = "%") -> str:
    return "–" if x is None else f"{x:+,.{digits}f}{suffix}"


def _tone(x: Optional[float]) -> str:
    return "" if x is None or x == 0 else (" up" if x > 0 else " down")


def _chart(symbol: str) -> str:
    url = "https://www.tradingview.com/chart/?symbol=" + urlquote(f"NSE:{symbol}", safe="")
    return f"<a href='{html.escape(url)}' target='_blank' rel='noopener'>{html.escape(symbol)}</a>"


def _when(at: str) -> str:
    try:
        return datetime.fromisoformat(at).strftime("%d %b %Y, %H:%M:%S IST")
    except ValueError:
        return at


def render_html(snapshot: Snapshot, top: int = 20, refresh_seconds: int = 30, status: str = "") -> str:
    e = html.escape
    frozen_rows = ""
    if snapshot.frozen:
        for i, f in enumerate(snapshot.frozen, 1):
            frozen_rows += (
                f"<tr><td class='num'>{i}</td><td><b>{_chart(f.symbol)}</b></td><td class='num'>{_n(f.rvol15, 1, '×')}</td>"
                f"<td class='num'>{_n(f.price_0930)}</td><td class='num'>{_n(f.ltp)}</td>"
                f"<td class='num{_tone(f.since_0930_pct)}'>{_signed(f.since_0930_pct)}</td>"
                f"<td class='num'>{_n(f.or_high)} / {_n(f.or_low)}</td><td class='num'>{_n(f.prev_high)} / {_n(f.prev_low)}</td>"
                f"<td>{e(', '.join(f.flags))}</td></tr>")
    frozen_body = (f"<table><thead><tr><th>#</th><th>Stock</th><th>Rel. volume 09:15–09:30</th><th>Price 09:30</th>"
                   f"<th>Now</th><th>Since 09:30</th><th>OR high / low</th><th>Prev day high / low</th><th>Flags</th>"
                   f"</tr></thead><tbody>{frozen_rows}</tbody></table>") if snapshot.frozen else ""
    frozen_empty = "Waiting for 09:30." if snapshot.frozen is None else "No list today."
    live_rows = ""
    for i, r in enumerate(snapshot.rows[:top], 1):
        live_rows += (
            f"<tr><td class='num'>{i}</td><td><b>{_chart(r.symbol)}</b></td><td class='num'>{_n(r.ltp)}</td>"
            f"<td class='num{_tone(r.change_pct)}'>{_signed(r.change_pct)}</td>"
            f"<td class='num{_tone(r.move_atr)}'>{_signed(r.move_atr, 1, '')}</td>"
            f"<td class='num'>{_n(r.rvol_now, 1, '×')}</td><td class='num'>{_n(r.range_pct, 2, '%')}</td>"
            f"<td class='num'>{_signed(r.gap_pct)}</td><td class='num'>{_n(r.prev_high)} / {_n(r.prev_low)}</td>"
            f"<td>{e(', '.join(r.flags))}</td></tr>")
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="{int(refresh_seconds)}">
<title>Mover Screen</title>
<style>
:root {{ --bg:#F5F6F4; --surface:#FFFFFF; --text:#1A211C; --muted:#5F6B63; --line:#D6DCD5; --head:#ECEFEA;
  --up:#1F6F4A; --down:#B3261E; --note:#E2ECF4; --note-line:#25557A; }}
@media (prefers-color-scheme: dark) {{ :root:not([data-theme="light"]) {{ --bg:#101412; --surface:#171C19; --text:#E5EAE6;
  --muted:#98A49C; --line:#2B332E; --head:#1F2622; --up:#6FCB9A; --down:#F08B82; --note:#152838; --note-line:#7FB3DB; }} }}
:root[data-theme="dark"] {{ --bg:#101412; --surface:#171C19; --text:#E5EAE6; --muted:#98A49C; --line:#2B332E;
  --head:#1F2622; --up:#6FCB9A; --down:#F08B82; --note:#152838; --note-line:#7FB3DB; }}
* {{ box-sizing:border-box; }}
body {{ margin:0; background:var(--bg); color:var(--text); font:14px/1.5 -apple-system,"Segoe UI",system-ui,sans-serif; padding:20px 16px 48px; }}
.page {{ max-width:1200px; margin:0 auto; }}
h1 {{ font-size:1.4rem; margin:0 0 4px; }} h2 {{ font-size:1.05rem; margin:28px 0 6px; }}
.muted {{ color:var(--muted); }}
.note {{ border-left:3px solid var(--note-line); background:var(--note); padding:8px 12px; border-radius:0 8px 8px 0; margin:8px 0; }}
.wrap {{ overflow-x:auto; border:1px solid var(--line); border-radius:10px; background:var(--surface); }}
table {{ border-collapse:collapse; width:100%; }}
th,td {{ padding:7px 10px; border-bottom:1px solid var(--line); text-align:left; white-space:nowrap; }}
th {{ background:var(--head); color:var(--muted); font:600 11.5px/1.3 ui-monospace,Menlo,monospace; text-transform:uppercase; }}
td.num {{ font-family:ui-monospace,Menlo,monospace; text-align:right; }}
.up {{ color:var(--up); }} .down {{ color:var(--down); }}
a {{ color:inherit; text-decoration:none; }} a:hover {{ text-decoration:underline; }}
</style></head><body><div class="page">
<h1>Mover Screen</h1>
<div class="muted">Quotes at {e(_when(snapshot.at))} · {snapshot.quoted} of {snapshot.universe} universe stocks quoted · levels from the {e(snapshot.basis_date)} session · refreshes every {int(refresh_seconds)} s{(' · ' + e(status)) if status else ''}</div>
<div class="note">Levels to watch, not buy or sell calls. Research found that these lists pick stocks that move more; it found no simple entry rule on them that pays after costs.</div>
<h2>09:30 list · tested</h2>
<div class="muted">{e(snapshot.frozen_note)} In 2024–2025 this list moved 1.6–1.8× an average stock for the rest of the day.</div>
<div class="wrap">{frozen_body or f'<p class="muted" style="padding:10px 12px;margin:0">{frozen_empty}</p>'}</div>
<h2>Relative volume now · not tested</h2>
<div class="muted">Today's volume against the stock's usual volume by this time of day (last 20 sessions). Move in ATRs = today's change ÷ the stock's 14-day ATR%. A stock's name opens its chart. After 15:10 it overstates about 200 F&amp;O stocks: since 3 Aug 2026 Dhan's history has no 5-minute bars for them after 15:10, so their usual volume stops growing there.</div>
<div class="wrap"><table><thead><tr><th>#</th><th>Stock</th><th>LTP</th><th>Change</th><th>Move (ATRs)</th><th>Rel. volume</th><th>Day range</th><th>Gap</th><th>Prev day high / low</th><th>Flags</th></tr></thead>
<tbody>{live_rows}</tbody></table></div>
</div></body></html>
"""


def baseline_to_json(b: Baseline) -> dict:
    return asdict(b)


def baseline_from_json(d: dict) -> Baseline:
    return Baseline(**d)
