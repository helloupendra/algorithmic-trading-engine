"""
analysis/symbols.py

Which NSE companies a headline names: rules, not a model, so every tag can be
explained by the phrase that produced it.

The dictionary is the instrument master's NSE cash equities (`NSE:<TICKER>-EQ`,
segment CM) and three ways of naming one:

  1. a short curated alias list for the NIFTY-50 names headlines actually use
     ("L&T", "Airtel", "HUL", "SBI"), which the master's legal names miss;
  2. the master's company names with "Ltd"/"Limited" dropped, when at least
     two words are left ("HDFC BANK", "RELIANCE POWER"): a one-word name is too
     often an English word ("Premier", "Eternal") to trust;
  3. the ticker itself, only as an upper-case token of three or more
     characters that is not a common abbreviation ("BSE", "IT", "GST") — and
     not at all in a headline written in capitals, where every word looks
     like a ticker.

Longer phrases win and consume their text first, so "Reliance Power" is
RPOWER and not RELIANCE, "SBI Life" is SBILIFE, and "Reserve Bank of India" —
blanked out before anything else — is never Bank of India.
"""

from __future__ import annotations

import re
from typing import Dict, Iterable, List, Sequence, Tuple

#: Institutions whose names contain a listed company's ("Bank of India"). Blanked first.
BLOCKED_PHRASES: Tuple[str, ...] = (
    "reserve bank of india", "securities and exchange board of india", "government of india",
    "national stock exchange of india", "insurance regulatory and development authority of india",
    "competition commission of india", "election commission of india",
)

#: How headlines name NIFTY-50 companies when not by the master's legal name. Lower case; matched as whole phrases.
ALIASES: Dict[str, str] = {
    "reliance industries": "RELIANCE", "ril": "RELIANCE",
    "hdfc bank": "HDFCBANK", "icici bank": "ICICIBANK", "axis bank": "AXISBANK",
    "kotak mahindra bank": "KOTAKBANK", "kotak bank": "KOTAKBANK",
    "state bank of india": "SBIN", "sbi": "SBIN", "sbi life": "SBILIFE", "sbi card": "SBICARD",
    "sbi cards": "SBICARD", "hdfc life": "HDFCLIFE",
    "infosys": "INFY", "tata consultancy services": "TCS", "wipro": "WIPRO", "hcltech": "HCLTECH",
    "hcl tech": "HCLTECH", "hcl technologies": "HCLTECH", "tech mahindra": "TECHM",
    "bharti airtel": "BHARTIARTL", "airtel": "BHARTIARTL",
    "larsen & toubro": "LT", "larsen and toubro": "LT", "l&t": "LT",
    "hindustan unilever": "HINDUNILVR", "hul": "HINDUNILVR", "nestle india": "NESTLEIND",
    "tata consumer": "TATACONSUM", "asian paints": "ASIANPAINT", "titan": "TITAN", "trent": "TRENT",
    "bajaj finance": "BAJFINANCE", "bajaj finserv": "BAJAJFINSV", "bajaj auto": "BAJAJ-AUTO",
    "shriram finance": "SHRIRAMFIN", "jio financial": "JIOFIN",
    "maruti suzuki": "MARUTI", "maruti": "MARUTI", "mahindra & mahindra": "M&M", "mahindra and mahindra": "M&M",
    "eicher motors": "EICHERMOT",
    "tata steel": "TATASTEEL", "jsw steel": "JSWSTEEL", "hindalco": "HINDALCO",
    "sun pharma": "SUNPHARMA", "sun pharmaceutical": "SUNPHARMA", "dr reddy's": "DRREDDY",
    "dr. reddy's": "DRREDDY", "dr reddys": "DRREDDY", "cipla": "CIPLA", "apollo hospitals": "APOLLOHOSP",
    "max healthcare": "MAXHEALTH",
    "ultratech cement": "ULTRACEMCO", "ultratech": "ULTRACEMCO", "grasim": "GRASIM",
    "adani enterprises": "ADANIENT", "adani ports": "ADANIPORTS",
    "power grid": "POWERGRID", "coal india": "COALINDIA", "bharat electronics": "BEL",
    "interglobe aviation": "INDIGO", "indigo": "INDIGO", "zomato": "ETERNAL",
}

#: Tickers that are also everyday capitals in financial headlines: never tagged from the ticker alone.
STOP_TICKERS = frozenset({
    "AI", "ALL", "AND", "BIG", "BSE", "BUY", "CAN", "CASH", "CEO", "CFO", "CPI", "DII", "DOW", "ECB", "EPS", "ESG",
    "ETF", "EU", "EV", "FED", "FII", "FOMC", "FPI", "FTSE", "FUND", "FY", "GAS", "GDP", "GIFT", "GOLD", "GST", "HOLD",
    "IMF", "INC", "INDIA", "INR", "IPO", "IT", "KEY", "LIVE", "LTD", "MCX", "MF", "MSCI", "NAV", "NEW", "NEWS", "NIFTY",
    "NIM", "NPA", "NSE", "OIL", "ONE", "OPEC", "PAT", "PLC", "PMI", "POWER", "RBI", "SEBI", "SELL", "SENSEX", "SGX",
    "SIP", "STOP", "TARGET", "THE", "TOP", "UK", "UPDATE", "UPI", "US", "USD", "VIX", "WPI",
})

