"""
This platform's canonical symbol, in TrueData's grammar.

The twin of TrueDataSymbols.cs, and deliberately only half of it: the streamer
subscribes with names it derived from the platform's own watchlist, and TrueData
answers with the symbol id it assigned. So the id is mapped back to the
canonical symbol we asked for, and vendor-to-canonical never has to be guessed
at runtime. Only this direction is needed here.
"""

import re

#: Canonical spot symbol -> TrueData's index name.
INDEX_BY_CANONICAL = {
    "NSE:NIFTY50-INDEX": "NIFTY 50",
    "NSE:NIFTYBANK-INDEX": "NIFTY BANK",
    "NSE:FINNIFTY-INDEX": "NIFTY FIN SERVICE",
    "NSE:MIDCPNIFTY-INDEX": "NIFTY MID SELECT",
    "NSE:NIFTYNXT50-INDEX": "NIFTY NEXT 50",
    "BSE:SENSEX-INDEX": "SENSEX",
    "BSE:BANKEX-INDEX": "BANKEX",
}

_EQUITY = re.compile(r"^(?:NSE|BSE):(?P<name>[A-Z0-9&\-]+?)-EQ$", re.IGNORECASE)

#: The canonical weekly grammar: underlying, yy, one character of month, dd,
#: strike, CE/PE. October to December are O, N and D.
_WEEKLY_OPTION = re.compile(
    r"^(?:NSE|BSE):(?P<u>[A-Z&]+?)(?P<yy>\d{2})(?P<m>[1-9OND])(?P<dd>\d{2})(?P<strike>\d+)(?P<type>CE|PE)$",
    re.IGNORECASE,
)

_MONTH_FROM_CHAR = {"O": 10, "N": 11, "D": 12}


def to_vendor(canonical_symbol: str | None) -> str | None:
    """
    TrueData's name for a canonical symbol, or None when it cannot be derived.

    None for monthly options and futures, and that is the correct answer rather
    than a gap. TrueData spells every option by its exact expiry day and every
    future by how far out it is ("CRUDEOIL-I" is whichever contract is nearest
    today), while the canonical monthly form carries a month and no day. A guess
    here would be wrong on exactly the days it matters most, so those names come
    from the mapping rows an import wrote from TrueData's own master.
    """
    if not canonical_symbol:
        return None
    s = canonical_symbol.strip()

    index = INDEX_BY_CANONICAL.get(s.upper())
    if index:
        return index

    equity = _EQUITY.match(s)
    if equity:
        return equity.group("name").upper()

    option = _WEEKLY_OPTION.match(s)
    if option:
        month_char = option.group("m").upper()
        month = _MONTH_FROM_CHAR.get(month_char)
        if month is None:
            month = int(month_char)
        day = int(option.group("dd"))
        if not 1 <= day <= 31:
            return None
        return (f"{option.group('u').upper()}{option.group('yy')}"
                f"{month:02d}{day:02d}{option.group('strike')}{option.group('type').upper()}")

    return None
