"""
analysis/data.py

What the forecasts are built from, read straight from Postgres and never
written: one bar per trading session for each index and for India VIX, the
exchanges' option expiry days, and their holidays.

Sessions are 09:15-15:30 IST. They are aggregated in SQL, one query per
symbol, because this runs on a small production server and five years of
5-minute bars is ~110,000 rows an index against ~1,500 sessions: moving the
sessions, not the bars, is the difference between a query and a transfer.

Two sources, one rule for what counts as a session:

  candles     5-minute history (broker and archived live candles). The history
              every model trains on.
  live_bars   1-minute bars the live ingestor builds from ticks. The nightly
              archive turns them into candles only at 23:50 IST, so at 15:50,
              when today's forecasts are scored, today exists only here. Used
              for recent sessions the candles do not have (yet).

A session is kept only when its bars cover it: enough of them, the first near
09:15 and the last near the close. A partial day does not become a small-range
day; it is dropped, and the drop is reported with its reason. Weekends are
dropped too — the Saturday and Sunday specials (Budget days, the 2024 disaster
recovery drills) are not sessions the models should learn "the previous day"
from, and a Muhurat hour is dropped by the bar count.
"""

from __future__ import annotations

import json
import logging
import os
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta, timezone
from typing import Any, Callable, Dict, FrozenSet, Iterable, List, Mapping, Optional, Sequence, Tuple

from backtest.timeutil import IST, ist_day_start_utc
from core.option_symbol import SEED_DIR, ExpiryReference, load_reference, monthly_expiry

log = logging.getLogger("analysis.data")

SESSION_OPEN = time(9, 15)
SESSION_CLOSE = time(15, 30)


@dataclass(frozen=True)
class Underlying:
    name: str        # what the API and the page call it: NIFTY
    symbol: str      # the candle store's spelling: NSE:NIFTY50-INDEX
    exchange: str    # whose holidays and expiry weekday apply: NSE | BSE


UNDERLYINGS: Tuple[Underlying, ...] = (
    Underlying("NIFTY", "NSE:NIFTY50-INDEX", "NSE"),
    Underlying("BANKNIFTY", "NSE:NIFTYBANK-INDEX", "NSE"),
    Underlying("SENSEX", "BSE:SENSEX-INDEX", "BSE"),
)
BY_NAME: Dict[str, Underlying] = {u.name: u for u in UNDERLYINGS}

VIX_SYMBOL = "NSE:INDIAVIX-INDEX"


class DataError(RuntimeError):
    """The data a command needs is not there; the message says what is missing."""


# ------------------------------------------------------------------ sources --

@dataclass(frozen=True)
class Source:
    """Where a session's bars are read from, and what makes a session complete."""

    label: str
    table: str
    time_column: str
    resolution: str
    expected_bars: int
    min_bars: int
    #: The first bar may start this late (a missing 09:15 bar would make the
    #: next bar's open the day's open) ...
    latest_first_bar: time
    #: ... and the last no earlier than this: a day cut off at 14:35 by an
    #: archive run during the session is not a day that closed at 14:35.
    earliest_last_bar: time


#: More flat bars than this share makes a session filled-in, not traded. FYERS's
#: index history has ended every session with two flat bars since Aug 2026
#: (2 of 75); a Muhurat hour forward-filled over the day would be most of them.
MAX_FLAT_SHARE = 0.10

#: 75 five-minute bars from 09:15 to 15:25; 60 is 80% of them.
CANDLES_5M = Source("candles 5m", "candles", "TimeStampUtc", "5", 75, 60, time(9, 20), time(15, 15))
#: 375 one-minute bars; the live feed can drop a few minutes, not an hour.
LIVE_1M = Source("live_bars 1m", "live_bars", "BarStartUtc", "1m", 375, 300, time(9, 16), time(15, 25))

#: Recent days are also looked for in live_bars, for sessions not archived yet.
LIVE_LOOKBACK_DAYS = 14

#: Earliest date asked for when a caller wants "all of it".
HISTORY_FLOOR = date(2015, 1, 1)


@dataclass(frozen=True)
class DayAggregate:
    """One IST date's in-session bars, folded: what the session SQL returns per row."""

    day: date
    open: float
    high: float
    low: float
    close: float
    bars: int
    first_utc: datetime
    last_utc: datetime
    #: Bars whose high equals their low. An index prints every second, so a
    #: flat five-minute bar inside the session is a vendor filling a gap with
    #: the last price — Dhan's history is a flat line through the 2021 Muhurat
    #: day — not a quiet market.
    flat: int = 0


