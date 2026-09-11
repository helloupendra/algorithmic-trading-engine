"""
A named symbol list for a feed, from the environment.

<KEY>_SYMBOLS (TRUEDATA_SYMBOLS, FYERS_SYMBOLS, ...): comma-separated entries,
each CANONICAL or CANONICAL=VENDOR_NAME. When set it replaces the recording
list — for a vendor whose symbol limit is below what the list carries, or for
the evening recap, where the contracts a strategy trades must be the ones
subscribed. The =VENDOR_NAME half is for contracts a vendor's grammar cannot
build (a monthly option carries a month and no expiry day), read from the
instrument master rather than guessed.
"""

import os


def parse_symbol_list(value: str | None) -> tuple[list[str], dict[str, str]]:
    symbols: list[str] = []
    names: dict[str, str] = {}
    for entry in (e.strip() for e in (value or "").split(",")):
        if not entry:
            continue
        canonical, _, vendor = entry.partition("=")
        canonical = canonical.strip()
        if not canonical:
            continue
        symbols.append(canonical)
        if vendor.strip():
            names[canonical] = vendor.strip()
    return symbols, names


def symbols_for(key: str) -> tuple[list[str], dict[str, str]]:
    return parse_symbol_list(os.getenv(f"{key.upper()}_SYMBOLS"))
