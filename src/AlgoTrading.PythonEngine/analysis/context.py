"""
analysis/context.py

Version 2's inputs: what else was known at 08:50 IST besides the index's own
sessions and India VIX. One rule holds for every one of them, and each has a
test built to fail if it were broken: nothing dated on or after the forecast
session is read, and nothing published after 08:50 that morning.

Backtestable (stored history from 2020), and so allowed into the v2 models:

  overnight global   US, Asian, oil, dollar, yield and rupee series
                     (market_global_daily), rows dated before the session
  FII positioning    FII index-futures net long in NSE's participant-wise OI
                     file (market_participant_oi), the previous session's
  breadth            NSE advances, declines and 52-week highs and lows
                     (market_breadth_daily), the previous session's; and the
                     heavyweights' previous-session returns (candles)
  events             RBI, Fed, US CPI, US jobs, Budget and India CPI days
                     (market_events), published in advance

Live-only (recorded from now on, with no history), so recorded on every
forecast's inputs under `liveOnly` and used by no model until enough live
sessions exist to test them:

  GIFT Nifty         the 08:45 snapshot's gap to NIFTY's previous close
  news               scored headlines first seen between the previous close and 08:50
  earnings           NIFTY-50 companies with results today or since the previous session

"The previous session" is the index table's previous row, the same session
v1's inputs come from. Series that close after India does (the US, oil, the
dollar, yields, the rupee) are measured *since that close*: from their last
row dated before the previous session to their last row dated before this one.
On an ordinary day that is simply their last session's move; after an Indian
holiday it adds up every session India missed, and when they did not trade
since India's close it is zero. Asia closes before India does, so its last
session is mostly inside India's previous one already; it is kept because
desks read it at 08:50, and the walk-forward can say what it is worth.
"""

from __future__ import annotations

import json
import logging
import math
import os
from bisect import bisect_left
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta, timezone
from typing import Any, Dict, FrozenSet, Iterable, List, Mapping, Optional, Sequence, Tuple

import numpy as np

from analysis.data import (HISTORY_FLOOR, SESSION_CLOSE, SESSION_OPEN, DataError, SessionSeries, _aware, _rollback,
                           is_trading_day, load_series)
from backtest.timeutil import IST
from core.option_symbol import SEED_DIR

log = logging.getLogger("analysis.context")

# ------------------------------------------------------------------ constants --

#: Series that close after India's 15:30: measured since India's previous close.
AFTER_CLOSE_RETURNS = {"spxRet": "SPX", "ndxRet": "NDX", "djiRet": "DJI", "esRet": "ES", "brentRet": "BRENT",
                       "dxyRet": "DXY", "usdinrRet": "USDINR"}
AFTER_CLOSE_CHANGES = {"usVixChange": "VIX", "us10yChange": "US10Y"}
#: Asia closes before India does: its last closed session.
ASIA_RETURNS = {"n225Ret": "N225", "hsiRet": "HSI", "ks11Ret": "KS11"}
GLOBAL_SYMBOLS: Tuple[str, ...] = tuple(sorted(set(AFTER_CLOSE_RETURNS.values()) | set(AFTER_CLOSE_CHANGES.values())
                                               | set(ASIA_RETURNS.values())))

#: A series whose last row is older than this, counted back from the session,
#: has stopped arriving, not closed for a holiday: its input is missing. Four
#: days cover a weekend and a one-day holiday on either side (Good Friday,
#: US Memorial Day); Asia gets a week for Golden Week and the Lunar New Year.
MAX_GAP_AFTER_CLOSE = timedelta(days=5)
MAX_GAP_ASIA = timedelta(days=7)

#: Ten NIFTY-50 heavyweights that were in the index from before 2020 to today,
#: so each day's set is the index's own members on that day (HDFC Ltd, merged
#: into HDFC Bank in 2023, is left out for that reason). Equal-weighted: the
#: historical weights are not stored.
HEAVYWEIGHTS: Tuple[str, ...] = ("RELIANCE", "HDFCBANK", "ICICIBANK", "INFY", "TCS", "BHARTIARTL", "ITC", "LT",
                                 "KOTAKBANK", "AXISBANK")