#: A company name made only of these words is a phrase headlines use for other
#: things ("Global Health" is Medanta's listed name and a news topic).
COMMON_WORDS = frozenset({
    "a", "and", "best", "big", "capital", "care", "city", "digital", "energy", "first", "future", "general", "global",
    "gold", "good", "great", "green", "health", "high", "home", "international", "life", "market", "modern",
    "national", "new", "of", "one", "power", "prime", "royal", "smart", "standard", "star", "sun", "super", "the",
    "time", "united", "universal", "world",
})

#: Suffixes the master's names carry that headlines drop.
_SUFFIXES = {"LTD", "LTD.", "LIMITED", "(INDIA)", "PVT", "PRIVATE"}
_TICKER_TOKEN = re.compile(r"(?<![A-Za-z0-9&-])([A-Z][A-Z0-9&-]{2,19})(?![A-Za-z0-9&-])")
_VALID_TICKER = re.compile(r"^[A-Z0-9&-]{1,20}$")
#: A text with more capitals than this among its letters is written in capitals.
_SHOUTING = 0.6


def ticker_of(symbol: str) -> str:
    """'NSE:BAJAJ-AUTO-EQ' -> 'BAJAJ-AUTO'."""
    text = symbol.strip().upper()
    text = text[4:] if text.startswith("NSE:") else text
    return text[:-3] if text.endswith("-EQ") else text


def company_phrase(name: str) -> str:
    """'RELIANCE INDUSTRIES LTD' -> 'reliance industries'; '' when fewer than two words are left."""
    words = [w for w in re.sub(r"[^A-Za-z0-9&.' ()-]", " ", name).upper().split()]
    while words and words[-1] in _SUFFIXES:
        words.pop()
    if len(words) < 2 or all(w.lower() in COMMON_WORDS for w in words):
        return ""
    return " ".join(words).lower()


class SymbolTagger:
    """Built once per run from (ticker, company name) pairs; `tag` is pure."""

    def __init__(self, equities: Iterable[Tuple[str, str]]):
        self.tickers = set()
        phrases: Dict[str, str] = {}
        for ticker, name in equities:
            ticker = ticker_of(ticker)
            if not _VALID_TICKER.match(ticker):
                continue
            self.tickers.add(ticker)
            phrase = company_phrase(name or "")
            if phrase:
                phrases.setdefault(phrase, ticker)
        # An alias names a company only when the master lists it: a tag is always a tradable NSE symbol.
        for alias, ticker in ALIASES.items():
            if ticker in self.tickers:
                phrases[alias] = ticker
        self.phrases = phrases
        ordered = sorted(phrases, key=len, reverse=True)
        self._phrase_re = (re.compile(r"(?<![a-z0-9&])(" + "|".join(re.escape(p) for p in ordered) + r")(?![a-z0-9&])")
                           if ordered else None)
        blocked = "|".join(re.escape(p) for p in BLOCKED_PHRASES)
        self._blocked_re = re.compile(r"(?<![a-z0-9])(" + blocked + r")(?![a-z0-9])")

    def tag(self, text: str) -> Tuple[str, ...]:
        """The NSE tickers `text` names, sorted."""
        if not text:
            return ()
        found = set()
        # Lower-cased character by character, so every index still points at the same character of `text`.
        lower = "".join(c.lower() if len(c.lower()) == 1 else c for c in text)
        lower = self._blocked_re.sub(lambda m: " " * len(m.group(0)), lower)
        if self._phrase_re is not None:
            def take(m: "re.Match") -> str:
                found.add(self.phrases[m.group(1)])
                return " " * len(m.group(0))
            lower = self._phrase_re.sub(take, lower)
        letters = [c for c in text if c.isalpha()]
        shouting = letters and sum(c.isupper() for c in letters) / len(letters) > _SHOUTING
        if not shouting:
            # The same blanking on the original text, so a phrase already matched is not a ticker again.
            masked = "".join(c if lower[k] != " " or c == " " else " " for k, c in enumerate(text))
            for token in _TICKER_TOKEN.findall(masked):
                if token in self.tickers and token not in STOP_TICKERS:
                    found.add(token)
        return tuple(sorted(found))


_EQUITIES_SQL = ('SELECT "Symbol", "Description" FROM instruments '
                 'WHERE "Exchange" = \'NSE\' AND "Segment" = \'CM\' AND "Symbol" LIKE \'NSE:%%-EQ\'')


def load_equities(conn) -> List[Tuple[str, str]]:
    """
    NSE cash equities from the instrument master: (ticker, company name). The
    -EQ series only: BE/BZ trade-to-trade, SME and bond series are not what a
    headline means by a company. (Some -EQ rows carry a mis-parsed
    InstrumentType, BAJAJ-AUTO's reads "AUTO-EQ", so the symbol decides.)
    """
    with conn.cursor() as cur:
        cur.execute(_EQUITIES_SQL)
        return [(ticker_of(symbol), str(name or "")) for symbol, name in cur.fetchall()]


def valid_symbols(symbols: Sequence[str]) -> bool:
    return all(isinstance(s, str) and _VALID_TICKER.match(s) for s in symbols)
