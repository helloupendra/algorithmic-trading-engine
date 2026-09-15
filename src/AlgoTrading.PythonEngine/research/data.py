"""
research/data.py

Read-only data access for research, straight from the local PostgreSQL
(`algotrading_db`; host, port and credentials from the repo-root `.env`, the
same variables `market_data/historical/db_replayer.py` reads). Nothing here
writes, and no secret is ever printed.

Three sources:

  - `candles` - broker history (FYERS today): "Symbol", "Resolution"
    ('1'/'5'/'15'/'D'), "TimeStampUtc" (bar start), OHLCV, "SourceKey".
    Index symbols are the FYERS spellings (NSE:NIFTY50-INDEX).
  - `live_bars` - 1m bars built by the live ingestor ("BarStartUtc",
    "VolumeDelta"). Often partial sessions. Rolled up to the research
    resolution only for sessions `candles` does not have, and every session
    says where its bars came from.
  - `option_history_bars` - rolling-ATM option premium history written by the
    Dhan expired-options importer (built in parallel, C#). One row per bar for
    the contract that was ATM+offset at that moment. The table may be absent
    or empty; every accessor then raises `PremiumDataUnavailable` with the
    reason. Premiums are NEVER derived from the index.
"""

from __future__ import annotations

import os
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta, timezone
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

from backtest.timeutil import IST, SESSION_CLOSE_IST, SESSION_OPEN_IST, iso_utc, ist_day_end_utc, ist_day_start_utc
from core.resolutions import minutes_of, to_candle_resolution
from strategies.base_strategy import BarFrame

#: Research underlying -> the index symbol the candle store uses.
INDEX_SYMBOLS: Dict[str, str] = {
    "NIFTY": "NSE:NIFTY50-INDEX",
    "BANKNIFTY": "NSE:NIFTYBANK-INDEX",
    "FINNIFTY": "NSE:FINNIFTY-INDEX",
    "MIDCPNIFTY": "NSE:MIDCPNIFTY-INDEX",
    "SENSEX": "BSE:SENSEX-INDEX",
}

#: Current exchange lot sizes, checked against the `instruments` table on
#: 2026-09-15 (NIFTY 65, BANKNIFTY 30, SENSEX 20, FINNIFTY 60). Used only when
#: the table cannot be read. Lot sizes change over the years; a backtest far in
#: the past uses today's size, which scales rupees but not R-multiples.
FALLBACK_LOT_SIZES: Dict[str, int] = {"NIFTY": 65, "BANKNIFTY": 30, "SENSEX": 20, "FINNIFTY": 60, "MIDCPNIFTY": 120}

OPTION_TABLE = "option_history_bars"


class ResearchDataError(RuntimeError):
    """Data the research needs is not there; the message says what and how to get it."""


class PremiumDataUnavailable(ResearchDataError):
    """Option premium history is absent or empty for the request."""


# ------------------------------------------------------------- connection --

def connect():
    """
    A psycopg2 connection to the local research database.

    Reads POSTGRES_HOST/PORT/DB/USER/PASSWORD from the environment, which
    `core.config` has already filled from the repo-root `.env`.
    """
    import psycopg2  # imported here so the pure helpers work without the driver

    import core.config  # noqa: F401  (loads the repo-root .env)

    password = os.getenv("POSTGRES_PASSWORD") or os.getenv("DB_PASSWORD")
    if not password:
        raise ResearchDataError(
            "POSTGRES_PASSWORD is not set. Copy .env.example to .env at the repo root "
            "and fill in the PostgreSQL section."
        )
    try:
        return psycopg2.connect(
            host=os.getenv("POSTGRES_HOST", "localhost"),
            port=os.getenv("POSTGRES_PORT", "5432"),
            dbname=os.getenv("POSTGRES_DB", "algotrading"),
            user=os.getenv("POSTGRES_USER", "postgres"),
            password=password,
            connect_timeout=5,
        )
    except Exception as ex:  # the driver's message never contains the password
        raise ResearchDataError(
            f"cannot connect to PostgreSQL at {os.getenv('POSTGRES_HOST', 'localhost')}:"
            f"{os.getenv('POSTGRES_PORT', '5432')} ({type(ex).__name__}). Is the "
            "algotrading_db container running?"
        ) from None


