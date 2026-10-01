"""
The market replay's player: a trading day the desk recorded, played again.

The owner, 1 Oct 2026: "jesa system abhi chal raha hai same, mai isko jab marji ho tb replay kar sku" —
replay a day exactly as the desk ran it, whenever he likes. So the player reads that day's own ticks from
`live_ticks`, in the order they arrived, and plays them at the speed asked for:

  * to the API's replay book (POST /api/Replay/ticks), which prices the recap runs' fills and marks; and
  * to the Redis stream `market:ticks`, marked `isReplay`, which the recap runners trade on.

The book is told first, so by the time a runner acts on a tick the price it will be filled at is there.
Nothing is stored: the day is already recorded. Live prices are never touched: the book is not
`live_quotes_latest`, and live runners ignore ticks marked `isReplay`.

The player waits until every run of the replay is listening (Redis `recap:listening:<run id>`): the
runners read the stream from the moment they start, and a tick played before that is lost to them. It
reports to Redis `replay:status` once a second, takes `pause`, `resume` and `stop` from `replay:control`,
and stops on SIGTERM. When the day is played out, past the close so the runners see it, it says `finished`
and exits; the API then stops the runs at the replay's prices.

Only NSE and BSE symbols are played: the evening crude run is live on MCX while a replay plays.
"""

from __future__ import annotations

import json
import time as _time
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta, timezone
from typing import Callable, Iterable, Optional

IST = timezone(timedelta(hours=5, minutes=30))
SESSION_OPEN = time(9, 15)
SESSION_CLOSE = time(15, 30)

#: The day's ticks are those that arrived between these IST times. Bounding by arrival (the hypertable's
#: time column) keeps the read to the day's chunk, and leaves out any evening TrueData recap that
#: re-recorded the same session hours later.
RECORDED_FROM = time(9, 0)
RECORDED_UNTIL = time(15, 40)

#: Played until here, a little past the close: the runners take a tick after 15:30 as the close.
PLAY_UNTIL = time(15, 33)

#: A start time is played from this much earlier, so the first quotes are in before the session's.
LEAD = timedelta(minutes=1)

#: One query reads this much of the original day.
WINDOW = timedelta(seconds=60)

#: Ticks are sent in batches that each cover this much wall time.
BATCH_WALL_SECONDS = 0.2

#: How long the runners are waited for before the replay plays without the missing ones.
LISTEN_TIMEOUT_SECONDS = 300

SOURCE_KEY = "desk-replay"
STATUS_KEY = "replay:status"
CONTROL_KEY = "replay:control"
LISTENING_KEY = "recap:listening:{run_id}"

COLUMNS = ('"Id"', '"Symbol"', '"DataType"', '"ReceivedUtc"', '"ExchangeTimestampUtc"', '"LastTradedPrice"',
           '"BidPrice"', '"AskPrice"', '"BidSize"', '"AskSize"', '"Open"', '"High"', '"Low"', '"PrevClose"', '"Volume"')

TICKS_SQL = f"""
    SELECT {", ".join(COLUMNS)}
    FROM live_ticks
    WHERE "ReceivedUtc" >= %s AND "ReceivedUtc" < %s
      AND ("Symbol" LIKE 'NSE:%%' OR "Symbol" LIKE 'BSE:%%')
      AND "SourceKey" <> 'mock'
      AND "ExchangeTimestampUtc" IS NOT NULL
      AND "LastTradedPrice" > 0
    ORDER BY "ReceivedUtc", "Id"
"""

#: The last recorded minute of each symbol before a later start: the book's first prices, so a contract
#: that does not tick in the first minute played still has one. From the 1m bars, not the ticks: one
#: small read instead of hours of them.
PRIME_SQL = """
    SELECT DISTINCT ON ("Symbol") "Symbol", "Close", "BarStartUtc"
    FROM live_bars
    WHERE "Resolution" = '1m' AND "BarStartUtc" >= %s AND "BarStartUtc" < %s
      AND ("Symbol" LIKE 'NSE:%%' OR "Symbol" LIKE 'BSE:%%')
    ORDER BY "Symbol", "BarStartUtc" DESC
"""


def ist(day: date, at: time) -> datetime:
    return datetime.combine(day, at, IST).astimezone(timezone.utc)


def iso(moment: datetime) -> str:
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def _utc(value: datetime) -> datetime:
    return value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value.astimezone(timezone.utc)


def _num(value):
    return None if value is None else float(value)


def _int(value):
    return None if value is None else int(value)