#: Fewer heavyweights than this with a previous session, and the inputs are missing.
MIN_HEAVYWEIGHTS = 7
#: A one-day move larger than this in a heavyweight is a split, bonus or
#: demerger in unadjusted candles (RELIANCE's 2024 bonus reads as -50%), not a
#: return: that stock sits the day out.
MAX_STOCK_MOVE_PCT = 20.0

#: NIFTY 50 as NSE listed it on 2026-09-17 (ind_nifty50list.csv). For the
#: live-only earnings count; refresh it after each March and September rebalance.
NIFTY50: Tuple[str, ...] = (
    "ADANIENT", "ADANIPORTS", "APOLLOHOSP", "ASIANPAINT", "AXISBANK", "BAJAJ-AUTO", "BAJFINANCE", "BAJAJFINSV",
    "BEL", "BHARTIARTL", "CIPLA", "COALINDIA", "DRREDDY", "EICHERMOT", "ETERNAL", "GRASIM", "HCLTECH", "HDFCBANK",
    "HDFCLIFE", "HINDALCO", "HINDUNILVR", "ICICIBANK", "ITC", "INFY", "INDIGO", "JSWSTEEL", "JIOFIN", "KOTAKBANK",
    "LT", "M&M", "MARUTI", "MAXHEALTH", "NTPC", "NESTLEIND", "ONGC", "POWERGRID", "RELIANCE", "SBILIFE",
    "SHRIRAMFIN", "SBIN", "SUNPHARMA", "TCS", "TATACONSUM", "TMPV", "TATASTEEL", "TECHM", "TITAN", "TRENT",
    "ULTRACEMCO", "WIPRO",
)

#: market_events categories the models read, by the short key the inputs use.
EVENT_CATEGORIES = {"RBI policy": "rbi", "Fed policy": "fed", "Budget": "budget", "US CPI": "usCpi",
                    "US jobs": "usJobs", "India CPI": "inCpi"}
#: Policy days that can move the whole market ...
MAJOR_EVENTS = frozenset({"rbi", "fed", "budget"})
#: ... and scheduled data prints.
DATA_RELEASES = frozenset({"usCpi", "usJobs", "inCpi"})
#: Words in a title that mark a decision nobody knew was coming (the Fed's
#: March 2020 cuts, RBI's May 2022 hike). Such an event never flags the day
#: before, and flags its own session only if it was out before that
#: morning's forecasts: the Fed's Sunday-evening cut of 15 March 2020 was
#: (02:30 IST Monday), RBI's 14:00 hike of 4 May 2022 was not.
UNANNOUNCED_MARKERS = ("unscheduled", "off-cycle")
#: When the morning's forecasts are issued (the API's ForecastScheduler).
ISSUE_TIME = time(8, 50)

#: Every column `columns` builds, in the order the inputs record them.
COLUMNS: Tuple[str, ...] = (
    "spxRet", "ndxRet", "djiRet", "esRet", "usVix", "usVixChange", "brentRet", "dxyRet", "us10yChange", "usdinrRet",
    "n225Ret", "hsiRet", "ks11Ret", "asiaRet",
    "fiiNetLong", "fiiChange1", "fiiChange5",
    "adRatio", "pctAdvancing", "netHighsLows", "netHighsLowsPct",
    "hwRet", "hwDispersion", "hwCount",
    "majorEvent", "majorEve", "majorAfter", "dataRelease",
)

#: Where each input comes from, for the message when one is missing.
SOURCES: Dict[str, str] = {
    **{k: f"market_global_daily {v}"
       for k, v in {**AFTER_CLOSE_RETURNS, **AFTER_CLOSE_CHANGES, **ASIA_RETURNS}.items()},
    "usVix": "market_global_daily VIX", "asiaRet": "market_global_daily N225/HSI/KS11",
    "fiiNetLong": "market_participant_oi FII", "fiiChange1": "market_participant_oi FII",
    "fiiChange5": "market_participant_oi FII",
    "adRatio": "market_breadth_daily NSE", "pctAdvancing": "market_breadth_daily NSE",
    "netHighsLows": "market_breadth_daily NSE", "netHighsLowsPct": "market_breadth_daily NSE",
    "hwRet": "candles (heavyweights)", "hwDispersion": "candles (heavyweights)", "hwCount": "candles (heavyweights)",
}


# ------------------------------------------------------------------- the data --

@dataclass(frozen=True)
class GlobalRow:
    day: date
    close: float