def index_symbol(underlying: str) -> str:
    key = underlying.strip().upper()
    if key in INDEX_SYMBOLS:
        return INDEX_SYMBOLS[key]
    if ":" in key:
        return underlying.strip()
    raise ResearchDataError(f"no index symbol known for {underlying!r}; known: {', '.join(INDEX_SYMBOLS)}")


def lot_size(conn, underlying: str) -> int:
    """The lot size the instrument master gives the underlying's live options."""
    key = underlying.strip().upper()
    try:
        with conn.cursor() as cur:
            cur.execute(
                'SELECT "LotSize", count(*) FROM instruments '
                'WHERE "Underlying" = %s AND "OptionType" IN (\'CE\', \'PE\') '
                'AND "LotSize" IS NOT NULL AND "ExpiryDate" >= CURRENT_DATE '
                'GROUP BY 1 ORDER BY 2 DESC LIMIT 1',
                (key,),
            )
            row = cur.fetchone()
        if row and row[0]:
            return int(row[0])
    except Exception:
        conn.rollback()
    if key in FALLBACK_LOT_SIZES:
        return FALLBACK_LOT_SIZES[key]
    raise ResearchDataError(f"no lot size for {underlying!r} in instruments and no fallback")


# ------------------------------------------------------------ index bars --

#: Research index bars stop at the bar STARTING 15:15 IST (kept: it is the bar a
#: 15:15 forced exit fills on). Later bars are dropped by default because
#: nothing trades then, and because since August 2026 the FYERS index history
#: ends every session with two flat bars (15:15, 15:20) and a 15:25 bar that
#: jumps to the closing price - four of those jumps exceed 4 ATR - which would
#: inflate the next morning's ATR and with it every ATR-normalised regime check.
RESEARCH_LAST_BAR_IST = time(15, 15)


def in_nse_session(start_utc: datetime, last_bar_ist: time = RESEARCH_LAST_BAR_IST) -> bool:
    """Bar START inside 09:15 <= t <= last_bar_ist (and always < 15:30) IST."""
    clock = start_utc.astimezone(IST).time()
    return SESSION_OPEN_IST <= clock < SESSION_CLOSE_IST and clock <= last_bar_ist


