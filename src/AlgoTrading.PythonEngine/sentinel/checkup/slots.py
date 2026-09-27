"""
When each checkup runs, and what it looks at.

The times follow the desk's own day (IST): the Dhan sign-in between 08:00 and
08:40, the morning job at 08:45, forecasts at 08:50 — so the morning checkup at
08:55 sees all three. The NSE close at 15:30 and the forecast scoring at 15:50
come before the 16:00 checkup; market-close.sh at 23:58 before the 00:15 one.

A checkup that could not run at its time (Sentinel was restarting, the box was
down) still runs later, up to ``until``: a morning report at 10:30 is late, but
it still says whether the plan started.
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, time
from typing import Optional

from sentinel.clock import to_ist


@dataclass(frozen=True)
class Slot:
    name: str
    #: How the headline says when: "2 things to do before the open".
    when: str
    #: What the console and the message call it.
    label: str
    at: time = time(0, 0)
    until: time = time(0, 0)
    #: "trading" (an NSE trading day, from the calendar), "daily" or "sunday".
    days: str = "trading"
    checks: tuple[str, ...] = ()
    #: Sent to Telegram only when something needs a person.
    quiet_when_ok: bool = False
    #: Sent to Telegram at all.
    telegram: bool = True


MORNING = Slot(
    "morning", "before the open", "Before the open", time(8, 55), time(12, 0), "trading",
    ("dhan-token", "fyers-backup", "feeds", "plan", "forecasts-issued", "manual-books", "stale-runs",
     "orphaned-legs", "incidents", "deploy", "disk", "archive", "calendar-today"))

CLOSE = Slot(
    "close", "after the close", "After the close", time(16, 0), time(20, 0), "trading",
    ("runs-after-close", "legs-after-close", "manual-books", "carried-today", "forecasts-scored", "day-result",
     "dhan-token", "failover", "incidents", "deploy", "desk-code"))

# After market-close.sh (23:58): everything should be off. At a quarter past
# midnight nobody wants a message that says so — only one that says it is not.
NIGHT = Slot(
    "night", "at the end of the day", "End of day", time(0, 15), time(3, 0), "daily",
    ("runs-overnight", "feeds", "incidents"), quiet_when_ok=True)

WEEKLY = Slot(
    "weekly", "for the week ahead", "Weekly review", time(18, 0), time(23, 0), "sunday",
    ("dhan-token", "calendar-week", "stale-runs", "orphaned-legs", "manual-books", "incidents", "incident-notes",
     "failover", "deploy", "desk-code", "disk", "archive"))

# Asked for from the console: everything that means something at any hour;
# the time-bound checks (the plan, the forecasts) decide for themselves.
ON_REQUEST = Slot(
    "on-request", "right now", "On request", checks=(
        "dhan-token", "fyers-backup", "feeds", "plan", "manual-books", "stale-runs", "orphaned-legs",
        "incidents", "failover", "deploy", "desk-code", "disk", "archive"),
    telegram=False)

SCHEDULED = (MORNING, CLOSE, NIGHT, WEEKLY)
BY_NAME = {s.name: s for s in (*SCHEDULED, ON_REQUEST)}


def due(now_utc: datetime, trading_day: bool, done: dict[str, str]) -> Optional[Slot]:
    """
    The scheduled checkup to run now, if any: inside its window, on its kind
    of day, and not yet run today (``done`` maps a slot to the IST date it last
    ran).
    """
    ist = to_ist(now_utc)
    today = ist.date().isoformat()
    for slot in SCHEDULED:
        if not slot.at <= ist.time() < slot.until:
            continue
        if slot.days == "trading" and not trading_day:
            continue
        if slot.days == "sunday" and ist.weekday() != 6:
            continue
        if done.get(slot.name) == today:
            continue
        return slot
    return None
