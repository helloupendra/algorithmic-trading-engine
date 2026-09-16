"""
Angel One SmartAPI, kept to itself.

A third data vendor, added on 2026-09-16 while FYERS and Dhan were feeding the
live desk. Nothing here is registered as a feed, a provider or a startup
service: it is a library plus `tools/angel_probe.py`, so it can be tried
locally without touching a running session. Wiring it into the live feed
registry is a separate, deliberate step.

  totp.py         the 6-digit code SmartAPI's login needs (no new dependency)
  instruments.py  the public scrip master: token lookup, symbol mapping
  session.py      credentials, login, the headers every call carries
  client.py       quotes, candles, historical OI, option greeks, scanners
"""

from market_data.angel.client import AngelClient, AngelError, AngelRateLimited
from market_data.angel.instruments import AngelInstruments, ScripRow
from market_data.angel.session import AngelCredentials, AngelSession

__all__ = ["AngelClient", "AngelError", "AngelRateLimited", "AngelInstruments", "ScripRow", "AngelCredentials", "AngelSession"]