def rollup(bars: Sequence[BarFrame], minutes: int, symbol: Optional[str] = None) -> List[BarFrame]:
    """
    Aggregate bars (oldest first) into `minutes` buckets aligned to the session
    open (09:15 IST), so 5m buckets start at :15, :20, ... like broker candles.
    Open = first open, close = last close, high/low = extremes, volume = sum.
    A bucket is emitted from whatever bars exist in it; completeness is the
    caller's concern (see `SessionCoverage`).
    """
    if minutes <= 0:
        raise ValueError("minutes must be positive")
    buckets: Dict[datetime, List[BarFrame]] = {}
    order: List[datetime] = []
    for bar in bars:
        start = _parse(bar.timestamp_utc)
        local = start.astimezone(IST)
        anchor = datetime.combine(local.date(), SESSION_OPEN_IST, tzinfo=IST)
        offset = int((local - anchor).total_seconds() // 60)
        bucket = anchor + timedelta(minutes=(offset // minutes) * minutes)
        if bucket not in buckets:
            buckets[bucket] = []
            order.append(bucket)
        buckets[bucket].append(bar)
    out: List[BarFrame] = []
    for bucket in sorted(order):
        group = buckets[bucket]
        out.append(BarFrame(
            symbol=symbol or group[0].symbol,
            resolution=f"{minutes}m",
            timestamp_utc=iso_utc(bucket),
            open=float(group[0].open),
            high=max(float(b.high) for b in group),
            low=min(float(b.low) for b in group),
            close=float(group[-1].close),
            volume=sum(float(b.volume or 0.0) for b in group),
        ))
    return out


def _parse(value: Any) -> datetime:
    if isinstance(value, datetime):
        return value if value.tzinfo else value.replace(tzinfo=timezone.utc)
    text = str(value)
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    parsed = datetime.fromisoformat(text)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


@dataclass
class SessionCoverage:
    session: str
    bars: int
    expected: int
    source: str            # "candles" | "live_bars rollup"
    first_ist: str
    last_ist: str

    @property
    def complete(self) -> bool:
        return self.bars >= self.expected


@dataclass
class IndexData:
    underlying: str
    symbol: str
    resolution_minutes: int
    bars: List[BarFrame]
    sessions: List[SessionCoverage]
    notes: List[str] = field(default_factory=list)

    @property
    def partial_sessions(self) -> List[SessionCoverage]:
        return [s for s in self.sessions if not s.complete]


def expected_bars(minutes: int, last_bar_ist: time = RESEARCH_LAST_BAR_IST) -> int:
    """Bars of `minutes` whose start lies in [09:15, min(last_bar_ist, 15:30))."""
    open_minute = SESSION_OPEN_IST.hour * 60 + SESSION_OPEN_IST.minute
    close_minute = SESSION_CLOSE_IST.hour * 60 + SESSION_CLOSE_IST.minute
    last_minute = min(last_bar_ist.hour * 60 + last_bar_ist.minute, close_minute - 1)
    return (last_minute - open_minute) // minutes + 1


def load_index_bars(conn, underlying: str, start: date, end: date, resolution: str = "5",
                    fill_from_live: bool = True, last_bar_ist: time = RESEARCH_LAST_BAR_IST) -> IndexData:
    """
    In-session index bars for [start, end] (IST dates, inclusive).

    Sessions come from `candles` at the requested resolution. When
    `fill_from_live`, a session with no candles but with `live_bars` 1m rows is
    rolled up from those and labelled as such. Out-of-session rows (the live
    builder has written 08:40 and 16:10 bars) are dropped, and so are bars
    starting after `last_bar_ist` (see RESEARCH_LAST_BAR_IST). Pass
    `time(15, 25)` to keep the whole session.
    """
    symbol = index_symbol(underlying)
    code = to_candle_resolution(resolution)
    minutes = minutes_of(code)
    lo, hi = ist_day_start_utc(start), ist_day_end_utc(end)

    with conn.cursor() as cur:
        cur.execute(
            'SELECT "TimeStampUtc", "Open", "High", "Low", "Close", "Volume", "SourceKey" FROM candles '
            'WHERE "Symbol" = %s AND "Resolution" = %s AND "TimeStampUtc" BETWEEN %s AND %s '
            'ORDER BY "TimeStampUtc"',
            (symbol, code, lo, hi),
        )
        rows = cur.fetchall()

    by_session: Dict[str, List[BarFrame]] = defaultdict(list)
    sources: Dict[str, set] = defaultdict(set)
    dropped_late = 0
    for ts, o, h, l, c, v, source in rows:
        if in_nse_session(ts, time(15, 29)) and not in_nse_session(ts, last_bar_ist):
            dropped_late += 1
        if not in_nse_session(ts, last_bar_ist):
            continue
        key = ts.astimezone(IST).strftime("%Y-%m-%d")
        by_session[key].append(BarFrame(symbol, f"{minutes}m", iso_utc(ts), float(o), float(h), float(l),
                                        float(c), float(v or 0)))
        sources[key].add(f"candles:{source}")

    notes: List[str] = []
    if dropped_late:
        notes.append(f"{dropped_late} candles starting after {last_bar_ist:%H:%M} IST left out (nothing trades "
                     "after the 15:15 exit; the FYERS index feed's closing bars carry flat bars and a jump to "
                     "the closing price)")
    if fill_from_live and minutes > 1:
        with conn.cursor() as cur:
            cur.execute(
                'SELECT "BarStartUtc", "Open", "High", "Low", "Close", "VolumeDelta" FROM live_bars '
                'WHERE "Symbol" = %s AND "Resolution" = \'1m\' AND "BarStartUtc" BETWEEN %s AND %s '
                'ORDER BY "BarStartUtc"',
                (symbol, lo, hi),
            )
            live = cur.fetchall()
        live_by_session: Dict[str, List[BarFrame]] = defaultdict(list)
        # Keep the minutes that make up the last kept bucket (15:15-15:19 for 5m).
        live_last = (datetime.combine(date(2000, 1, 1), last_bar_ist) + timedelta(minutes=minutes - 1)).time()
        for ts, o, h, l, c, v in live:
            if not in_nse_session(ts, live_last):
                continue
            key = ts.astimezone(IST).strftime("%Y-%m-%d")
            live_by_session[key].append(BarFrame(symbol, "1m", iso_utc(ts), float(o), float(h), float(l),
                                                 float(c), float(v or 0)))
        for key, minute_bars in live_by_session.items():
            if key in by_session:
                continue
            by_session[key] = rollup(minute_bars, minutes, symbol)
            sources[key].add("live_bars rollup")
            notes.append(f"{key}: no candles; {len(minute_bars)} live 1m bars rolled up to {minutes}m")

    bars: List[BarFrame] = []
    sessions: List[SessionCoverage] = []
    expected = expected_bars(minutes, last_bar_ist)
    for key in sorted(by_session):
        session_bars = sorted(by_session[key], key=lambda b: b.timestamp_utc)
        bars.extend(session_bars)
        sessions.append(SessionCoverage(
            session=key,
            bars=len(session_bars),
            expected=expected,
            source=",".join(sorted(sources[key])),
            first_ist=_parse(session_bars[0].timestamp_utc).astimezone(IST).strftime("%H:%M"),
            last_ist=_parse(session_bars[-1].timestamp_utc).astimezone(IST).strftime("%H:%M"),
        ))
    for s in sessions:
        if not s.complete:
            notes.append(f"{s.session}: partial session, {s.bars}/{s.expected} bars "
                         f"({s.first_ist}-{s.last_ist}, {s.source})")
    return IndexData(underlying.upper(), symbol, minutes, bars, sessions, notes)


def coverage_summary(conn, underlying: str) -> Dict[str, Any]:
    """What the local database holds for an underlying, per source and resolution."""
    symbol = index_symbol(underlying)
    out: Dict[str, Any] = {"underlying": underlying.upper(), "symbol": symbol, "candles": [], "live_bars": [],
                           "option_history_bars": None}
    with conn.cursor() as cur:
        cur.execute(
            'SELECT "Resolution", min("TimeStampUtc"), max("TimeStampUtc"), count(*), '
            'count(DISTINCT ("TimeStampUtc" AT TIME ZONE \'Asia/Kolkata\')::date) '
            'FROM candles WHERE "Symbol" = %s GROUP BY 1 ORDER BY 1',
            (symbol,),
        )
        for res, lo, hi, count, days in cur.fetchall():
            out["candles"].append({"resolution": res, "from": iso_utc(lo), "to": iso_utc(hi), "rows": count,
                                   "sessions": days})
        cur.execute(
            'SELECT "Resolution", min("BarStartUtc"), max("BarStartUtc"), count(*) FROM live_bars '
            'WHERE "Symbol" = %s GROUP BY 1 ORDER BY 1',
            (symbol,),
        )
        for res, lo, hi, count in cur.fetchall():
            out["live_bars"].append({"resolution": res, "from": iso_utc(lo), "to": iso_utc(hi), "rows": count})
    try:
        out["option_history_bars"] = option_history_coverage(conn, underlying)
    except PremiumDataUnavailable as ex:
        out["option_history_bars"] = {"available": False, "reason": str(ex)}
    return out


# --------------------------------------------------------- option premiums --

@dataclass(frozen=True)
class PremiumBar:
    """One option bar from `option_history_bars`."""
    bar_start_utc: str
    underlying: str
    expiry_flag: str
    expiry_code: int
    expiry_date: Optional[str]
    strike_offset: int
    strike: float
    option_type: str
    resolution: str
    open: float
    high: float
    low: float
    close: float
    volume: float
    open_interest: Optional[float]
    implied_volatility: Optional[float]
    spot_price: Optional[float]
    #: False for a bar rolled up from minutes in which this strike joined the
    #: stored offset window after the bar had started: its open is not the
    #: bar's open, so it cannot be an entry price (see `rollup_premiums`).
    opens_bar: bool = True


#: Contract field -> the column name the importer's contract gives it.
_OPTION_COLUMNS = ("Underlying", "ExpiryFlag", "ExpiryCode", "ExpiryDate", "StrikeOffset", "Strike", "OptionType",
                   "Resolution", "BarStartUtc", "Open", "High", "Low", "Close", "Volume", "OpenInterest",
                   "ImpliedVolatility", "SpotPrice")


def _option_columns(conn) -> Dict[str, str]:
    """
    Contract name -> the actual column, matched ignoring case and underscores so
    "ExpiryFlag", "expiryflag" and "expiry_flag" all work. Raises when the table
    is absent or a required column is missing.
    """
    with conn.cursor() as cur:
        cur.execute("SELECT to_regclass(%s)", (f"public.{OPTION_TABLE}",))
        exists = cur.fetchone()[0]
        if not exists:
            raise PremiumDataUnavailable(
                f"table {OPTION_TABLE} does not exist in the local database yet. It is created by the Dhan "
                "expired-options importer (C#); run that import first. No premium backtest is possible "
                "without it, and premiums are never estimated from the index."
            )
        cur.execute("SELECT column_name FROM information_schema.columns WHERE table_name = %s",
                    (OPTION_TABLE,))
        actual = [r[0] for r in cur.fetchall()]
    normal = {name.replace("_", "").lower(): name for name in actual}
    mapping: Dict[str, str] = {}
    missing = []
    for wanted in _OPTION_COLUMNS:
        found = normal.get(wanted.lower())
        if found is None:
            missing.append(wanted)
        else:
            mapping[wanted] = found
    required_missing = [m for m in missing if m not in ("ExpiryDate", "OpenInterest", "ImpliedVolatility",
                                                         "SpotPrice", "Volume")]
    if required_missing:
        raise PremiumDataUnavailable(
            f"{OPTION_TABLE} exists but lacks column(s) {', '.join(required_missing)}; the research code "
            "expects the importer's published contract."
        )
    return mapping


def option_history_coverage(conn, underlying: str) -> Dict[str, Any]:
    cols = _option_columns(conn)
    q = lambda name: f'"{cols[name]}"'  # noqa: E731
    with conn.cursor() as cur:
        cur.execute(
            f"SELECT {q('ExpiryFlag')}, {q('ExpiryCode')}, {q('Resolution')}, min({q('BarStartUtc')}), "
            f"max({q('BarStartUtc')}), count(*), min({q('StrikeOffset')}), max({q('StrikeOffset')}) "
            f"FROM {OPTION_TABLE} WHERE upper({q('Underlying')}) = %s GROUP BY 1, 2, 3 ORDER BY 1, 2, 3",
            (underlying.upper(),),
        )
        rows = cur.fetchall()
    if not rows:
        return {"available": False, "reason": f"{OPTION_TABLE} has no rows for {underlying.upper()}"}
    return {"available": True, "series": [
        {"expiry_flag": f, "expiry_code": c, "resolution": r, "from": iso_utc(lo), "to": iso_utc(hi),
         "rows": n, "offsets": [omin, omax]} for f, c, r, lo, hi, n, omin, omax in rows]}


def _normal_resolution(resolution: str) -> str:
    return f"{minutes_of(to_candle_resolution(resolution))}m"


def _fetch_premiums(conn, underlying: str, start: date, end: date, resolution: str, expiry_flag: str,
                    expiry_code: int, option_types: Iterable[str], offsets: Optional[Iterable[int]] = None
                    ) -> List[PremiumBar]:
    cols = _option_columns(conn)
    q = lambda name: f'"{cols[name]}"'  # noqa: E731
    optional = lambda name: q(name) if name in cols else "NULL"  # noqa: E731
    select = ", ".join([
        q("BarStartUtc"), q("Underlying"), q("ExpiryFlag"), q("ExpiryCode"), optional("ExpiryDate"),
        q("StrikeOffset"), q("Strike"), q("OptionType"), q("Resolution"), q("Open"), q("High"), q("Low"),
        q("Close"), optional("Volume"), optional("OpenInterest"), optional("ImpliedVolatility"),
        optional("SpotPrice"),
    ])
    sql = (f"SELECT {select} FROM {OPTION_TABLE} WHERE upper({q('Underlying')}) = %s "
           f"AND upper({q('ExpiryFlag')}) = %s AND {q('ExpiryCode')} = %s AND {q('Resolution')} = %s "
           f"AND upper({q('OptionType')}) = ANY(%s) AND {q('BarStartUtc')} BETWEEN %s AND %s")
    params: List[Any] = [underlying.upper(), expiry_flag.upper(), int(expiry_code), resolution,
                         [t.upper() for t in option_types], ist_day_start_utc(start), ist_day_end_utc(end)]
    if offsets is not None:
        sql += f" AND {q('StrikeOffset')} = ANY(%s)"
        params.append([int(o) for o in offsets])
    sql += f" ORDER BY {q('BarStartUtc')}, {q('StrikeOffset')}"
    with conn.cursor() as cur:
        cur.execute(sql, params)
        rows = cur.fetchall()
    out = []
    for (ts, und, flag, code, expiry, offset, strike, otype, res, o, h, l, c, v, oi, iv, spot) in rows:
        out.append(PremiumBar(
            bar_start_utc=iso_utc(ts), underlying=str(und), expiry_flag=str(flag), expiry_code=int(code),
            expiry_date=expiry.isoformat() if hasattr(expiry, "isoformat") else (str(expiry) if expiry else None),
            strike_offset=int(offset), strike=float(strike), option_type=str(otype).upper(), resolution=str(res),
            open=float(o), high=float(h), low=float(l), close=float(c), volume=float(v or 0),
            open_interest=None if oi is None else float(oi), implied_volatility=None if iv is None else float(iv),
            spot_price=None if spot is None else float(spot),
        ))
    return out


def rollup_premiums(rows: Sequence[PremiumBar], minutes: int) -> List[PremiumBar]:
    """
    1m premium rows -> `minutes` bars, grouped by (bucket, option type, STRIKE).

    Grouping by strike rather than by offset matters: within five minutes the
    ATM strike can change, and a 5m "ATM" bar stitched from two contracts is a
    price no contract ever traded at (Dhan's own 5m rolling bars are built that
    way: open/high can come from the old strike and the close from the new).

    The offset kept is the one the strike had at its FIRST minute in the bucket,
    and `opens_bar` says whether that first minute was the bucket's first
    minute - only then is the open a price a next-bar-open entry could have
    had. High/low cover only the minutes the strike was inside the stored
    window. OI/IV/spot are the last minute's.
    """
    groups: Dict[Tuple[datetime, str, float, Optional[str]], List[PremiumBar]] = defaultdict(list)
    for row in rows:
        local = _parse(row.bar_start_utc).astimezone(IST)
        anchor = datetime.combine(local.date(), SESSION_OPEN_IST, tzinfo=IST)
        offset = int((local - anchor).total_seconds() // 60)
        bucket = anchor + timedelta(minutes=(offset // minutes) * minutes)
        groups[(bucket, row.option_type, row.strike, row.expiry_date)].append(row)
    out: List[PremiumBar] = []
    for (bucket, otype, strike, expiry), group in sorted(groups.items(), key=lambda kv: (kv[0][0], kv[0][1],
                                                                                        kv[0][2])):
        group.sort(key=lambda r: r.bar_start_utc)
        first, last = group[0], group[-1]
        out.append(PremiumBar(
            bar_start_utc=iso_utc(bucket), underlying=last.underlying, expiry_flag=last.expiry_flag,
            expiry_code=last.expiry_code, expiry_date=expiry, strike_offset=first.strike_offset, strike=strike,
            option_type=otype, resolution=f"{minutes}m", open=group[0].open, high=max(r.high for r in group),
            low=min(r.low for r in group), close=last.close, volume=sum(r.volume for r in group),
            open_interest=last.open_interest, implied_volatility=last.implied_volatility,
            spot_price=last.spot_price, opens_bar=_parse(first.bar_start_utc) == bucket,
        ))
    return out


def stitched_bars(rows: Sequence[PremiumBar], minutes: int) -> int:
    """
    Native coarser-than-1m rows whose strike differs from the same offset's
    strike one bar earlier in the same session: ATM moved during (or just
    before) the bar, so its open/high/low may belong to the previous strike.
    """
    previous: Dict[Tuple[str, int, str], Tuple[datetime, float]] = {}
    count = 0
    for row in sorted(rows, key=lambda r: r.bar_start_utc):
        start = _parse(row.bar_start_utc)
        key = (row.option_type, row.strike_offset, start.astimezone(IST).strftime("%Y-%m-%d"))
        before = previous.get(key)
        if before and start - before[0] == timedelta(minutes=minutes) and before[1] != row.strike:
            count += 1
        previous[key] = (start, row.strike)
    return count


def _load_premiums(conn, underlying: str, start: date, end: date, resolution: str, expiry_flag: str,
                   expiry_code: int, option_types: Iterable[str], offsets: Optional[Iterable[int]] = None,
                   prefer_minutes: bool = False) -> Tuple[List[PremiumBar], List[str]]:
    """
    Rows at `resolution`. With `prefer_minutes` (what fixed-strike simulation
    wants) 1m rows are used whenever they exist and rolled up by strike; the
    native coarser rows are the fallback and the note counts their stitched
    bars. Without it the native rows come first and 1m is the fallback.
    """
    wanted = _normal_resolution(resolution)
    minutes = minutes_of(to_candle_resolution(wanted))
    notes: List[str] = []

    def from_minutes() -> List[PremiumBar]:
        # Fetch every offset: the strike being followed changes offset minute to minute.
        minute_rows = _fetch_premiums(conn, underlying, start, end, "1m", expiry_flag, expiry_code, option_types,
                                      None)
        if not minute_rows:
            return []
        rolled = rollup_premiums(minute_rows, minutes)
        if offsets is not None:
            keep = {int(o) for o in offsets}
            rolled = [r for r in rolled if r.strike_offset in keep]
        notes.append(f"{wanted} premium bars rolled up by strike from {len(minute_rows)} 1m rows")
        return rolled

    def session_of(row: PremiumBar) -> str:
        return _parse(row.bar_start_utc).astimezone(IST).strftime("%Y-%m-%d")

    rows: List[PremiumBar] = []
    if wanted != "1m" and prefer_minutes:
        rows = from_minutes()
    covered = {session_of(r) for r in rows}
    native = [r for r in _fetch_premiums(conn, underlying, start, end, wanted, expiry_flag, expiry_code,
                                         option_types, offsets) if session_of(r) not in covered]
    if native and wanted != "1m":
        stitched = stitched_bars(native, minutes)
        if stitched:
            notes.append(f"{stitched} native {wanted} premium bars follow an ATM change: their open/high/low may "
                         "come from the previous strike (Dhan builds them from minutes). Import 1m bars for "
                         "clean fixed-strike prices.")
    rows = sorted(rows + native, key=lambda r: (r.bar_start_utc, r.option_type, r.strike))
    if not rows and wanted != "1m" and not prefer_minutes:
        rows = from_minutes()
    if not rows:
        raise PremiumDataUnavailable(
            f"{OPTION_TABLE} has no rows for {underlying.upper()} {expiry_flag.upper()} expiry code {expiry_code} "
            f"{'/'.join(option_types)} at {wanted} (or 1m) between {start} and {end}"
            + (f" for offsets {sorted(offsets)}" if offsets is not None else "")
            + ". Import that window with the Dhan expired-options importer first."
        )
    return rows, notes


def premium_series(underlying: str, offset: int, option_type: str, start: date, end: date,
                   resolution: str = "5m", expiry_flag: str = "WEEK", expiry_code: int = 1,
                   conn=None) -> List[PremiumBar]:
    """
    The rolling contract at `offset` (0 = ATM) for [start, end] IST dates.

    This is the ROLLING series: its strike changes as spot moves. To follow one
    contract through a trade use `PremiumBook.at_strike`.
    Raises `PremiumDataUnavailable` when the table is absent or has no rows.
    """
    owned = conn is None
    conn = conn or connect()
    try:
        rows, _ = _load_premiums(conn, underlying, start, end, resolution, expiry_flag, expiry_code,
                                 [option_type], [offset])
        return rows
    finally:
        if owned:
            conn.close()


class PremiumBook:
    """
    Premium rows indexed two ways, for the simulator:

      - by (bar start, option type, offset): what was ATM+k at that bar - used to
        choose the contract at entry;
      - by (bar start, option type, strike, expiry): the SAME contract at a
        later bar, whatever offset it had drifted to - used while holding.

    A strike that has drifted further than the importer's offset window (for
    Dhan's rolling data, ATM +/- N) is simply absent at that bar; the simulator
    handles that explicitly (see research/harness.py).
    """

    def __init__(self, rows: Iterable[PremiumBar], notes: Optional[List[str]] = None) -> None:
        self._by_offset: Dict[Tuple[str, str, int], PremiumBar] = {}
        self._by_strike: Dict[Tuple[str, str, float], List[PremiumBar]] = defaultdict(list)
        self.offsets: set = set()
        self.sessions: set = set()
        self.notes: List[str] = list(notes or [])
        count = 0
        for row in rows:
            count += 1
            if row.opens_bar:
                self._by_offset[(row.bar_start_utc, row.option_type, row.strike_offset)] = row
            self._by_strike[(row.bar_start_utc, row.option_type, row.strike)].append(row)
            self.offsets.add(row.strike_offset)
            self.sessions.add(_parse(row.bar_start_utc).astimezone(IST).strftime("%Y-%m-%d"))
        self.rows = count

    def __bool__(self) -> bool:
        return self.rows > 0

    def at_offset(self, bar_start_utc: str, option_type: str, offset: int) -> Optional[PremiumBar]:
        return self._by_offset.get((bar_start_utc, option_type.upper(), int(offset)))

    def at_strike(self, bar_start_utc: str, option_type: str, strike: float,
                  expiry_date: Optional[str] = None) -> Optional[PremiumBar]:
        candidates = self._by_strike.get((bar_start_utc, option_type.upper(), float(strike)), [])
        if expiry_date is not None:
            candidates = [r for r in candidates if r.expiry_date in (None, expiry_date)]
        return candidates[0] if candidates else None

    @property
    def offset_window(self) -> Optional[Tuple[int, int]]:
        return (min(self.offsets), max(self.offsets)) if self.offsets else None


def load_premium_book(conn, underlying: str, start: date, end: date, resolution: str = "5m",
                      expiry_flag: str = "WEEK", expiry_code: int = 1,
                      option_types: Sequence[str] = ("CE", "PE")) -> PremiumBook:
    """Every offset the table holds for the window, both option types; 1m rolled up by strike when present."""
    rows, notes = _load_premiums(conn, underlying, start, end, resolution, expiry_flag, expiry_code, option_types,
                                 prefer_minutes=True)
    return PremiumBook(rows, notes)