@dataclass(frozen=True)
class SessionBar:
    day: date
    open: float
    high: float
    low: float
    close: float
    bars: int
    source: str


@dataclass
class SessionSeries:
    """A symbol's complete sessions, oldest first, and the days left out with why."""

    symbol: str
    sessions: List[SessionBar]
    dropped: List[Tuple[date, str]] = field(default_factory=list)

    def by_day(self) -> Dict[date, SessionBar]:
        return {s.day: s for s in self.sessions}

    def closes(self) -> Dict[date, float]:
        return {s.day: s.close for s in self.sessions}

    @property
    def first(self) -> Optional[date]:
        return self.sessions[0].day if self.sessions else None

    @property
    def last(self) -> Optional[date]:
        return self.sessions[-1].day if self.sessions else None

    def before(self, day: date) -> "SessionSeries":
        """Only the sessions strictly before `day`: what was knowable that morning."""
        return SessionSeries(self.symbol, [s for s in self.sessions if s.day < day],
                             [d for d in self.dropped if d[0] < day])

    def dropped_for(self, day: date) -> Optional[str]:
        for d, why in self.dropped:
            if d == day:
                return why
        return None


#: Complete sessions, and the (day, reason) of every day left out.
Sessions = Tuple[List[SessionBar], List[Tuple[date, str]]]


# ------------------------------------------------------------- the session SQL --

#: One row per IST date: the first bar's open, the extremes, the last bar's
#: close, how many bars and the first and last bar's start. Table and column
#: names come from a `Source` constant, never from input. The index is
#: (Symbol, Resolution, time), so the inner select is a range scan.
_SESSION_SQL = """
SELECT (b.ts AT TIME ZONE 'Asia/Kolkata')::date AS day,
       (array_agg(b.o ORDER BY b.ts))[1]       AS open,
       max(b.h)                                AS high,
       min(b.l)                                AS low,
       (array_agg(b.c ORDER BY b.ts DESC))[1]  AS close,
       count(*)                                AS bars,
       min(b.ts)                               AS first_utc,
       max(b.ts)                               AS last_utc,
       count(*) FILTER (WHERE b.h = b.l)       AS flat
FROM (SELECT "{time_column}" AS ts, "Open" AS o, "High" AS h, "Low" AS l, "Close" AS c
      FROM {table}
      WHERE "Symbol" = %(symbol)s AND "Resolution" = %(resolution)s
        AND "{time_column}" >= %(from_utc)s AND "{time_column}" < %(to_utc)s) AS b
WHERE (b.ts AT TIME ZONE 'Asia/Kolkata')::time >= TIME '09:15'
  AND (b.ts AT TIME ZONE 'Asia/Kolkata')::time <  TIME '15:30'
GROUP BY 1
ORDER BY 1
"""


def session_sql(source: Source, table: Optional[str] = None) -> str:
    """The session query for `source` (`table` overrides the table name, for the SQL's own test)."""
    return _SESSION_SQL.format(table=table or source.table, time_column=source.time_column)


def fetch_aggregates(conn, source: Source, symbol: str, start: date, end: date,
                     table: Optional[str] = None) -> List[DayAggregate]:
    """`symbol`'s in-session bars folded per IST date, for [start, end] (IST, inclusive)."""
    params = {
        "symbol": symbol,
        "resolution": source.resolution,
        "from_utc": ist_day_start_utc(start),
        "to_utc": ist_day_start_utc(end + timedelta(days=1)),
    }
    with conn.cursor() as cur:
        cur.execute(session_sql(source, table), params)
        rows = cur.fetchall()
    return [DayAggregate(day, float(o), float(h), float(l), float(c), int(n), _aware(first), _aware(last), int(flat))
            for day, o, h, l, c, n, first, last, flat in rows]


