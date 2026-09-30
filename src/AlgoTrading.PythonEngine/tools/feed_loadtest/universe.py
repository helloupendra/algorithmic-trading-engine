"""
The load test's instruments and Dhan packets, shared by the fake Dhan server
and the fake API so both name every instrument the same way.

About 312 instruments shaped like the 28 Sep subscription: 6 indices (Quote),
256 index options (Full), 24 MCX contracts (Full) and 26 NSE equities (Quote),
split into a 158-symbol watchlist and 154 extras (the chain-recording
universe), plus one probe index whose price is the time it was sent.

Option symbols are dated from today, so they parse and are priced for greeks
exactly as live ones are, whenever the test runs. Byte layouts are the ones
market_data/live/vendors/dhan.py reads (little-endian, header <BHBI).
"""

import math
import struct
from datetime import date, timedelta

IDX_I, NSE_EQ, NSE_FNO, MCX_COMM, BSE_FNO = 0, 1, 2, 5, 8
SEGMENT_NAMES = {IDX_I: "IDX_I", NSE_EQ: "NSE_EQ", NSE_FNO: "NSE_FNO", MCX_COMM: "MCX_COMM", BSE_FNO: "BSE_FNO"}
SEGMENT_NUMBERS = {name: number for number, name in SEGMENT_NAMES.items()}

#: The probe: an index nothing else sends, quoted every 2 s with a price that
#: is the send time (epoch milliseconds modulo 1,000,000, over 100).
PROBE_SYMBOL = "NSE:LOADPROBE-INDEX"
PROBE_KEY = (IDX_I, 9001)

WATCHLIST_SIZE = 158

#: Packets a second per instrument at the base mix, before scaling to a rate.
BASE_RATES = {"option": 3, "mcx": 5, "index": 2, "equity": 1}

SPOTS = {"NIFTY": 25_000.0, "BANKNIFTY": 55_000.0, "FINNIFTY": 26_000.0, "MIDCPNIFTY": 13_000.0,
         "SENSEX": 82_000.0, "BANKEX": 62_000.0}
INDICES = {"NIFTY": (13, "NSE:NIFTY50-INDEX"), "BANKNIFTY": (25, "NSE:NIFTYBANK-INDEX"),
           "FINNIFTY": (27, "NSE:FINNIFTY-INDEX"), "MIDCPNIFTY": (442, "NSE:MIDCPNIFTY-INDEX"),
           "SENSEX": (51, "BSE:SENSEX-INDEX"), "BANKEX": (69, "BSE:BANKEX-INDEX")}
_MONTHS = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]
_WEEKLY_MONTH = {10: "O", 11: "N", 12: "D"}


def _next_weekday(today: date, weekday: int) -> date:
    """The next `weekday` (Mon=0) strictly after today, so the contract has time left."""
    ahead = (weekday - today.weekday()) % 7 or 7
    return today + timedelta(days=ahead)


def _weekly(stem: str, expiry: date) -> str:
    return f"{stem}{expiry.year % 100:02d}{_WEEKLY_MONTH.get(expiry.month, str(expiry.month))}{expiry.day:02d}"


def _monthly(stem: str, today: date) -> str:
    year, month = (today.year, today.month + 1) if today.month < 12 else (today.year + 1, 1)
    return f"{stem}{year % 100:02d}{_MONTHS[month - 1]}"


def _option_price(kind: str, spot: float, strike: float) -> float:
    """Intrinsic value plus a smooth time value: a plausible premium, never zero."""
    intrinsic = max(spot - strike, 0.0) if kind == "CE" else max(strike - spot, 0.0)
    return round(intrinsic + spot * 0.004 * math.exp(-abs(spot - strike) / (spot * 0.01)) + 0.5, 2)