@dataclass(frozen=True)
class ParticipantDay:
    """FII index futures, in contracts, on one trading day."""

    day: date
    long: int
    short: int

    @property
    def net_long_pct(self) -> float:
        """(long − short) / (long + short), %: +100 all long, −100 all short."""
        total = self.long + self.short
        return (self.long - self.short) / total * 100.0 if total > 0 else float("nan")


@dataclass(frozen=True)
class BreadthDay:
    day: date
    advances: int
    declines: int
    unchanged: int
    traded: int
    #: None when NSE's 52-week file was not there for the day (the table stores null).
    highs: Optional[int]
    lows: Optional[int]


@dataclass(frozen=True)
class Event:
    day: date                     # the IST date of the announcement
    time_ist: Optional[time]      # None when no time was published
    category: str                 # market_events.Category
    title: str

    @property
    def key(self) -> Optional[str]:
        return EVENT_CATEGORIES.get(self.category)

    @property
    def announced(self) -> bool:
        """Known in advance: a scheduled meeting or release, not an emergency decision."""
        text = self.title.lower()
        return not any(marker in text for marker in UNANNOUNCED_MARKERS)


@dataclass
class Context:
    """Everything the v2 inputs are computed from, each list oldest first."""

    global_rows: Dict[str, List[GlobalRow]] = field(default_factory=dict)
    fii: List[ParticipantDay] = field(default_factory=list)
    breadth: List[BreadthDay] = field(default_factory=list)
    heavy: Dict[str, SessionSeries] = field(default_factory=dict)
    events: List[Event] = field(default_factory=list)
    #: NSE's full-day closures, for the session an event first reaches.
    holidays: Mapping[str, FrozenSet[date]] = field(default_factory=dict)


@dataclass
class ContextColumns:
    """The v2 inputs for a list of session dates: one array per column, and each row's events in words."""

    values: Dict[str, np.ndarray]
    events: List[str]

    def row(self, i: int) -> Dict[str, Any]:
        """Row i as the forecast's inputs record it: raw values (not the models' transforms), null when missing."""
        out: Dict[str, Any] = {}
        for name in COLUMNS:
            v = float(self.values[name][i])
            if name in ("majorEvent", "majorEve", "majorAfter", "dataRelease"):
                out[name] = bool(v) if math.isfinite(v) else None
            elif name == "hwCount":
                out[name] = int(v) if math.isfinite(v) else None
            else:
                out[name] = round(v, 4) if math.isfinite(v) else None
        out["events"] = self.events[i]
        return out


# ---------------------------------------------------------------- the columns --

