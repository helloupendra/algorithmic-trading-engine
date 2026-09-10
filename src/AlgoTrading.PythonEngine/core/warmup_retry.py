"""
core/warmup_retry.py

Warmup history is fetched with the broker token the API holds. A token that
has expired (FYERS: at 06:00 IST) answers "Could not authenticate the user";
on 2026-09-10 the runner logged that as a WARN and went on without warmup,
which for Ghost means no evaluation for 51 live bars — the whole day, silent.

Retrying while a person signs in is what keeps a late sign-in a delay rather
than a lost session. Each attempt builds a fresh DataEngine so the token is
re-read from the API, not served from the old client's cache.

No engine imports here on purpose: the runner module pulls in the broker SDK,
and this decision has to stay testable without it.
"""

import time
from typing import Callable, Optional

WARMUP_AUTH_RETRY_SECONDS = 30
WARMUP_AUTH_RETRY_ATTEMPTS = 40   # 20 minutes


def is_auth_failure(exc: BaseException) -> bool:
    text = str(exc).lower()
    return ("could not authenticate" in text
            or "not authenticated" in text
            or ("token" in text and ("expired" in text or "invalid" in text)))


def fetch_warmup_bars_with_retry(make_engine: Callable, symbol: str, resolution: str, start_date: str,
                                 end_date: str, label: str, attempts: int = WARMUP_AUTH_RETRY_ATTEMPTS,
                                 delay: float = WARMUP_AUTH_RETRY_SECONDS, sleep=time.sleep):
    """
    get_historical_bars, retried on authentication failures only. Any other
    error is the caller's to report. Returns the bars, or raises the last
    authentication error once the attempts are spent.
    """
    last: Optional[BaseException] = None
    for attempt in range(1, attempts + 1):
        try:
            return make_engine().get_historical_bars(
                symbol=symbol, resolution=resolution, start_date=start_date, end_date=end_date)
        except Exception as ex:  # noqa: BLE001 — classified below
            if not is_auth_failure(ex):
                raise
            last = ex
            if attempt < attempts:
                print(f"[{label}] Warmup: broker rejected the token ({ex}) — "
                      f"retry {attempt}/{attempts - 1} in {delay}s (waiting for a fresh sign-in).", flush=True)
                sleep(delay)
    assert last is not None
    raise last