def aggregate_bars(rows: Iterable[Tuple[datetime, float, float, float, float]]) -> List[DayAggregate]:
    """
    The session SQL in Python, over (bar start UTC, open, high, low, close)
    rows. It pins down what the SQL means — IST dates, 09:15 inclusive, 15:30
    exclusive — in a test that needs no database.
    """
    by_day: Dict[date, List[Tuple[datetime, float, float, float, float]]] = {}
    for row in sorted(rows, key=lambda r: _aware(r[0])):
        start = _aware(row[0])
        local = start.astimezone(IST)
        if not (SESSION_OPEN <= local.time() < SESSION_CLOSE):
            continue
        by_day.setdefault(local.date(), []).append((start, *map(float, row[1:5])))
    out = []
    for day in sorted(by_day):
        bars = by_day[day]
        out.append(DayAggregate(day, bars[0][1], max(b[2] for b in bars), min(b[3] for b in bars), bars[-1][4],
                                len(bars), bars[0][0], bars[-1][0], sum(1 for b in bars if b[2] == b[3])))
    return out


def incomplete_reason(agg: DayAggregate, source: Source, max_flat_share: float = MAX_FLAT_SHARE) -> Optional[str]:
    """Why a day is not a usable session, or None when it is."""
    if agg.day.weekday() >= 5:
        return "weekend"
    if agg.bars < source.min_bars:
        return f"{agg.bars} of {source.expected_bars} bars ({source.label})"
    first = agg.first_utc.astimezone(IST).time()
    last = agg.last_utc.astimezone(IST).time()
    if first > source.latest_first_bar:
        return f"first bar at {first:%H:%M} ({source.label})"
    if last < source.earliest_last_bar:
        return f"last bar at {last:%H:%M} ({source.label})"
    if agg.flat > max_flat_share * agg.bars or agg.high <= agg.low:
        return f"filled in: {agg.flat} of {agg.bars} bars flat ({source.label})"
    if not (agg.low > 0 and agg.low <= min(agg.open, agg.close) and agg.high >= max(agg.open, agg.close)):
        return f"inconsistent prices ({source.label})"
    return None


def to_sessions(aggs: Sequence[DayAggregate], source: Source, max_flat_share: float = MAX_FLAT_SHARE) -> Sessions:
    """Complete sessions and the (day, reason) of every day left out."""
    kept: List[SessionBar] = []
    dropped: List[Tuple[date, str]] = []
    for agg in aggs:
        why = incomplete_reason(agg, source, max_flat_share)
        if why is None:
            kept.append(SessionBar(agg.day, agg.open, agg.high, agg.low, agg.close, agg.bars, source.label))
        else:
            dropped.append((agg.day, why))
    return kept, dropped


def merge_live(candles: Sessions, live: Sessions) -> Sessions:
    """
    Candles first — the broker's candle is the official one — and a live-bars
    session only for a day the candles have no complete session for. A day
    missing from both keeps the candles' reason, or the live one's.
    """
    sessions = {s.day: s for s in candles[0]}
    reasons: Dict[date, str] = dict(candles[1])
    for s in live[0]:
        if s.day not in sessions:
            sessions[s.day] = s
            reasons.pop(s.day, None)
    for day, why in live[1]:
        if day not in sessions:
            reasons[day] = f"{reasons[day]}; {why}" if day in reasons else why
    return [sessions[d] for d in sorted(sessions)], sorted(reasons.items())


def load_series(conn, symbol: str, start: Optional[date], end: date, live_since: Optional[date] = None,
                max_flat_share: float = MAX_FLAT_SHARE) -> SessionSeries:
    """
    `symbol`'s complete sessions in [start, end]: candles, plus live_bars for
    days from `live_since` (default: LIVE_LOOKBACK_DAYS before `end`) that the
    candles do not cover.
    """
    start = start or HISTORY_FLOOR
    candles = to_sessions(fetch_aggregates(conn, CANDLES_5M, symbol, start, end), CANDLES_5M, max_flat_share)
    since = max(start, live_since or (end - timedelta(days=LIVE_LOOKBACK_DAYS)))
    live = (to_sessions(fetch_aggregates(conn, LIVE_1M, symbol, since, end), LIVE_1M, max_flat_share)
            if since <= end else ([], []))
    sessions, dropped = merge_live(candles, live)
    return SessionSeries(symbol, sessions, dropped)


# ----------------------------------------------------------------- calendars --