def columns(days: Sequence[date], ctx: Context) -> ContextColumns:
    """
    The v2 inputs for sessions `days` (oldest first; the last may be the
    pending session). Row i is computed from what was known before days[i]
    opened, with days[i − 1] as the previous session; row 0 has none.
    """
    n = len(days)
    values = {name: np.full(n, np.nan) for name in COLUMNS}
    labels = [""] * n
    global_index = {s: ([r.day for r in rows], rows) for s, rows in ctx.global_rows.items()}
    fii_days = [r.day for r in ctx.fii]
    breadth_days = [r.day for r in ctx.breadth]
    heavy_index = {s: ([x.day for x in series.sessions], series.sessions) for s, series in ctx.heavy.items()}
    flags = event_flags(days, ctx.events, ctx.holidays)

    for i, day in enumerate(days):
        for name in ("majorEvent", "majorEve", "majorAfter", "dataRelease"):
            values[name][i] = flags[i][name]
        labels[i] = flags[i]["label"]
        if i == 0:
            continue
        prev = days[i - 1]
        for name, symbol in AFTER_CLOSE_RETURNS.items():
            values[name][i] = _since_close(global_index.get(symbol), day, prev, pct=True)
        for name, symbol in AFTER_CLOSE_CHANGES.items():
            values[name][i] = _since_close(global_index.get(symbol), day, prev, pct=False)
        values["usVix"][i] = _last_close(global_index.get("VIX"), day)
        asia = []
        for name, symbol in ASIA_RETURNS.items():
            values[name][i] = _last_session_return(global_index.get(symbol), day)
            if math.isfinite(values[name][i]):
                asia.append(values[name][i])
        values["asiaRet"][i] = float(np.mean(asia)) if asia else np.nan

        k = bisect_left(fii_days, day) - 1          # the last FII row dated before the session ...
        if k >= 0 and ctx.fii[k].day >= prev:       # ... and not older than the previous session
            net = ctx.fii[k].net_long_pct
            values["fiiNetLong"][i] = net
            if k >= 1:
                values["fiiChange1"][i] = net - ctx.fii[k - 1].net_long_pct
            if k >= 5:
                values["fiiChange5"][i] = net - ctx.fii[k - 5].net_long_pct

        k = bisect_left(breadth_days, day) - 1
        if k >= 0 and ctx.breadth[k].day >= prev:
            b = ctx.breadth[k]
            counted = b.advances + b.declines + b.unchanged
            values["adRatio"][i] = b.advances / b.declines if b.declines > 0 else np.nan
            values["pctAdvancing"][i] = b.advances / counted * 100.0 if counted > 0 else np.nan
            if b.highs is not None and b.lows is not None:
                values["netHighsLows"][i] = float(b.highs - b.lows)
                values["netHighsLowsPct"][i] = (b.highs - b.lows) / b.traded * 100.0 if b.traded > 0 else np.nan

        moves = []
        for symbol in HEAVYWEIGHTS:
            index = heavy_index.get(symbol)
            if index is None:
                continue
            stock_days, sessions = index
            k = bisect_left(stock_days, day) - 1
            if k >= 1 and sessions[k].day >= prev and sessions[k - 1].close > 0:
                move = (sessions[k].close / sessions[k - 1].close - 1.0) * 100.0
                if abs(move) <= MAX_STOCK_MOVE_PCT:
                    moves.append(move)
        values["hwCount"][i] = float(len(moves))
        if len(moves) >= MIN_HEAVYWEIGHTS:
            values["hwRet"][i] = float(np.mean(moves))
            values["hwDispersion"][i] = float(np.std(moves))
    return ContextColumns(values, labels)


def _last_before(index: Optional[Tuple[List[date], List[GlobalRow]]], day: date) -> Optional[GlobalRow]:
    if index is None:
        return None
    days, rows = index
    k = bisect_left(days, day) - 1
    return rows[k] if k >= 0 else None


def _last_close(index, day: date) -> float:
    last = _last_before(index, day)
    if last is None or day - last.day > MAX_GAP_AFTER_CLOSE:
        return np.nan
    return last.close


def _since_close(index, day: date, prev: date, pct: bool) -> float:
    """
    The move from India's previous close to this morning: the last row dated
    before `day` against the last row dated before `prev`. A US session dated
    `prev` closes after India's `prev` close, so it is part of the move.
    """
    last, anchor = _last_before(index, day), _last_before(index, prev)
    if last is None or anchor is None or day - last.day > MAX_GAP_AFTER_CLOSE:
        return np.nan
    if pct:
        return (last.close / anchor.close - 1.0) * 100.0 if anchor.close > 0 else np.nan
    return last.close - anchor.close


def _last_session_return(index, day: date) -> float:
    """The last row dated before `day` against the row before it, %."""
    if index is None:
        return np.nan
    days, rows = index
    k = bisect_left(days, day) - 1
    if k < 1 or day - rows[k].day > MAX_GAP_ASIA or rows[k - 1].close <= 0:
        return np.nan
    return (rows[k].close / rows[k - 1].close - 1.0) * 100.0


# -------------------------------------------------------------------- events --

def reaction_session(event: Event, holidays: Mapping[str, FrozenSet[date]]) -> date:
    """
    The first NSE session that can trade on an event: its own day when it is
    announced before the 15:30 close (or at no stated time), else the next
    trading day. A US print at 18:00 IST, the Fed at 23:30 and India's CPI at
    17:30 all reach the next morning; an RBI decision at 10:00 reaches that day.
    A Saturday or Sunday Budget reaches the Monday: weekend special sessions
    are not sessions the models learn from (see data.py).
    """
    day = event.day
    if event.time_ist is not None and event.time_ist >= SESSION_CLOSE:
        day += timedelta(days=1)
    while not is_trading_day("NSE", day, holidays):
        day += timedelta(days=1)
    return day


def _neighbour(day: date, step: int, holidays: Mapping[str, FrozenSet[date]]) -> date:
    day += timedelta(days=step)
    while not is_trading_day("NSE", day, holidays):
        day += timedelta(days=step)
    return day


