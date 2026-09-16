"""
core/live/reconnect_policy.py

How long to wait before rebuilding a vendor socket that keeps dropping.

The runner's watchdog rebuilds a connection that has been down for 20 seconds,
which is right when the drop was a blip. It is wrong when the vendor is
dropping the socket on purpose — an expired session, a blocked account, a
maintenance window — because the rebuild then repeats every 20 seconds
forever.

That is what happened on 2026-09-16: yesterday's Dhan feed kept its process
alive into a session whose token had died, connected, subscribed 267 symbols,
was dropped immediately, and tried again. After an hour of that, Dhan answered
the handshake with 429 "Too many requests from this IP hence client id is
blocked" — the account itself was locked out, so even a fresh token could not
have fed the desk.

A cycle that carried no ticks is treated as a failure and the wait grows;
anything the vendor says about rate limits jumps straight to the long wait.
One healthy cycle clears the count.
"""

from __future__ import annotations

#: First wait after a tickless cycle, in seconds.
BASE_DELAY_SECONDS = 5.0

#: Never wait longer than this between attempts: a session that recovers at
#: 11:00 should be back within minutes, not after lunch.
MAX_DELAY_SECONDS = 300.0

#: A vendor that is rate-limiting needs a real pause, not a doubling from 5s.
RATE_LIMIT_DELAY_SECONDS = 60.0

#: Phrases vendors use when the account, not the socket, is the problem.
_RATE_LIMITED = ("too many requests", "rate limit", "client id is blocked", "429", "blocked")


def is_rate_limited(detail: str | None) -> bool:
    """True when the vendor's own words say we are knocking too often."""
    text = (detail or "").lower()
    return any(phrase in text for phrase in _RATE_LIMITED)


def reconnect_delay(
    consecutive_tickless: int,
    detail: str | None = None,
    base: float = BASE_DELAY_SECONDS,
    cap: float = MAX_DELAY_SECONDS,
    rate_limit_delay: float = RATE_LIMIT_DELAY_SECONDS,
) -> float:
    """
    Seconds to wait before the next connection attempt.

    `consecutive_tickless` counts cycles that delivered nothing, so 0 (the
    connection was working) means reconnect at once.
    """
    if consecutive_tickless <= 0:
        return 0.0
    delay = min(base * (2 ** (consecutive_tickless - 1)), cap)
    if is_rate_limited(detail):
        delay = min(max(delay, rate_limit_delay), cap)
    return float(delay)


def describe(consecutive_tickless: int, delay: float, detail: str | None = None) -> str:
    """The log line explaining the wait, in words the owner can act on."""
    if is_rate_limited(detail):
        return (f"the vendor is rate-limiting us ({(detail or '').strip()[:80]}) — waiting {int(delay)}s "
                f"before reconnecting. Check the token: a dead session is the usual reason a feed "
                f"reconnects in a loop.")
    return (f"{consecutive_tickless} reconnect(s) carried no ticks — waiting {int(delay)}s before the next "
            f"attempt so the vendor does not block the account.")
