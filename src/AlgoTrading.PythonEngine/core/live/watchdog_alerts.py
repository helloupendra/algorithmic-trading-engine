"""
Why a live feed rebuilt its connection, told to the desk's System channel.

The feed's watchdog (core/live/feed_runner.py) restarts the connection for one
of four reasons: the vendor refused the login, it rejected the credential, the
socket stayed down, or the socket stayed up and carried nothing. Until 28 Sep
2026 each reason was a WATCHDOG line in the feed's own log and nowhere else.
On 25 Sep the desk got 572 feed alerts from the runners and not one of them
said why the feed had gone quiet; the cause was in a log file nobody was
reading.

So each cause is published on Redis ``alerts:new`` with source "process" —
the API's subscriber routes that to the System channel and keeps the row —
at most once per cause per ten minutes. A feed looping on the same cause
(24 Sep: connect, subscribe, "connection lost", every few seconds) says so
once, not every cycle; a different cause is news and goes out at once.
"""

from __future__ import annotations

import json
import time
from typing import Any, Callable, Optional

ALERT_CHANNEL = "alerts:new"

#: One message per cause in this window.
REPEAT_SECONDS = 600.0

#: The watchdog's causes.
REFUSED = "refused"
CREDENTIAL = "credential"
DISCONNECT = "disconnect"
SILENT = "silent"

_CAUSE_TITLE = {
    REFUSED: "the vendor refused the login",
    CREDENTIAL: "the credential was rejected",
    DISCONNECT: "the socket stayed down",
    SILENT: "connected but no data",
}

#: How the desk writes each vendor's name; anything else is title-cased.
_VENDOR_NAME = {"fyers": "FYERS", "dhan": "Dhan", "truedata": "TrueData", "angel": "Angel One"}


def vendor_name(key: str) -> str:
    return _VENDOR_NAME.get((key or "").lower(), (key or "feed").title())


class WatchdogAlerts:
    """Publishes a watchdog restart's cause, at most once per cause per ``repeat_seconds``."""

    def __init__(self, feed_key: str, publisher: Any = None,
                 clock: Callable[[], float] = time.monotonic,
                 repeat_seconds: float = REPEAT_SECONDS,
                 log: Callable[[str], None] = print):
        self._key = feed_key
        self._publisher = publisher
        self._clock = clock
        self._repeat = repeat_seconds
        self._log = log
        self._last: dict[str, float] = {}

    def report(self, cause: str, detail: str) -> bool:
        """
        True when a message was published. Never raises: the feed restarting
        itself matters more than the desk hearing about it.
        """
        client = getattr(self._publisher, "client", None) if self._publisher is not None else None
        if client is None:
            return False

        now = self._clock()
        last = self._last.get(cause)
        if last is not None and now - last < self._repeat:
            return False
        # Marked before the publish, so a Redis that is down costs one attempt
        # per cause per window rather than one per reconnect.
        self._last[cause] = now

        name = vendor_name(self._key)
        minutes = int(round(self._repeat / 60))
        payload = {
            # PascalCase: AlertSubscriberService binds case-sensitively.
            "Title": f"{name} feed reconnecting: {_CAUSE_TITLE.get(cause, cause)}",
            "Message": (f"{name} feed watchdog: {_trim(detail)} "
                        f"Further '{cause}' reconnects in the next {minutes} minutes are in the feed's log only."),
            "Source": "process",
            "Severity": "warning",
            "Underlying": None,
            "Symbol": None,
            "SimulationRunId": None,
        }
        try:
            client.publish(ALERT_CHANNEL, json.dumps(payload))
            return True
        except Exception as ex:  # noqa: BLE001 - never fatal to the feed
            self._log(f"[{self._key}] could not publish the watchdog alert ({cause}): {ex}")
            return False


def _trim(text: Optional[str], limit: int = 300) -> str:
    text = " ".join(str(text or "").split())
    return text if len(text) <= limit else text[: limit - 1] + "…"