def event_flags(days: Sequence[date], events: Iterable[Event],
                holidays: Mapping[str, FrozenSet[date]]) -> List[Dict[str, Any]]:
    """
    For each session: whether a major event (RBI, Fed, Budget) reaches it, is
    due the next session, or reached the previous one, and whether a data
    print (US CPI, US jobs, India CPI) reaches it. `label` lists them by
    category, e.g. "fed day, usCpi before". An unannounced decision flags
    the session after it, and its own session only when it came out before
    08:50 IST that morning.
    """
    by_session: Dict[date, List[Tuple[str, str]]] = {}
    for e in events:
        key = e.key
        if key is None:
            continue
        hit = reaction_session(e, holidays)
        if e.announced:
            marks = [(hit, "day"), (_neighbour(hit, -1, holidays), "before")]
        elif e.time_ist is not None and datetime.combine(e.day, e.time_ist) < datetime.combine(hit, ISSUE_TIME):
            marks = [(hit, "day")]
        else:
            marks = []
        marks.append((_neighbour(hit, 1, holidays), "after"))
        for session, when in marks:
            by_session.setdefault(session, []).append((key, when))
    out = []
    for day in days:
        hits = sorted(set(by_session.get(day, [])))
        out.append({
            "majorEvent": float(any(k in MAJOR_EVENTS and w == "day" for k, w in hits)),
            "majorEve": float(any(k in MAJOR_EVENTS and w == "before" for k, w in hits)),
            "majorAfter": float(any(k in MAJOR_EVENTS and w == "after" for k, w in hits)),
            "dataRelease": float(any(k in DATA_RELEASES and w == "day" for k, w in hits)),
            "label": ", ".join(f"{k} {w}" for k, w in hits),
        })
    return out


# ------------------------------------------------------------------- loading --

_GLOBAL_SQL = ('SELECT "Symbol", "Date", "Close" FROM market_global_daily '
               'WHERE "Symbol" = ANY(%(symbols)s) AND "Date" >= %(start)s AND "Date" <= %(end)s '
               'ORDER BY "Symbol", "Date"')
_FII_SQL = ('SELECT "Date", "FutureIndexLong", "FutureIndexShort" FROM market_participant_oi '
            'WHERE "ClientType" = \'FII\' AND "Date" >= %(start)s AND "Date" <= %(end)s ORDER BY "Date"')
_BREADTH_SQL = ('SELECT "Date", "Advances", "Declines", "Unchanged", "Traded", "Highs52w", "Lows52w" '
                'FROM market_breadth_daily WHERE "Exchange" = \'NSE\' AND "Date" >= %(start)s AND "Date" <= %(end)s '
                'ORDER BY "Date"')
_EVENTS_SQL = 'SELECT "Date", "TimeIst", "Category", "Title" FROM market_events'

#: How far before the first session the rows are read: the anchors of the
#: first "since the previous close" and the five-session FII change.
_LEAD = timedelta(days=30)


def load(conn, start: Optional[date], end: date, holidays: Mapping[str, FrozenSet[date]]) -> Context:
    """
    The context for sessions in [start, end], read-only. A table that is not
    there yet (the MarketIntelligence migration has not run, or its backfill
    has not) is a DataError naming it: version 2 cannot be tested on half its inputs.
    """
    lo = (start or HISTORY_FLOOR) - _LEAD
    params = {"start": lo, "end": end, "symbols": list(GLOBAL_SYMBOLS)}
    ctx = Context(holidays=holidays)
    for symbol, day, close in _query(conn, _GLOBAL_SQL, params, "market_global_daily"):
        if close is not None:
            ctx.global_rows.setdefault(str(symbol), []).append(GlobalRow(day, float(close)))
    ctx.fii = [ParticipantDay(day, int(lg), int(sh))
               for day, lg, sh in _query(conn, _FII_SQL, params, "market_participant_oi")]
    ctx.breadth = [BreadthDay(day, int(a), int(d), int(u), int(t), _int(h), _int(lw))
                   for day, a, d, u, t, h, lw in _query(conn, _BREADTH_SQL, params, "market_breadth_daily")]
    for symbol in HEAVYWEIGHTS:
        ctx.heavy[symbol] = load_series(conn, f"NSE:{symbol}-EQ", lo, end)
    ctx.events = load_events(conn)
    missing = [s for s in GLOBAL_SYMBOLS if s not in ctx.global_rows]
    if missing:
        log.warning("market_global_daily has no rows for %s; the inputs built from them are missing",
                    ", ".join(missing))
    return ctx


