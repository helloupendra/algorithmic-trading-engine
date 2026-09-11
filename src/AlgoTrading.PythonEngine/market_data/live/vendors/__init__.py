"""
The live feed adapters, by connector key.

Adding a vendor is one module in this package and one line in ADAPTERS. The
runner, the Redis stream, the API, the heartbeat, the watchdogs and the C#
supervisor that starts it are all shared — see core.live.feed_runner.
"""

import importlib

#: connector key -> "module:class". Imported only when asked for, so a TrueData
#: process never loads the FYERS SDK and the reverse.
ADAPTERS = {
    "fyers": "market_data.live.vendors.fyers:FyersFeed",
    "truedata": "market_data.live.vendors.truedata:TrueDataFeed",
}


def build_feed(key: str):
    """An adapter configured from the environment, for one connector key."""
    target = ADAPTERS.get((key or "").strip().lower())
    if target is None:
        raise SystemExit(f"No live feed adapter is registered for '{key}'. Known: {', '.join(sorted(ADAPTERS))}.")
    module_name, class_name = target.split(":")
    cls = getattr(importlib.import_module(module_name), class_name)
    return cls.from_env()
