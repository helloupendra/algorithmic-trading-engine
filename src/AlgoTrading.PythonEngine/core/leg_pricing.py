"""
Deciding which signal legs still need a price, and how long to keep asking.

The runner used to poll one symbol at a time, up to 35 seconds EACH, in the
middle of its tick loop. A four-leg roll could stop the strategy from seeing the
market for over two minutes — and a sixteen-leg one for nine — while the loop sat
in `time.sleep(1)`. Every leg's clock also started only after the previous leg
gave up, so the last leg was subscribed minutes after the first.

The shape that replaces it: subscribe every leg first, then wait ONCE for all of
them together against a single budget. The waiting itself needs a socket and a
clock; the decisions do not, so they live here where they can be tested.

Nothing here invents a price. A leg that stays unpriced is reported as unpriced
and the API decides what to do — it refuses an opening group outright rather
than filling it at zero, which is the behaviour this module must not undermine.
"""

from __future__ import annotations

from typing import Any, Callable, Dict, Iterable, List, Optional

from core.tick_age import tick_age_seconds

#: A quote older than this is not evidence of a current price. LiveQuotesLatest
#: is never purged, so a strike re-added on Monday still carries Friday's close —
#: which would look perfectly valid and price the entry hundreds of points wrong.
MAX_QUOTE_AGE_SECONDS = 900.0

#: Total wait for ALL legs of one signal, not per leg.
DEFAULT_WAIT_SECONDS = 10.0

#: How often to re-ask while waiting.
DEFAULT_POLL_INTERVAL = 0.25


def leg_price(leg: Dict[str, Any]) -> Optional[float]:
    """The leg's price if it has a usable one. Zero is not a price."""
    value = leg.get("price")
    if value is None:
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    return number if number > 0 else None


def symbols_needing_price(legs: Iterable[Dict[str, Any]]) -> List[str]:
    """Distinct symbols of the legs that still have no usable price, in order."""
    seen = set()
    missing = []
    for leg in legs:
        symbol = (leg.get("symbol") or "").strip()
        if not symbol or leg_price(leg) is not None or symbol in seen:
            continue
        seen.add(symbol)
        missing.append(symbol)
    return missing


def usable_quote(
    quote: Optional[Dict[str, Any]],
    now_epoch: float,
    max_age_seconds: float = MAX_QUOTE_AGE_SECONDS,
) -> Optional[float]:
    """
    A quote's price, if it is both positive and recent enough to believe.

    A quote with no timestamp is accepted: some sources do not stamp one, and
    refusing every unstamped quote would reject far more good prices than stale
    ones. A quote whose timestamp is unreadable is treated the same way — the
    price is the evidence, the timestamp is the caveat.
    """
    if not quote:
        return None

    try:
        price = float(quote.get("lastTradedPrice"))
    except (TypeError, ValueError):
        return None
    if price <= 0:
        return None

    age = tick_age_seconds(quote.get("updatedUtc"), now_epoch)
    if age is not None and age > max_age_seconds:
        return None

    return price


def apply_quotes(
    legs: List[Dict[str, Any]],
    quotes: Dict[str, Dict[str, Any]],
    now_epoch: float,
    max_age_seconds: float = MAX_QUOTE_AGE_SECONDS,
) -> List[str]:
    """
    Fill in every leg a fresh quote covers. Returns the symbols still unpriced.

    Mutates the legs in place, which is what the caller wants: the same list is
    posted to the API immediately afterwards.
    """
    for leg in legs:
        if leg_price(leg) is not None:
            continue
        symbol = (leg.get("symbol") or "").strip()
        if not symbol:
            continue
        price = usable_quote(quotes.get(symbol), now_epoch, max_age_seconds)
        if price is not None:
            leg["price"] = price

    return symbols_needing_price(legs)


def wait_plan(
    elapsed: float,
    budget_seconds: float,
    poll_interval: float = DEFAULT_POLL_INTERVAL,
) -> Optional[float]:
    """
    How long to sleep before asking again, or None when the budget is spent.

    Never sleeps past the deadline, and never sleeps *after* the last attempt —
    the old code's final `time.sleep(1)` bought nothing but a second of blindness.
    A budget of zero means one attempt with no sleeping at all, which is what a
    closing signal gets: getting flat must not queue behind an entry.
    """
    remaining = budget_seconds - elapsed
    if remaining <= 0:
        return None
    return min(poll_interval, remaining)


def resolve_leg_prices(
    legs: List[Dict[str, Any]],
    fetch_quotes: Callable[[], Dict[str, Dict[str, Any]]],
    now: Callable[[], float],
    sleep: Callable[[float], None],
    budget_seconds: float = DEFAULT_WAIT_SECONDS,
    poll_interval: float = DEFAULT_POLL_INTERVAL,
    max_age_seconds: float = MAX_QUOTE_AGE_SECONDS,
    on_poll: Optional[Callable[[], None]] = None,
) -> List[str]:
    """
    Price every leg it can within one shared budget. Returns what is still missing.

    ``fetch_quotes`` returns the whole quote table in one call, so the cost of a
    poll does not grow with the number of legs. ``on_poll`` runs once per round:
    the runner passes its housekeeping there so the feed watchdog and the status
    line keep ticking instead of going quiet for the length of the wait.

    A failing fetch is not fatal — it is one lost round out of many, and the
    budget still bounds the whole thing.
    """
    missing = symbols_needing_price(legs)
    if not missing:
        return []

    started = now()

    while True:
        try:
            quotes = fetch_quotes() or {}
        except Exception:
            quotes = {}

        missing = apply_quotes(legs, quotes, now(), max_age_seconds)
        if not missing:
            return []

        if on_poll is not None:
            try:
                on_poll()
            except Exception:
                pass

        delay = wait_plan(now() - started, budget_seconds, poll_interval)
        if delay is None:
            return missing
        sleep(delay)