@dataclass
class ReplayPlan:
    """What to play: the day, the speed, where in the session to start, and for which runs."""

    day: date
    speed: float
    start: time
    session: int
    runs: list[int] = field(default_factory=list)

    def __post_init__(self):
        if self.speed <= 0:
            raise ValueError("speed must be above 0")
        if not SESSION_OPEN <= self.start <= time(15, 0):
            raise ValueError("start is between 09:15 and 15:00")

    @property
    def play_from_utc(self) -> datetime:
        return max(ist(self.day, self.start) - LEAD, ist(self.day, RECORDED_FROM))

    @property
    def play_until_utc(self) -> datetime:
        return ist(self.day, PLAY_UNTIL)

    def progress(self, clock_utc: Optional[datetime]) -> float:
        if clock_utc is None:
            return 0.0
        open_, close = ist(self.day, SESSION_OPEN), ist(self.day, SESSION_CLOSE)
        return max(0.0, min(1.0, (clock_utc - open_).total_seconds() / (close - open_).total_seconds()))


def tick_from_row(row) -> dict:
    """A recorded row as a tick: the shape the API's book and the strategy stream both take."""
    (_id, symbol, data_type, _received, exchange, ltp, bid, ask, bid_size, ask_size,
     open_, high, low, prev_close, volume) = row
    return {
        "symbol": symbol,
        "dataType": data_type or "symbolUpdate",
        "exchangeTimestampUtc": iso(_utc(exchange)),
        "lastTradedPrice": _num(ltp),
        "bidPrice": _num(bid),
        "askPrice": _num(ask),
        "bidSize": _int(bid_size),
        "askSize": _int(ask_size),
        "open": _num(open_),
        "high": _num(high),
        "low": _num(low),
        "prevClose": _num(prev_close),
        "volume": _int(volume),
        "sourceKey": SOURCE_KEY,
        "isReplay": True,
    }


def read_window(conn, since_utc: datetime, until_utc: datetime) -> list[tuple]:
    """The day's ticks that arrived in [since, until), in arrival order."""
    with conn.cursor() as cur:
        cur.execute(TICKS_SQL, (since_utc, until_utc))
        return cur.fetchall()


def read_prime(conn, plan: ReplayPlan) -> list[dict]:
    """Each symbol's last recorded minute before the start, as a tick; nothing for a start at the open."""
    start = ist(plan.day, plan.start)
    if start <= ist(plan.day, SESSION_OPEN):
        return []
    with conn.cursor() as cur:
        cur.execute(PRIME_SQL, (ist(plan.day, RECORDED_FROM), start - LEAD))
        rows = cur.fetchall()
    return [{
        "symbol": symbol,
        "dataType": "symbolUpdate",
        # The minute's close, stamped at its last second.
        "exchangeTimestampUtc": iso(_utc(bar_start) + timedelta(seconds=59)),
        "lastTradedPrice": _num(close),
        "sourceKey": SOURCE_KEY,
        "isReplay": True,
    } for symbol, close, bar_start in rows if close and float(close) > 0]


def batches(rows: Iterable[tuple], speed: float):
    """Rows grouped so that each group plays within BATCH_WALL_SECONDS: (first arrival, rows)."""
    span = timedelta(seconds=BATCH_WALL_SECONDS * speed)
    group: list[tuple] = []
    first: Optional[datetime] = None
    for row in rows:
        received = _utc(row[3])
        if first is not None and received - first >= span:
            yield first, group
            group, first = [], None
        if first is None:
            first = received
        group.append(row)
    if group:
        yield first, group


class Stopped(Exception):
    """The replay was asked to stop."""