def _int(value: Any) -> Optional[int]:
    return None if value is None else int(value)


def _query(conn, sql: str, params: Dict[str, Any], table: str) -> List[tuple]:
    try:
        with conn.cursor() as cur:
            cur.execute(sql, params)
            return cur.fetchall()
    except Exception as ex:
        _rollback(conn)
        raise DataError(f"{table} is not readable ({type(ex).__name__}); version 2 needs the MarketIntelligence "
                        "tables and their backfill") from None


def load_events(conn=None, seed_dir: str = SEED_DIR) -> List[Event]:
    """
    The event calendar: the seed file (every row with its official source)
    and the market_events table (the seed plus what admins added), without
    duplicates. The file alone when the table cannot be read.
    """
    rows: Dict[Tuple[date, str, str], Event] = {}
    try:
        with open(os.path.join(seed_dir, "market_events.json"), encoding="utf-8") as handle:
            for item in json.load(handle).get("events") or []:
                e = _event(item.get("date"), item.get("timeIst"), item.get("category"), item.get("title"))
                if e is not None:
                    rows[(e.day, e.category, e.title)] = e
    except (OSError, ValueError) as ex:
        log.warning("market_events.json not readable (%s)", type(ex).__name__)
    if conn is not None:
        try:
            with conn.cursor() as cur:
                cur.execute(_EVENTS_SQL)
                for day, at, category, title in cur.fetchall():
                    e = _event(day, at, category, title)
                    if e is not None:
                        rows[(e.day, e.category, e.title)] = e
        except Exception as ex:
            _rollback(conn)
            log.warning("market_events not readable (%s); using the seed calendar only", type(ex).__name__)
    return sorted(rows.values(), key=lambda e: (e.day, e.category, e.title))


def _event(day: Any, at: Any, category: Any, title: Any) -> Optional[Event]:
    try:
        d = day if isinstance(day, date) else date.fromisoformat(str(day)[:10])
    except (TypeError, ValueError):
        return None
    t: Optional[time] = None
    if isinstance(at, time):
        t = at
    elif at:
        try:
            t = time.fromisoformat(str(at)[:5])
        except ValueError:
            t = None
    return Event(d, t, str(category or "Other").strip(), str(title or "").strip())


# --------------------------------------------------------------- live-only --

def issue_cutoff(session: date, now_utc: datetime) -> datetime:
    """What a forecast for `session` may know: now, but never past the open."""
    opened = datetime.combine(session, SESSION_OPEN, tzinfo=IST).astimezone(timezone.utc)
    return min(now_utc, opened)


def load_live_only(conn, session: date, prev_session: date, nifty_prev_close: Optional[float],
                   cutoff_utc: datetime) -> Dict[str, Any]:
    """
    The inputs that exist only from now on, as known at `cutoff_utc`: the
    GIFT Nifty gap, the news since the previous close, and the earnings load.
    Never raises: a table that is not there yet, or a part that fails, says
    so in its place, because a morning's v1 forecasts must not wait on them.
    """
    out: Dict[str, Any] = {"usedByModels": False}
    since = datetime.combine(prev_session, SESSION_CLOSE, tzinfo=IST).astimezone(timezone.utc)
    day_start = datetime.combine(session, time(0, 0), tzinfo=IST).astimezone(timezone.utc)
    for part, loader in (("giftNifty", lambda: _gift(conn, day_start, cutoff_utc, nifty_prev_close)),
                         ("news", lambda: _news(conn, since, cutoff_utc)),
                         ("earnings", lambda: _earnings(conn, session, prev_session, cutoff_utc))):
        try:
            out.update(loader())
        except Exception as ex:
            _rollback(conn)
            out[part] = f"unavailable ({type(ex).__name__})"
    return out