def load_holidays(conn=None) -> Dict[str, FrozenSet[date]]:
    """
    Full-day closures per exchange: the API's market_holidays table (the truth,
    edited from the console) over the seed file it was first filled from.
    Without a connection, or when the table cannot be read, the seed alone.
    """
    holidays: Dict[str, set] = {k: set(v) for k, v in load_reference().holidays.items()}
    if conn is not None:
        try:
            with conn.cursor() as cur:
                # MarketClosure.FullDay = 0; the other values are MCX half-days.
                cur.execute('SELECT "Exchange", "Date" FROM market_holidays WHERE "Closure" = 0')
                for exchange, day in cur.fetchall():
                    holidays.setdefault(str(exchange).upper(), set()).add(day)
        except Exception as ex:  # an older database without the table: the seed still knows this year
            _rollback(conn)
            log.warning("market_holidays not readable (%s); using the seed calendar only", type(ex).__name__)
    return {k: frozenset(v) for k, v in holidays.items()}


def is_trading_day(exchange: str, day: date, holidays: Mapping[str, FrozenSet[date]]) -> bool:
    return day.weekday() < 5 and day not in holidays.get(exchange, frozenset())


def previous_trading_day(exchange: str, day: date, holidays: Mapping[str, FrozenSet[date]]) -> date:
    prev = day - timedelta(days=1)
    while not is_trading_day(exchange, prev, holidays):
        prev -= timedelta(days=1)
    return prev


#: SEBI's one-expiry-day-per-exchange rule, from 1 Sep 2025: NSE Tuesday, BSE Thursday.
_WEEKLY_WEEKDAY = {"NSE": 1, "BSE": 3}
#: Underlyings with a weekly contract today; BANKNIFTY has had monthlies only since Nov 2024.
_WEEKLY = frozenset({"NIFTY", "SENSEX"})
#: A listed expiry in the instrument master speaks for the days this close to it.
_LISTED_REACH = timedelta(days=3)


@dataclass(frozen=True)
class ExpiryCalendar:
    """
    Whether an index option of an underlying expires on a day.

    1. Days the exchanges' bhavcopies were read through
       (SeedData/index_option_expiries.json): exactly what they recorded —
       the 2023-2025 weekday changes and every holiday shift included.
    2. Later days: the instrument master's listed expiries (it carries the
       coming ones, and some expired ones) ...
    3. ... and where it lists nothing nearby, today's weekday rule: NIFTY
       Tuesdays, SENSEX Thursdays, BANKNIFTY's last-Tuesday monthly, moved
       back over holidays. Refresh the seed file (tools/option_expiry_calendar.py)
       before relying on step 3 for long.
    """

    recorded: Mapping[str, FrozenSet[date]]
    recorded_through: Mapping[str, date]
    listed: Mapping[str, FrozenSet[date]]
    holidays: Mapping[str, FrozenSet[date]]

    def is_expiry(self, underlying: Underlying, day: date) -> bool:
        through = self.recorded_through.get(underlying.exchange)
        if through is not None and day <= through:
            return day in self.recorded.get(underlying.name, frozenset())
        listed = self.listed.get(underlying.name, frozenset())
        if day in listed:
            return True
        if any(abs(d - day) <= _LISTED_REACH for d in listed):
            return False
        return self._rule(underlying, day)

    def _rule(self, underlying: Underlying, day: date) -> bool:
        if underlying.name in _WEEKLY:
            weekday = _WEEKLY_WEEKDAY[underlying.exchange]
            nominal = day + timedelta(days=(weekday - day.weekday()) % 7)
            closed = self.holidays.get(underlying.exchange, frozenset())
            while nominal.weekday() >= 5 or nominal in closed:
                nominal -= timedelta(days=1)
            return nominal == day
        # The weekday rule alone, moved over this calendar's holidays (the database's, not only the seed's).
        rule = ExpiryReference({}, {}, self.holidays)
        return day == monthly_expiry(underlying.exchange, underlying.name, day.year, day.month, rule)


