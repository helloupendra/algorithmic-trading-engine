"""
How old a tick is, from whatever timestamp it happens to carry.

The runner used to do this inline with strptime and a format string ending in a
literal "Z". Real ticks carry "2026-09-04T10:00:44.629503+00:00" — the offset
spelled out, not "Z" — so the parse raised on every single tick and an
`except Exception: pass` swallowed it. The REDIS_LAG gauge was never once set,
which is exactly the number you would go looking for to find out whether the
feed is running behind.

Both spellings are the same instant. Parsing them is not the interesting part;
not silently losing the answer is.
"""

from __future__ import annotations

from datetime import datetime, timezone
from typing import Optional


def parse_tick_timestamp(value: Optional[str]) -> Optional[datetime]:
    """
    A tick's timestamp as an aware UTC datetime, or None if it cannot be read.

    Accepts both "…+00:00" and "…Z", with or without fractional seconds, and
    treats a naive timestamp as UTC — everything on this feed is UTC, and
    guessing local time would silently shift the age by hours.
    """
    if not value:
        return None

    text = value.strip()
    if not text:
        return None

    # fromisoformat handles "Z" only from Python 3.11; normalising first keeps
    # this working the same way on older runtimes.
    if text.endswith(("Z", "z")):
        text = text[:-1] + "+00:00"

    try:
        parsed = datetime.fromisoformat(text)
    except ValueError:
        return None

    return parsed.replace(tzinfo=timezone.utc) if parsed.tzinfo is None else parsed.astimezone(timezone.utc)


def tick_age_seconds(value: Optional[str], now_epoch: float) -> Optional[float]:
    """
    Seconds between the tick's timestamp and now, or None if it cannot be read.

    A negative age is returned as-is rather than clamped: it means the producer's
    clock is ahead of ours, and hiding that would hide a real problem.
    """
    parsed = parse_tick_timestamp(value)
    if parsed is None:
        return None
    return now_epoch - parsed.timestamp()
