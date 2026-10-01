"""
When a silent tick feed is worth waking someone for.

A stalled feed is the failure a live runner cannot survive quietly. The process
stays up, the loop keeps spinning, every heartbeat and dashboard says healthy —
and the strategy is blind to a market that is still moving. Only the runner can
see it, because a Redis stream that has stopped carrying ticks looks exactly
like a market with nothing to say.

The decision is kept here, apart from the runner's loop, because it is the part
that has to be right and the part that is otherwise impossible to test: the
runner's copy lived in a closure over a live socket, a live clock and market
hours.
"""

from __future__ import annotations

from typing import NamedTuple, Optional

#: No ticks for this long, with the market open, is a stall.
DEFAULT_STALL_AFTER_SECONDS = 90

#: While it stays stalled, say so again only this often.
DEFAULT_REPEAT_AFTER_SECONDS = 600

#: A stall first reported before this age is reported once more when it gets
#: here. The API sends a runner's stall to Telegram only at this age
#: (FeedStallAlertGate, 180 s): the feed reconnects itself after 120 s of
#: silence (core/live/feed_runner.py), and the 90 s report alone described
#: blips that were already healing — 572 messages on 25 Sep 2026. Without
#: this second report the API would hear nothing between 90 s and the
#: ten-minute repeat, and a real outage would reach the desk ten minutes late.
DEFAULT_CONFIRM_AFTER_SECONDS = 180


class FeedVerdict(NamedTuple):
    """What to do about the feed, and how long it has been silent."""

    #: "quiet" (nothing to say), "stalled", "still-stalled" or "recovered".
    action: str
    silent_seconds: int

    @property
    def should_report(self) -> bool:
        return self.action != "quiet"

    @property
    def is_stalled(self) -> bool:
        return self.action in ("stalled", "still-stalled")


def assess_feed(
    *,
    now: float,
    last_tick_at: Optional[float],
    listening_since: float,
    currently_stalled: bool,
    last_report_at: float,
    market_open: Optional[bool],
    stall_after: float = DEFAULT_STALL_AFTER_SECONDS,
    repeat_after: float = DEFAULT_REPEAT_AFTER_SECONDS,
    confirm_after: float = DEFAULT_CONFIRM_AFTER_SECONDS,
) -> FeedVerdict:
    """
    Decide whether a feed has gone dry, stayed dry, or come back.

    ``last_tick_at`` is None until the first tick arrives; silence is then
    measured from ``listening_since``, because starting into a feed that was
    never alive is the more common way this goes wrong.

    ``market_open`` may be None when the question could not be asked. Silence
    only means something while the market is open, and an unanswerable question
    is not grounds to cry wolf — but a *recovery* is always worth reporting,
    since it closes a warning that has already gone out.

    A stall reported before ``confirm_after`` seconds of silence is reported
    once more ("still-stalled") when the silence reaches it, then every
    ``repeat_after``.
    """
    silent_since = last_tick_at if last_tick_at is not None else listening_since
    silent_seconds = int(max(0.0, now - silent_since))

    if silent_seconds < stall_after:
        if currently_stalled:
            return FeedVerdict("recovered", silent_seconds)
        return FeedVerdict("quiet", silent_seconds)

    if market_open is not True:
        return FeedVerdict("quiet", silent_seconds)

    if not currently_stalled:
        return FeedVerdict("stalled", silent_seconds)

    if now - last_report_at >= repeat_after:
        return FeedVerdict("still-stalled", silent_seconds)

    # Stateless: the last report was made before the silence was confirm_after
    # old, and now it is.
    if silent_seconds >= confirm_after and last_report_at - silent_since < confirm_after:
        return FeedVerdict("still-stalled", silent_seconds)

    return FeedVerdict("quiet", silent_seconds)


def session_market(spot_symbol: Optional[str]) -> tuple[str, str]:
    """
    The (exchange, segment) whose hours decide whether a run's silence is a stall. A crude run is on MCX
    until 23:30 or 23:55, so asking about NSE (closed from 15:30) hid every evening stall of its feed.
    """
    exchange = (spot_symbol or "").split(":", 1)[0].strip().upper()
    if exchange == "MCX":
        return "MCX", "COM"
    if exchange == "BSE":
        return "BSE", "FO"
    return "NSE", "FO"