class Player:
    """
    Plays one day. Every outside thing is handed in, so a test plays a day with no database, Redis, API
    or wall clock: `read` gives the rows of a window, `prime` the first prices, `post` sends ticks to the
    book (and answers whether the API said its book was opened again), `publish` to the stream, `report`
    writes the status, `control` reads the last command, `listening` says whether a run is listening,
    `stop_requested` whether to stop.
    """

    def __init__(self, plan: ReplayPlan, *, read: Callable, prime: Callable, post: Callable, publish: Callable,
                 report: Callable, control: Callable, listening: Callable, stop_requested: Callable[[], bool],
                 clock: Callable[[], float] = _time.monotonic, sleep: Callable[[float], None] = _time.sleep,
                 log: Callable[[str], None] = print, listen_timeout: float = LISTEN_TIMEOUT_SECONDS):
        self.plan = plan
        self._read, self._prime, self._post, self._publish = read, prime, post, publish
        self._report, self._control, self._listening, self._stop = report, control, listening, stop_requested
        self._clock, self._sleep, self._log = clock, sleep, log
        self._listen_timeout = listen_timeout
        self.state = "waiting"
        self.ticks_sent = 0
        self.clock_utc: Optional[datetime] = None
        self.error: Optional[str] = None
        self._last_report = -1e9
        self._last_control = -1e9
        self._paused = False
        self._pause_started = 0.0
        self._paused_total = 0.0
        #: The latest tick of every symbol sent to the book so far, to send it again should the API lose its book.
        self._latest: dict[str, dict] = {}

    # ---------------------------------------------------------------- status

    def status(self) -> dict:
        return {
            "session": self.plan.session,
            "date": self.plan.day.isoformat(),
            "speed": self.plan.speed,
            "state": self.state,
            "clockUtc": iso(self.clock_utc) if self.clock_utc else None,
            "ticksSent": self.ticks_sent,
            "progress": round(1.0 if self.state == "finished" else self.plan.progress(self.clock_utc), 4),
            "error": self.error,
            "updatedUtc": iso(datetime.now(timezone.utc)),
        }

    def _say(self, force: bool = False) -> None:
        now = self._clock()
        if force or now - self._last_report >= 1.0:
            self._last_report = now
            try:
                self._report(self.status())
            except Exception as ex:  # a missed status is not worth the replay
                self._log(f"[replay] could not write the status: {ex}")

    # ---------------------------------------------------------------- the run

    def run(self) -> str:
        try:
            self._say(force=True)
            self._wait_for_runners()
            first = self._prime(self.plan)
            if first:
                self._book(first)
                self._log(f"[replay] primed the book with {len(first)} prices from before {self.plan.start:%H:%M}")
            self.state = "playing"
            self._say(force=True)
            self._play()
            self.state = "finished"
            self._log(f"[replay] {self.plan.day} played out: {self.ticks_sent:,} ticks")
        except Stopped:
            self.state = "stopped"
            self._log("[replay] stopped")
        except Exception as ex:
            self.state = "failed"
            self.error = f"{type(ex).__name__}: {ex}"
            self._log(f"[replay] FAILED: {self.error}")
        self._say(force=True)
        return self.state

    def _wait_for_runners(self) -> None:
        waiting = set(self.plan.runs)
        started = self._clock()
        while waiting:
            if self._stop():
                raise Stopped()
            waiting = {run for run in waiting if not self._listening(run)}
            if not waiting:
                break
            if self._clock() - started >= self._listen_timeout:
                self._log(f"[replay] runs {sorted(waiting)} are not listening after {self._listen_timeout:.0f}s; "
                          f"playing without waiting for them")
                break
            self._say()
            self._sleep(1.0)
        if not waiting:
            self._log(f"[replay] every run is listening ({', '.join(str(r) for r in self.plan.runs)})")

    def _play(self) -> None:
        plan = self.plan
        origin = plan.play_from_utc
        wall_origin = self._clock()
        since = origin
        while since < plan.play_until_utc:
            until = min(since + WINDOW, plan.play_until_utc)
            for first, group in batches(self._read(since, until), plan.speed):
                self._wait_until(wall_origin + (first - origin).total_seconds() / plan.speed)
                ticks = [tick_from_row(row) for row in group]
                self._book(ticks)       # the book first: the fill a runner asks for must find this price
                self._publish(ticks)
                self.ticks_sent += len(ticks)
                stamp = max(_utc(row[4]) for row in group)
                # Only the replayed day moves the clock: a quote stamped with an earlier trade does not.
                if stamp.astimezone(IST).date() == plan.day and (self.clock_utc is None or stamp > self.clock_utc):
                    self.clock_utc = stamp
                self._say()
            since = until

    def _book(self, ticks: list[dict]) -> None:
        """
        Sends ticks to the API's book. An API restarted in the middle of a replay has lost every price, and a
        contract that does not trade again had none until it did; when its answer says the book was opened again,
        it is sent the latest price of every other symbol played so far. Only the book: the runners have had them.
        """
        reopened = self._post(ticks)
        if reopened:
            sent = {tick["symbol"] for tick in ticks}
            again = [tick for symbol, tick in self._latest.items() if symbol not in sent]
            if again:
                self._post(again)
                self._log(f"[replay] the API's book was opened again; sent it the latest price of {len(again):,} more symbols")
        for tick in ticks:
            self._latest[tick["symbol"]] = tick

    def _wait_until(self, due: float) -> None:
        """Sleeps until `due` on the wall clock, moved later by every pause; obeys pause and stop meanwhile."""
        while True:
            if self._stop():
                raise Stopped()
            self._obey()
            if not self._paused:
                remaining = due + self._paused_total - self._clock()
                if remaining <= 0:
                    return
                self._sleep(min(0.25, remaining))
            else:
                self._sleep(0.25)
            self._say()

    def _obey(self) -> None:
        """
        Reads the last command at most twice a second: a pause holds the replay's clock still; a stop ends the
        replay (the API sends it when it could not stop this process at 08:45: paused, the player posts nothing,
        and no refused post would end it).
        """
        now = self._clock()
        if now - self._last_control < 0.5:
            return
        self._last_control = now
        try:
            command = (self._control() or "").strip().lower()
        except Exception:
            command = ""
        if command == "stop":
            raise Stopped()
        if command == "pause" and not self._paused:
            self._paused, self._pause_started = True, now
            self.state = "paused"
            self._log("[replay] paused")
            self._say(force=True)
        elif command == "resume" and self._paused:
            self._paused = False
            self._paused_total += now - self._pause_started
            self.state = "playing"
            self._log("[replay] resumed")
            self._say(force=True)


def status_json(status: dict) -> str:
    return json.dumps(status, separators=(",", ":"))
