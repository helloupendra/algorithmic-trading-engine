"""
What a live run does differently when its prices are a replay of a past session.

TrueData replays the day's feed in the evening ("recap"), tick for tick, with
the exchange's own timestamps. A strategy started on it is an ordinary live
paper run in every respect but one: the platform already holds that whole day,
because it happened this morning. Three places would hand the strategy prices
from later in the day than the replay has reached, and a run that can see the
close before it trades the open is not a test of anything:

  * warm-up, which asks for fifteen days of bars up to today;
  * the recent-bars read on every tick, which returns the newest bars stored,
    and this morning's real bars are newer than a replay at 10:00;
  * the end of the session, which live runs reach at the 15:30 wall-clock stop,
    long past by the time a recap is playing.

This class answers all three from the replay's own clock — the timestamps on
its ticks — and nothing else. It never guesses a clock: a tick stamped outside
the session it is replaying is refused, loudly, rather than fed through.
"""

from datetime import date, datetime, time, timedelta, timezone

IST = timezone(timedelta(hours=5, minutes=30))
SESSION_OPEN = time(9, 15)
SESSION_CLOSE = time(15, 30)


def _parse_utc(stamp) -> datetime | None:
    if stamp is None or stamp == "":
        return None
    if isinstance(stamp, datetime):
        value = stamp
    else:
        try:
            value = datetime.fromisoformat(str(stamp).replace("Z", "+00:00"))
        except ValueError:
            return None
    if value.tzinfo is None:
        value = value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


class RecapSession:
    """One replayed trading day, and the clock that belongs to it."""

    #: How close to 15:30 the replay must already be before a later stamp is
    #: believed to be the close. TrueData sends a touchline snapshot on every
    #: subscribe, stamped with the time it was sent — 17:31 on the first
    #: evening — and the first version of this class took that for the replayed
    #: close and stopped all three runs within a second of the open.
    CLOSE_APPROACH = timedelta(minutes=5)
    #: The largest step between two ticks that still counts as the replay
    #: moving forward rather than a stamp from somewhere else.
    MAX_STEP = timedelta(minutes=15)

    def __init__(self, day: date):
        self.day = day
        self.last_in_session_utc: datetime | None = None
        self.open_utc = datetime.combine(day, SESSION_OPEN, IST).astimezone(timezone.utc)
        self.close_utc = datetime.combine(day, SESSION_CLOSE, IST).astimezone(timezone.utc)
        self.start_of_day_utc = datetime.combine(day, time(0, 0), IST).astimezone(timezone.utc)

    @classmethod
    def from_params(cls, run_params) -> "RecapSession | None":
        """
        A recap run is marked explicitly: {"session": "recap", "recap_date": "yyyy-mm-dd"}.

        The date is required rather than taken from the wall clock. The replay
        runs past midnight, and a run whose idea of "today" changes at 00:00
        would start refusing the very session it is replaying.
        """
        if not isinstance(run_params, dict):
            return None
        if str(run_params.get("session") or "").strip().lower() != "recap":
            return None
        raw = str(run_params.get("recap_date") or "").strip()
        try:
            day = date.fromisoformat(raw)
        except ValueError:
            raise ValueError(
                f"a recap run needs recap_date as yyyy-mm-dd, got {raw!r}; "
                "it is not taken from the clock because the replay runs past midnight")
        return cls(day)

    # ---------------------------------------------------------------- warm-up

    def warmup_end_date(self) -> str:
        """The last day warm-up may read: the one before the replayed session."""
        return (self.day - timedelta(days=1)).isoformat()

    def before_session(self, bar_start) -> bool:
        """True for a bar that began before the replayed day — warm-up may keep it."""
        at = _parse_utc(bar_start)
        return at is not None and at < self.start_of_day_utc

    # ------------------------------------------------------------ live ticks

    def tick_time(self, stamp) -> datetime | None:
        return _parse_utc(stamp)

    def in_session(self, stamp) -> bool:
        at = _parse_utc(stamp)
        return at is not None and self.open_utc <= at < self.close_utc

    def note_in_session(self, stamp) -> None:
        """Record the replay's progress: the latest tick accepted inside the session."""
        at = _parse_utc(stamp)
        if at is not None and self.in_session(at):
            if self.last_in_session_utc is None or at > self.last_in_session_utc:
                self.last_in_session_utc = at

    def reached_close(self, stamp) -> bool:
        """
        The replay has played its way to 15:30.

        A stamp after 15:30 is not enough on its own: the replay must already
        have been trading within a few minutes of the close, and this tick must
        follow on from it. A snapshot stamped with the wall clock, a stamp from
        another day, or anything that jumps hours ahead of the replay is not the
        close — it is a timestamp this class cannot place, and a run stopped on
        one is a run lost to a clock.
        """
        at = _parse_utc(stamp)
        last = self.last_in_session_utc
        if at is None or last is None:
            return False
        if not (self.close_utc <= at < self.start_of_day_utc + timedelta(days=1)):
            return False
        return last >= self.close_utc - self.CLOSE_APPROACH and at - last <= self.MAX_STEP

    def drop_future_bars(self, rows, stamp):
        """
        Recent-bar rows with every bar that starts after the replay's clock
        removed. The API returns the newest bars stored, and this morning's real
        bars are newer than a replay that has reached only the open.
        """
        now = _parse_utc(stamp)
        if now is None:
            return []
        kept = []
        for row in rows or []:
            start = _parse_utc(row.get("barStartUtc"))
            if start is not None and start <= now:
                kept.append(row)
        return kept