def build_universe(today: date | None = None) -> list[dict]:
    """[{canonical, seg, sid, kind, price}] — indices first, then options, MCX, equities."""
    today = today or date.today()
    out = [dict(canonical=canonical, seg=IDX_I, sid=sid, kind="index", price=SPOTS[name])
           for name, (sid, canonical) in INDICES.items()]

    tuesday, thursday = _next_weekday(today, 1), _next_weekday(today, 3)
    series = [
        ("NSE", "NIFTY", _weekly("NIFTY", tuesday), 50, 10),
        ("NSE", "NIFTY", _weekly("NIFTY", tuesday + timedelta(days=7)), 50, 10),
        ("NSE", "BANKNIFTY", _monthly("BANKNIFTY", today), 100, 10),
        ("BSE", "SENSEX", _weekly("SENSEX", thursday), 100, 10),
        ("NSE", "FINNIFTY", _monthly("FINNIFTY", today), 50, 5),
        ("NSE", "MIDCPNIFTY", _monthly("MIDCPNIFTY", today), 25, 5),
        ("BSE", "BANKEX", _monthly("BANKEX", today), 100, 5),
        ("NSE", "NIFTY", _monthly("NIFTY", today), 100, 5),
    ]
    sid = 40_000
    for exchange, underlying, stem, step, each_side in series:
        spot = SPOTS[underlying]
        atm = round(spot / step) * step
        for k in range(-each_side, each_side + 1):
            strike = atm + k * step
            for kind in ("CE", "PE"):
                out.append(dict(canonical=f"{exchange}:{stem}{int(strike)}{kind}",
                                seg=NSE_FNO if exchange == "NSE" else BSE_FNO, sid=sid, kind="option",
                                price=_option_price(kind, spot, strike)))
                sid += 1
    month = _monthly("", today)
    for name, price in (("CRUDEOIL", 5_900.0), ("NATURALGAS", 300.0)):
        for m in (month, _monthly("", today.replace(day=1) + timedelta(days=40))):
            out.append(dict(canonical=f"MCX:{name}{m}FUT", seg=MCX_COMM, sid=sid, kind="mcx", price=price))
            sid += 1
    for name, spot, step in (("CRUDEOIL", 5_900, 50), ("NATURALGAS", 300, 5)):
        for k in range(-2, 3):
            for kind in ("CE", "PE"):
                out.append(dict(canonical=f"MCX:{name}{month}{int(spot + k * step)}{kind}", seg=MCX_COMM, sid=sid,
                                kind="mcx", price=50.0))
                sid += 1
    for i in range(26):
        out.append(dict(canonical=f"NSE:LOADSTOCK{i:02d}-EQ", seg=NSE_EQ, sid=1000 + i, kind="equity",
                        price=1_000.0 + i))
    return out


def split_watchlist(universe: list[dict]) -> tuple[list[str], list[str]]:
    """(watchlist, extras): the probe and the first 157 instruments, then the rest."""
    names = [inst["canonical"] for inst in universe]
    return [PROBE_SYMBOL] + names[:WATCHLIST_SIZE - 1], names[WATCHLIST_SIZE - 1:]


def dhan_names(universe: list[dict]) -> dict[str, str]:
    """{canonical: "SEGMENT:SECURITYID:INSTRUMENT"} — what the fake API's resolve answers."""
    names = {inst["canonical"]: f"{SEGMENT_NAMES[inst['seg']]}:{inst['sid']}:X" for inst in universe}
    names[PROBE_SYMBOL] = f"IDX_I:{PROBE_KEY[1]}:INDEX"
    return names


# ------------------------------------------------------------------ packets

def pk_quote(seg, sid, ltp, ltt, volume=1_000_000):
    return struct.pack("<BHBIfHIfIIIffff", 4, 50, seg, sid, ltp, 25, ltt, ltp * 0.999, volume, 500, 700,
                       ltp * 0.99, 0.0, ltp * 1.01, ltp * 0.98)


def pk_prev_close(seg, sid, price, prev_oi=0):
    return struct.pack("<BHBIfI", 6, 16, seg, sid, price, prev_oi)


def pk_full(seg, sid, ltp, ltt, oi=6_543_875):
    """A Full packet (162 bytes) with all five levels of the book filled."""
    body = struct.pack("<BHBIfHIfIIIIIIffff", 8, 162, seg, sid, ltp, 75, ltt, ltp * 0.99, 38_760_000,
                       1_241_000, 1_443_000, oi, oi + 50_000, oi - 90_000, ltp * 0.9, 0.0, ltp * 1.1, ltp * 0.85)
    levels = b"".join(struct.pack("<IIHHff", 750 * (i + 1), 525 * (i + 2), 3 + i, 2 + i,
                                  ltp - 0.05 * (i + 1), ltp + 0.05 * (i + 1)) for i in range(5))
    return body + levels


def packet_for(inst: dict, ltt: int, jitter: float = 0.0) -> bytes:
    """The packet Dhan sends for this instrument's subscription mode (Full for F&O and MCX, Quote otherwise)."""
    price = inst["price"] * (1.0 + jitter)
    if inst["kind"] in ("option", "mcx"):
        return pk_full(inst["seg"], inst["sid"], price, ltt)
    return pk_quote(inst["seg"], inst["sid"], price, ltt)


def probe_packet(send_epoch_ms: int, ltt: int) -> bytes:
    return pk_quote(PROBE_KEY[0], PROBE_KEY[1], (send_epoch_ms % 1_000_000) / 100.0, ltt)


def probe_lag_ms(now_epoch_ms: int, ltp: float) -> int:
    """How long ago a probe price was sent, in ms (the price is send time mod 1,000,000 ms, over 100)."""
    return int((now_epoch_ms % 1_000_000 - round(ltp * 100)) % 1_000_000)


def scaled_rates(universe: list[dict], rate: float) -> dict[str, float]:
    """Packets a second per instrument, by kind, so the whole universe sends `rate` frames a second."""
    base = sum(BASE_RATES[inst["kind"]] for inst in universe)
    return {kind: per * rate / base for kind, per in BASE_RATES.items()}