def load_expiry_calendar(conn=None, holidays: Optional[Mapping[str, FrozenSet[date]]] = None,
                         seed_dir: str = SEED_DIR) -> ExpiryCalendar:
    recorded: Dict[str, set] = {}
    through: Dict[str, date] = {}
    try:
        with open(os.path.join(seed_dir, "index_option_expiries.json"), encoding="utf-8") as handle:
            seed = json.load(handle)
    except (OSError, ValueError) as ex:
        log.warning("index_option_expiries.json not readable (%s); expiry days come from the master and the "
                    "weekday rule only", type(ex).__name__)
        seed = {}
    for exchange, coverage in (seed.get("coverage") or {}).items():
        last = _iso(coverage.get("last")) if isinstance(coverage, dict) else None
        if last is not None:
            through[str(exchange).upper()] = last
    for name, days in (seed.get("underlyings") or {}).items():
        recorded[str(name).upper()] = {d for d in (_iso(x) for x in days or []) if d is not None}

    listed: Dict[str, set] = {}
    if conn is not None:
        try:
            with conn.cursor() as cur:
                cur.execute('SELECT DISTINCT "Underlying", "ExpiryDate" FROM instruments '
                            'WHERE "Underlying" = ANY(%s) AND "OptionType" IN (\'CE\', \'PE\') '
                            'AND "ExpiryDate" IS NOT NULL',
                            ([u.name for u in UNDERLYINGS],))
                for name, expiry in cur.fetchall():
                    day = expiry.date() if isinstance(expiry, datetime) else expiry
                    listed.setdefault(str(name).upper(), set()).add(day)
        except Exception as ex:
            _rollback(conn)
            log.warning("instrument master not readable (%s); later expiries come from the weekday rule",
                        type(ex).__name__)
    return ExpiryCalendar({k: frozenset(v) for k, v in recorded.items()}, through,
                          {k: frozenset(v) for k, v in listed.items()},
                          holidays if holidays is not None else load_holidays(None))


# ---------------------------------------------------------- everything at once --

@dataclass
class MarketData:
    """The sessions, VIX, expiry calendar and holidays a command works from."""

    series: Dict[str, SessionSeries]          # underlying name -> sessions
    vix: SessionSeries
    calendar: ExpiryCalendar
    holidays: Mapping[str, FrozenSet[date]]

    def is_expiry(self, name: str) -> Callable[[date], bool]:
        underlying = BY_NAME[name]
        return lambda day: self.calendar.is_expiry(underlying, day)


def load_market(conn, names: Sequence[str], start: Optional[date], end: date, with_vix: bool = True) -> MarketData:
    holidays = load_holidays(conn)
    calendar = load_expiry_calendar(conn, holidays)
    series = {name: load_series(conn, BY_NAME[name].symbol, start, end) for name in names}
    # India VIX is recomputed in small steps, not with every trade, so a quiet one-minute VIX bar can be
    # flat without anything having been filled in. Only a VIX day with no movement at all is dropped.
    vix = (load_series(conn, VIX_SYMBOL, start, end, max_flat_share=1.0) if with_vix
           else SessionSeries(VIX_SYMBOL, []))
    return MarketData(series, vix, calendar, holidays)


def connect(readonly: bool = True):
    """
    A read-only connection to the platform database, from the repo-root .env
    (core.config loads it) the way Sentinel builds its DSN. `readonly=False`
    is for the news scorer alone, which writes its six score columns, one
    transaction per table; every other command reads.
    """
    import psycopg2

    import core.config  # noqa: F401  (fills os.environ from the repo-root .env)
    from sentinel.store import dsn_from_env

    dsn = dsn_from_env(dict(os.environ))
    if dsn is None:
        raise DataError("POSTGRES_PASSWORD is not set; add the PostgreSQL section of .env.example to the "
                        "repo-root .env")
    try:
        conn = psycopg2.connect(dsn, connect_timeout=5)
    except Exception as ex:  # the driver's message never carries the password; the host and port are enough
        raise DataError(f"cannot connect to PostgreSQL at {os.getenv('POSTGRES_HOST', 'localhost')}:"
                        f"{os.getenv('POSTGRES_PORT', '5432')} ({type(ex).__name__})") from None
    # Read-only at the session level: nothing here may write, and now nothing can.
    conn.set_session(readonly=readonly, autocommit=readonly)
    return conn


def _aware(value: Any) -> datetime:
    if isinstance(value, datetime):
        return value if value.tzinfo else value.replace(tzinfo=timezone.utc)
    text = str(value)
    parsed = datetime.fromisoformat(text[:-1] + "+00:00" if text.endswith("Z") else text)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def _iso(text: Any) -> Optional[date]:
    try:
        return date.fromisoformat(str(text)[:10])
    except (TypeError, ValueError):
        return None


def _rollback(conn) -> None:
    try:
        conn.rollback()
    except Exception:
        pass
