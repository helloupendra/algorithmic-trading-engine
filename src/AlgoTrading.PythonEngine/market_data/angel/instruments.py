"""
market_data/angel/instruments.py

Angel One's scrip master: the token every other call needs.

SmartAPI addresses an instrument by `exchange` + `symboltoken`, not by a
symbol, and publishes the whole list as one public JSON file (about 22 MB, no
authentication). It is downloaded once a day into the data directory and read
from there.

Mapping to the platform's own symbols ("NSE:RELIANCE-EQ",
"NSE:NIFTY50-INDEX", "MCX:CRUDEOIL26SEPFUT") is deliberately explicit:
equities and indices are matched by name, derivatives by their parts (name,
expiry, strike, option type). A wrong guess here prices the wrong contract,
so anything uncertain returns None instead.
"""

from __future__ import annotations

import json
import os
import time
from dataclasses import dataclass
from datetime import date, datetime
from typing import Dict, Iterable, List, Optional, Sequence

MASTER_URL = "https://margincalculator.angelbroking.com/OpenAPI_File/files/OpenAPIScripMaster.json"

#: Where the daily copy lives. Outside the repo, beside the other market data.
DEFAULT_CACHE_DIR = os.path.join(os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data")), "angel")

#: Platform exchange prefix -> Angel's exchange segment.
EXCHANGE_SEGMENTS = {"NSE": ("NSE", "NFO"), "BSE": ("BSE", "BFO"), "MCX": ("MCX",), "CDS": ("CDS",)}

#: Angel marks indices with these instrument types.
INDEX_TYPES = ("AMXIDX", "INDEX")


@dataclass(frozen=True)
class ScripRow:
    """One instrument as Angel publishes it."""
    token: str
    symbol: str            # "RELIANCE-EQ", "NIFTY25SEP24000CE", "Nifty 50"
    name: str              # "RELIANCE", "NIFTY"
    expiry: str            # "25SEP2026" or ""
    strike: float          # in paise for options (Angel multiplies by 100)
    lot_size: int
    instrument_type: str   # "", "OPTIDX", "FUTSTK", "AMXIDX", ...
    exchange: str          # "NSE", "NFO", "MCX", ...
    tick_size: float

    @property
    def is_index(self) -> bool:
        return self.instrument_type in INDEX_TYPES

    @property
    def expiry_date(self) -> Optional[date]:
        return parse_expiry(self.expiry)


def parse_expiry(text: str) -> Optional[date]:
    """"25SEP2026" -> date(2026, 9, 25). Empty or unparseable -> None."""
    raw = (text or "").strip()
    if not raw:
        return None
    # The value is upper-cased ("22SEP2026"), the FORMAT is not: upper-casing
    # "%d%b%Y" turns it into "%D%B%Y", which parses nothing.
    for fmt in ("%d%b%Y", "%d-%b-%Y", "%Y-%m-%d"):
        try:
            return datetime.strptime(raw.upper(), fmt).date()
        except ValueError:
            continue
    return None


def parse_master(rows: Iterable[dict]) -> List[ScripRow]:
    """The published JSON as typed rows; anything without a token is skipped."""
    out: List[ScripRow] = []
    for row in rows or []:
        token = str(row.get("token") or "").strip()
        if not token:
            continue
        try:
            strike = float(row.get("strike") or 0.0)
        except (TypeError, ValueError):
            strike = 0.0
        try:
            lot = int(float(row.get("lotsize") or 0))
        except (TypeError, ValueError):
            lot = 0
        try:
            tick = float(row.get("tick_size") or 0.0)
        except (TypeError, ValueError):
            tick = 0.0
        out.append(ScripRow(
            token=token, symbol=str(row.get("symbol") or "").strip(), name=str(row.get("name") or "").strip(),
            expiry=str(row.get("expiry") or "").strip(), strike=strike, lot_size=lot,
            instrument_type=str(row.get("instrumenttype") or "").strip(), exchange=str(row.get("exch_seg") or "").strip(),
            tick_size=tick,
        ))
    return out


def download_master(cache_dir: str = DEFAULT_CACHE_DIR, max_age_hours: float = 20.0,
                    fetch=None, log=print) -> str:
    """
    Path to today's scrip master, downloading it only when the copy on disk is
    older than `max_age_hours` (Angel republishes it every morning).
    """
    os.makedirs(cache_dir, exist_ok=True)
    path = os.path.join(cache_dir, "scrip-master.json")
    fresh = os.path.exists(path) and (time.time() - os.path.getmtime(path)) < max_age_hours * 3600
    if fresh:
        return path
    if fetch is None:
        import requests

        def fetch(url):  # noqa: E306 - tiny local default
            response = requests.get(url, timeout=120)
            response.raise_for_status()
            return response.content

    log(f"[angel] downloading the scrip master ({MASTER_URL}) ...")
    content = fetch(MASTER_URL)
    tmp = path + ".part"
    with open(tmp, "wb") as fh:
        fh.write(content)
    os.replace(tmp, path)
    log(f"[angel] scrip master saved: {path} ({len(content) / 1e6:.1f} MB)")
    return path


class AngelInstruments:
    """The scrip master in memory, with the lookups the rest of the module needs."""

    def __init__(self, rows: Sequence[ScripRow]) -> None:
        self.rows = list(rows)
        self._by_token: Dict[str, ScripRow] = {r.token: r for r in self.rows}
        self._by_symbol: Dict[tuple, ScripRow] = {}
        for r in self.rows:
            self._by_symbol.setdefault((r.exchange, r.symbol.upper()), r)

    @classmethod
    def load(cls, path: Optional[str] = None, cache_dir: str = DEFAULT_CACHE_DIR, **kw) -> "AngelInstruments":
        path = path or download_master(cache_dir=cache_dir, **kw)
        with open(path, "rb") as fh:
            return cls(parse_master(json.load(fh)))

    def __len__(self) -> int:
        return len(self.rows)

    def by_token(self, token: str) -> Optional[ScripRow]:
        return self._by_token.get(str(token))

    def by_symbol(self, exchange: str, symbol: str) -> Optional[ScripRow]:
        return self._by_symbol.get((exchange.upper(), symbol.upper()))

    def search(self, text: str, exchange: Optional[str] = None, limit: int = 20) -> List[ScripRow]:
        """Rows whose symbol or name contains `text`, for finding what Angel calls something."""
        needle = (text or "").strip().upper()
        if not needle:
            return []
        hits = [r for r in self.rows
                if (exchange is None or r.exchange == exchange.upper())
                and (needle in r.symbol.upper() or needle in r.name.upper())]
        hits.sort(key=lambda r: (len(r.symbol), r.symbol))
        return hits[:limit]

    # ------------------------------------------------------- platform symbols

    def equity(self, platform_symbol: str) -> Optional[ScripRow]:
        """"NSE:RELIANCE-EQ" -> the NSE cash row."""
        exchange, _, rest = platform_symbol.partition(":")
        if not rest or exchange.upper() not in ("NSE", "BSE"):
            return None
        return self.by_symbol(exchange, rest)

    def index(self, platform_symbol: str, aliases: Optional[Dict[str, str]] = None) -> Optional[ScripRow]:
        """
        "NSE:NIFTY50-INDEX" -> Angel's index row.

        Angel names indices in prose ("Nifty 50", "Nifty Bank"), so the few we
        trade are mapped by hand; everything else falls back to matching the
        row's `name` against the platform symbol with the punctuation removed.
        """
        exchange, _, rest = platform_symbol.partition(":")
        key = rest.upper().replace("-INDEX", "")
        mapping = {**DEFAULT_INDEX_ALIASES, **(aliases or {})}
        wanted = mapping.get(f"{exchange.upper()}:{key}") or mapping.get(key)
        for row in self.rows:
            if not row.is_index or row.exchange != exchange.upper():
                continue
            if wanted and row.symbol.upper() == wanted.upper():
                return row
            if not wanted and row.name.upper().replace(" ", "") == key:
                return row
        return None

    def derivative(self, *, exchange: str, name: str, expiry: date, strike: Optional[float] = None,
                   option_type: Optional[str] = None) -> Optional[ScripRow]:
        """
        One futures or options contract, matched on every part rather than on a
        spelled-out symbol: Angel's option symbols differ from FYERS's and a
        near miss would price the wrong strike.
        """
        segments = EXCHANGE_SEGMENTS.get(exchange.upper(), (exchange.upper(),))
        wanted_type = (option_type or "").upper()
        for row in self.rows:
            if row.exchange not in segments or row.name.upper() != name.upper():
                continue
            if row.expiry_date != expiry:
                continue
            if wanted_type:
                if not row.symbol.upper().endswith(wanted_type):
                    continue
                # Angel publishes option strikes multiplied by 100.
                if strike is not None and abs(row.strike / 100.0 - float(strike)) > 0.01:
                    continue
            elif row.instrument_type.startswith("OPT"):
                continue    # asked for a future, this is an option
            return row
        return None


#: Angel's own names for the indices this desk trades.
DEFAULT_INDEX_ALIASES = {
    "NSE:NIFTY50": "Nifty 50",
    "NSE:NIFTYBANK": "Nifty Bank",
    "NSE:FINNIFTY": "Nifty Fin Service",
    "NSE:MIDCPNIFTY": "NIFTY MID SELECT",
    "NSE:INDIAVIX": "India VIX",
    "BSE:SENSEX": "SENSEX",
    "BSE:BANKEX": "BANKEX",
}