def _gift(conn, day_start: datetime, cutoff: datetime, prev_close: Optional[float]) -> Dict[str, Any]:
    """
    The latest snapshot the desk had taken by the cutoff this morning. Chosen
    by FetchedUtc, when the desk had it; the source's own AsOfUtc may be null.
    """
    with conn.cursor() as cur:
        cur.execute('SELECT "Price", "ChangePct", "AsOfUtc", "FetchedUtc" FROM market_quote_snapshots '
                    'WHERE "Key" = \'GIFTNIFTY\' AND "FetchedUtc" >= %s AND "FetchedUtc" <= %s '
                    'ORDER BY "FetchedUtc" DESC LIMIT 1', (day_start, cutoff))
        row = cur.fetchone()
    if row is None:
        return {"giftNifty": "no snapshot this morning"}
    price, change, as_of, fetched = row
    gap = (float(price) / prev_close - 1.0) * 100.0 if price and prev_close else None
    stamp = lambda t: None if t is None else _aware(t).strftime("%Y-%m-%dT%H:%M:%SZ")  # noqa: E731
    return {"giftNiftyGapPct": None if gap is None else round(gap, 3),
            "giftNiftyChangePct": None if change is None else round(float(change), 3),
            "giftNiftyAsOf": stamp(as_of), "giftNiftyFetchedUtc": stamp(fetched)}


def _news(conn, since: datetime, cutoff: datetime) -> Dict[str, Any]:
    """
    Headlines first seen between the previous close and the cutoff, by
    category; a score counts only if it was written before the cutoff too.
    Announcements are NIFTY-50 companies' only: the other ~2,000 companies'
    filings are not index news.
    """
    scored = '"Sentiment" IS NOT NULL AND "ScoredUtc" <= %(cutoff)s'
    params = {"since": since, "cutoff": cutoff, "symbols": list(NIFTY50)}
    news: Dict[str, Any] = {}
    with conn.cursor() as cur:
        cur.execute(f'SELECT "Category", count(*), count(*) FILTER (WHERE {scored}), '
                    f'avg("Sentiment") FILTER (WHERE {scored}), max("Importance") FILTER (WHERE {scored}) '
                    'FROM news_items WHERE "FirstSeenUtc" >= %(since)s AND "FirstSeenUtc" < %(cutoff)s '
                    'GROUP BY "Category" ORDER BY "Category"', params)
        rows = cur.fetchall()
        cur.execute(f'SELECT count(*), count(*) FILTER (WHERE {scored}), avg("Sentiment") FILTER (WHERE {scored}), '
                    f'max("Importance") FILTER (WHERE {scored}) FROM corporate_announcements '
                    'WHERE "FirstSeenUtc" >= %(since)s AND "FirstSeenUtc" < %(cutoff)s AND "Symbol" = ANY(%(symbols)s)',
                    params)
        rows.append(("nifty50 announcements", *cur.fetchone()))
    for category, n, n_scored, sentiment, importance in rows:
        news[str(category or "uncategorised")] = {
            "n": int(n or 0), "scored": int(n_scored or 0),
            "sentiment": None if sentiment is None else round(float(sentiment), 3),
            "maxImportance": None if importance is None else int(importance),
        }
    return {"news": news}


def _earnings(conn, session: date, prev_session: date, cutoff: datetime) -> Dict[str, Any]:
    """NIFTY-50 companies with results on the session, and since the previous session (weekend results included)."""
    with conn.cursor() as cur:
        cur.execute('SELECT "EventDate", count(DISTINCT "Symbol") FROM corporate_calendar '
                    'WHERE "Symbol" = ANY(%s) AND "Purpose" ILIKE %s AND "EventDate" >= %s AND "EventDate" <= %s '
                    'AND "FirstSeenUtc" <= %s GROUP BY "EventDate"',
                    (list(NIFTY50), "%result%", prev_session, session, cutoff))
        counts = {day: int(n) for day, n in cur.fetchall()}
    return {"earningsToday": counts.get(session, 0),
            "earningsSincePrev": sum(n for day, n in counts.items() if day < session)}


# ----------------------------------------------------------------- coverage --

def coverage(cols: ContextColumns, days: Sequence[date]) -> List[Tuple[str, int, Optional[date]]]:
    """(column, sessions with a value, first such session): what the report's data section shows."""
    out = []
    for name in COLUMNS:
        finite = np.isfinite(cols.values[name])
        first = next((d for d, ok in zip(days, finite) if ok), None)
        out.append((name, int(finite.sum()), first))
    return out
