"""
backtest/timeutil.py

Timestamp helpers for the replay: every timestamp the runner sends to the API
is ISO-8601 UTC with a trailing "Z"; sessions, daily buckets and the EOD
square-off are reasoned about in IST (UTC+05:30).
"""

from __future__ import annotations

from datetime import date, datetime, time, timedelta, timezone
from typing import Optional, Tuple

IST = timezone(timedelta(hours=5, minutes=30), name="IST")

SESSION_OPEN_IST = time(9, 15)
SESSION_CLOSE_IST = time(15, 30)


def parse_utc(value) -> datetime:
    """
    ISO-8601 string (with "Z", an offset, or naive = UTC) or datetime -> aware UTC datetime.
    """
    if isinstance(value, datetime):
        dt = value
    else:
        text = str(value or "").strip()
        if not text:
            raise ValueError("timestamp is required")
        if text.endswith("Z") or text.endswith("z"):
            text = text[:-1] + "+00:00"
        dt = datetime.fromisoformat(text)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def iso_utc(dt: datetime) -> str:
    """Aware datetime -> "YYYY-MM-DDTHH:MM:SSZ" (whole seconds; the API stores seconds)."""
    utc = dt.astimezone(timezone.utc) if dt.tzinfo else dt.replace(tzinfo=timezone.utc)
    return utc.strftime("%Y-%m-%dT%H:%M:%SZ")


def to_ist(dt: datetime) -> datetime:
    return parse_utc(dt).astimezone(IST)


def ist_date(dt: datetime) -> date:
    return to_ist(dt).date()


def ist_time(dt: datetime) -> time:
    return to_ist(dt).time().replace(microsecond=0)


def ist_date_str(dt: datetime) -> str:
    return ist_date(dt).isoformat()


def ist_day_start_utc(day: date) -> datetime:
    """00:00 IST of `day` as UTC."""
    return datetime.combine(day, time(0, 0), tzinfo=IST).astimezone(timezone.utc)


def ist_day_end_utc(day: date) -> datetime:
    """23:59:59 IST of `day` as UTC."""
    return datetime.combine(day, time(23, 59, 59), tzinfo=IST).astimezone(timezone.utc)


def in_session(dt: datetime) -> bool:
    """True for bar starts inside the 09:15-15:30 IST session (close exclusive)."""
    t = ist_time(dt)
    return SESSION_OPEN_IST <= t < SESSION_CLOSE_IST


def parse_hhmm(value: Optional[str]) -> Optional[time]:
    """"HH:MM" -> time; None for empty/blank (meaning: no EOD square-off)."""
    text = str(value or "").strip()
    if not text:
        return None
    parts = text.split(":")
    if len(parts) < 2:
        raise ValueError(f"invalid HH:MM value: {value!r}")
    hour, minute = int(parts[0]), int(parts[1])
    if not (0 <= hour <= 23 and 0 <= minute <= 59):
        raise ValueError(f"invalid HH:MM value: {value!r}")
    return time(hour, minute)


def format_ist(dt: datetime, fmt: str = "%d %b %H:%M") -> str:
    return to_ist(dt).strftime(fmt)


def date_range_chunks(start: date, end: date, days: int = 30) -> list[Tuple[date, date]]:
    """Inclusive [start, end] split into consecutive windows of at most `days` days."""
    if end < start:
        return []
    chunks = []
    cursor = start
    while cursor <= end:
        chunk_end = min(end, cursor + timedelta(days=days - 1))
        chunks.append((cursor, chunk_end))
        cursor = chunk_end + timedelta(days=1)
    return chunks


def compact_stamp(dt: datetime) -> str:
    """"20260819T0345" style token for group ids."""
    return parse_utc(dt).strftime("%Y%m%dT%H%M")
