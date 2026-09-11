"""
Implied volatility and greeks for option ticks, from whichever feed sent them.

Lifted out of the FYERS streamer unchanged. It needs only a platform tick — a
canonical symbol and a last price — so every vendor's options get priced the
same way, instead of one feed having greeks and the next quietly not.
"""

from core.option_symbol import parse_option_symbol, years_to_expiry

#: The spot instrument each underlying is quoted by. Mirrors the API's
#: UnderlyingCatalog; without SENSEX and the rest here their options could never
#: be priced at all.
UNDERLYING_BY_SPOT_SYMBOL = {
    "NSE:NIFTY50-INDEX": "NIFTY",
    "NSE:NIFTYBANK-INDEX": "BANKNIFTY",
    "NSE:FINNIFTY-INDEX": "FINNIFTY",
    "NSE:MIDCPNIFTY-INDEX": "MIDCPNIFTY",
    "NSE:NIFTYNXT50-INDEX": "NIFTYNXT50",
    "BSE:SENSEX-INDEX": "SENSEX",
    "BSE:BANKEX-INDEX": "BANKEX",
}


class GreeksEnricher:
    """
    Keeps the latest spot per underlying and prices option ticks against it.

    The spot map starts EMPTY. It used to be seeded with two made-up prices, so
    before the first index tick every option was priced against a number nobody
    had observed — and anything that was not NIFTY or BANKNIFTY was priced
    against BANKNIFTY's 51,000 forever. A missing spot now means no greeks,
    which is the honest answer until the index reports.
    """

    def __init__(self):
        self.latest_spots: dict[str, float] = {}

    def enrich(self, tick: dict) -> dict:
        symbol = tick.get("symbol")
        ltp = tick.get("lastTradedPrice")
        if not symbol or not ltp:
            return tick

        underlying = UNDERLYING_BY_SPOT_SYMBOL.get(symbol)
        if underlying:
            self.latest_spots[underlying] = ltp
            return tick

        # All three inputs used to be wrong. The symbol was matched with a regex
        # anchored to "NSE:", so every BSE contract silently got nothing. The
        # spot came from a two-entry dict with hardcoded fallbacks. And the time
        # to expiry was a flat seven days "for demo" — the same option at the
        # same price implies 20% vol over seven days, 38% over two and 110% on
        # expiry morning, so the number that decides whether an option is dear
        # or cheap was computed against a date the contract does not have.
        contract = parse_option_symbol(symbol)
        if not contract:
            return tick

        spot = self.latest_spots.get(contract.underlying)
        tte_years = years_to_expiry(contract.expiry)

        # No invented spot, and nothing priced after the bell. Missing greeks
        # are honest; wrong ones are read as fact by whatever comes next.
        greeks = None
        if spot and spot > 0 and tte_years > 0:
            from core.greeks_calculator import calculate_greeks
            greeks = calculate_greeks(
                spot=spot,
                strike=contract.strike,
                tte_years=tte_years,
                option_type=contract.kind,
                option_price=ltp,
            )

        tick["impliedVolatility"] = greeks.iv if greeks else None
        tick["delta"] = greeks.delta if greeks else None
        tick["gamma"] = greeks.gamma if greeks else None
        tick["theta"] = greeks.theta if greeks else None
        tick["vega"] = greeks.vega if greeks else None
        return tick
