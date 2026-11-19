"""
strategies/directional/trend_rider_v2_be.py

Trend Rider V2 BE: TrendRiderV2 with the breakeven step that passed the pre-registered trailing test of
17 Nov 2026 (BE25), for any market the desk trades. TrendRiderV2 itself is not touched; this class only adds:

  1. **The breakeven step, on by default.** Once the underlying has been `breakeven_adr` x the average daily
     range (ADR) in the trade's favour, the stop moves to the entry level: the position is sold when the
     underlying is back at or through its entry price. V2's own stop (0.5 x ADR against) and trail (armed at
     0.5 x ADR, 0.35 x ADR give-back) are unchanged. `breakeven_adr: 0` is TrendRiderV2 exactly.
     On NIFTY, Sep 2024 - Sep 2026, one lot of 65, simple P&L, the research lab replayed it at +Rs 1,41,434
     against V2's +Rs 1,26,513, with a smaller worst day and a smaller deepest fall; it held on the untouched
     Aug 2020 - Aug 2024 (project repo, trailing-2026-11-17).

  2. **The session follows the market** (`market`: auto | nse | mcx). TrendRider reads only candles inside the
     NSE session (09:15-15:30). An MCX commodity (CRUDEOIL, NATURALGAS, ...) trades 09:00 to 23:30 while New
     York is on daylight saving and to 23:55 while it is not (MarketSessionService). On MCX this class reads
     candles from 09:00 to 23:55, which on a 23:30 day simply has none after 23:30, so "yesterday", the
     sessions of the average range, the day's open and the entry window are all MCX's. Entries default to
     09:00-22:30 there (an hour before the earlier close, as 14:30 is on NSE). `auto` picks MCX for an MCX
     underlying or an "MCX:" price symbol, and NSE (with BSE) for everything else. The run's square-off is
     the platform's, at its market's close (MarketCloseRules), as for every live run.

     The parameter is not called "session": a run's "session" is the platform's own key ("recap" marks a
     recap run), and a strategy default there would have been read as the run's kind.

  3. **Options or the share itself** (`instrument_kind`: options | equity, the platform's own name, which the
     backtest engine and the backtest dialog already read). `options` is V2's ITM call or put. `equity` buys
     the underlying share itself (the run's price symbol, NSE:HDFCBANK-EQ for HDFCBANK), long only: an
     up-day break buys the share, a down-day break is skipped and counted (`equity_long_only`), never sold
     short. The stop, the breakeven step and the trail then run on the share's own price, and `lots` is the
     number of shares (the platform's lot size for a share is 1). It asks the runner for no option contract.
     An MCX run has no share to buy and an index none either: either one with `equity` is refused.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from datetime import datetime, timedelta
from typing import Any, Dict, List, Optional

from strategies.base_strategy import BarFrame, ContractRequirement, OptionContract, StrategyInput, StrategySignal
from strategies.directional.trend_rider import FULL_SESSION_SHARE, _ist
from strategies.directional.trend_rider_v2 import TrendRiderV2Strategy
from strategies.index_exits import IndexExitRules

#: Underlyings whose price is an MCX future (UnderlyingCatalog.CommodityUnderlyings).
MCX_UNDERLYINGS = frozenset({"CRUDEOIL", "CRUDEOILM", "NATURALGAS", "NATGASMINI", "GOLD", "GOLDM", "SILVER", "SILVERM"})
#: Index underlyings (UnderlyingCatalog.IndexUnderlyings): there is no share to buy.
INDEX_UNDERLYINGS = frozenset({"NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "SENSEX", "BANKEX"})
INSTRUMENT_KINDS = ("options", "equity")
MARKETS = ("auto", "nse", "mcx")


@dataclass(frozen=True)
class MarketSession:
    """The hours the strategy reads candles in, and its entry window when the run sets none."""
    name: str
    first: str              # the first candle of the session
    close: str              # the latest the market can close: candles that start before it count
    full_minutes: int       # the shortest regular session, for "a complete session" in the average range
    first_entry: str
    last_entry: str

    @property
    def window_minutes(self) -> int:
        h1, m1 = map(int, self.first.split(":"))
        h2, m2 = map(int, self.close.split(":"))
        return (h2 * 60 + m2) - (h1 * 60 + m1)


#: NSE and BSE (indices, stock options, shares): TrendRider's own session, 09:15-15:30.
NSE_SESSION = MarketSession("nse", "09:15", "15:30", 375, "09:15", "14:30")
#: MCX: 09:00 to 23:30 (New York on daylight saving) or 23:55 (not). A complete session is judged against the
#: shorter one (870 minutes), the candles are read to the later close.
MCX_SESSION = MarketSession("mcx", "09:00", "23:55", 870, "09:00", "22:30")
SESSIONS = {"nse": NSE_SESSION, "mcx": MCX_SESSION}


def market_of(underlying: Optional[str], price_symbol: Optional[str] = None) -> str:
    """"mcx" for an MCX commodity or an "MCX:" price symbol (the near-month future), else "nse"."""
    if str(price_symbol or "").strip().upper().startswith("MCX:"):
        return "mcx"
    return "mcx" if str(underlying or "").strip().upper() in MCX_UNDERLYINGS else "nse"


def _hhmm(value: Any, name: str) -> Optional[str]:
    """An explicit HH:MM, or None for "auto" (the market's default)."""
    text = str(value if value is not None else "").strip().lower()
    if text in ("", "auto", "none", "null"):
        return None
    try:
        return datetime.strptime(text, "%H:%M").strftime("%H:%M")
    except ValueError as ex:
        raise ValueError(f"{name} must be HH:MM or auto, got {value!r}") from ex


@dataclass(frozen=True)
class BreakevenExitRules(IndexExitRules):
    """
    V2's index rules plus the breakeven step: once the best move in favour has reached `breakeven_adr` x ADR,
    sell when the underlying is back at or through the entry. Checked after the stop, the trail and the time
    exit, on the same call (live, every tick of the price), so those keep their own reasons when they fire.
    """
    breakeven_adr: float = 0.0

    @property
    def any_rule(self) -> bool:
        return super().any_rule or bool(self.breakeven_adr)

    def merged(self, **overrides: Any) -> "BreakevenExitRules":
        return replace(self, **overrides)

    def check_tick(self, position: Dict[str, Any], spot: float, now_ist: Optional[datetime],
                   name: str = "the index") -> Optional[str]:
        why = super().check_tick(position, spot, now_ist, name)       # also keeps position["best"] up to date
        if why is not None or not self.breakeven_adr or spot <= 0:
            return why
        entry, adr = position.get("entry_spot"), float(position.get("adr") or 0.0)
        if entry is None or adr <= 0:
            return None
        entry = float(entry)
        favour = spot - entry if position.get("direction") == "BUY" else entry - spot
        if float(position.get("best") or 0.0) >= self.breakeven_adr * adr and favour <= 0:
            return (f"breakeven stop: {name} {spot:g} is back at the entry {entry:g} after being "
                    f"{self.breakeven_adr:g} x the {adr:.0f}-point average range in favour")
        return None


class TrendRiderV2BEStrategy(TrendRiderV2Strategy):
    name = "TrendRiderV2BE"
    description = (
        "TrendRiderV2 with the breakeven step that passed the 17 Nov 2026 trailing test, for any market: once the "
        "underlying is 0.25 x the average daily range in favour, the position is sold if it comes back to the entry "
        "price (V2's 0.5 x ADR stop and its trail are unchanged; breakeven_adr 0 is V2 exactly). It trades index or "
        "stock options (one strike in the money) and MCX commodity options in their own session (09:00 to the MCX "
        "close, entries to 22:30), or, with instrument_kind equity, buys the stock itself on up-trend days only "
        "(lots = shares). A paper/testing version beside V2; V2 is unchanged."
    )
    # The launch dialog offers only these; the API itself starts any underlying (it logs one that is not listed).
    supported_underlyings: List[str] = [
        "NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX",
        "HDFCBANK", "ICICIBANK", "SBIN", "AXISBANK", "KOTAKBANK", "RELIANCE", "INFY", "TCS", "BHARTIARTL", "LT", "ITC",
        "CRUDEOIL", "CRUDEOILM", "NATURALGAS", "NATGASMINI",
    ]
    legs_summary = ("Buy CE 1 strike ITM on an up-trend day, or Buy PE 1 strike ITM on a down-trend day (itm_steps); "
                    "with instrument_kind equity, buy the share itself on up-trend days only; one position at a time")
    default_params: Dict[str, Any] = {
        **TrendRiderV2Strategy.default_params,
        # auto: 09:15 / 14:30 on NSE and BSE (V2's), 09:00 / 22:30 on MCX. An HH:MM set by the run wins.
        "first_entry": "auto",
        "last_entry": "auto",
        # BE25: once 0.25 x ADR in favour, the stop moves to the entry; 0 turns it off (TrendRiderV2 exactly).
        "breakeven_adr": 0.25,
        "market": "auto",              # auto | nse | mcx: whose session the candles are read in
        "instrument_kind": "options",  # options | equity (the share itself, long only, lots = shares)
    }

    def __init__(self, params: Dict[str, Any] = None):
        params = dict(params or {})
        if "instrument" in params:
            raise ValueError("TrendRiderV2BE reads instrument_kind (options | equity), the platform's name for it; "
                             "there is no 'instrument' parameter")
        super().__init__(params)
        p = {**self.default_params, **params}
        try:
            self.breakeven_adr = float(p["breakeven_adr"] or 0)
        except (TypeError, ValueError) as ex:
            raise ValueError(f"breakeven_adr must be a number: {ex}") from ex
        if self.breakeven_adr < 0:
            raise ValueError("breakeven_adr cannot be negative")
        self.instrument_kind = str(p["instrument_kind"] or "options").strip().lower()
        if self.instrument_kind not in INSTRUMENT_KINDS:
            raise ValueError("instrument_kind must be options or equity")
        self.market_param = str(p["market"] or "auto").strip().lower()
        if self.market_param not in MARKETS:
            raise ValueError("market must be auto, nse or mcx")
        self._first_entry = _hhmm(p["first_entry"], "first_entry")
        self._last_entry = _hhmm(p["last_entry"], "last_entry")

        # V2's stop and trail (and any run "index_exits") with the breakeven step on top.
        base = dict(self.index_exits.__dict__) if self.index_exits is not None else {}
        rules = BreakevenExitRules(**base, breakeven_adr=self.breakeven_adr)
        self.index_exits = rules if rules.any_rule else None

        # The market is known now on a live or backtest run (the API writes "underlying" into its parameters);
        # otherwise it is read from the first call (the run's underlying and price symbol). Until then the
        # session is NSE's, which is what the warm-up size and the entry times read meanwhile.
        self.market: Optional[str] = None
        self.underlying = str(params.get("underlying") or "").strip().upper()
        self._apply(NSE_SESSION)
        if self.market_param != "auto" or self.underlying:
            self._use_market(self.market_param if self.market_param != "auto" else market_of(self.underlying),
                             self.underlying)

    # ------------------------------------------------------------- catalogue --
    @classmethod
    def get_contract_requirements(cls, params: Optional[Dict[str, Any]] = None) -> List[ContractRequirement]:
        """V2's ITM call and put; none for an equity run, which buys the share itself."""
        kind = str((params or {}).get("instrument_kind") or cls.default_params["instrument_kind"]).strip().lower()
        if kind == "equity":
            return []
        return super().get_contract_requirements(params)

    # ---------------------------------------------------------------- market --
    def _apply(self, session: MarketSession) -> None:
        """The session's hours in the attributes TrendRider reads (its own come from its NSE constants)."""
        self.market_session = session
        self.session_first = session.first
        h, m = map(int, session.close.split(":"))
        self.session_last = (datetime(2000, 1, 1, h, m) - timedelta(minutes=self.bar_minutes)).strftime("%H:%M")
        self.full_session_bars = int(FULL_SESSION_SHARE * session.full_minutes / self.bar_minutes)
        self.warmup_bars = (session.window_minutes // self.bar_minutes) * 12
        self.first_entry = self._first_entry or session.first_entry
        self.last_entry = self._last_entry or session.last_entry

    def _use_market(self, market: str, underlying: str) -> None:
        if self.instrument_kind == "equity":
            if market == "mcx":
                raise ValueError(
                    f"TrendRiderV2BE: instrument_kind equity buys the share itself, and {underlying or 'this run'} "
                    "trades on MCX, where there is no share to buy. Run it with instrument_kind options (the "
                    "commodity's options), or with equity on an NSE stock.")
            if underlying in INDEX_UNDERLYINGS:
                raise ValueError(
                    f"TrendRiderV2BE: instrument_kind equity buys the share itself, and {underlying} is an index, "
                    "which has no share to buy. Run it with instrument_kind options, or with equity on a stock.")
        self.market = market
        self._apply(SESSIONS[market])
        # The square-off time follows the market (15:15 on NSE/BSE, none on MCX) unless the run named one.
        if self.index_exits is not None and not self.square_off_from_run:
            rules = self.index_exits.merged(exit_after_ist=self.square_off_for(market))
            self.index_exits = rules if rules.any_rule else None

    @staticmethod
    def _price_symbol(inp: StrategyInput, resolution: str) -> Optional[str]:
        frames = (inp.bars.get(resolution) or {}).get("index") or []
        return str(getattr(frames[-1], "symbol", "") or "") if frames else None

    # ------------------------------------------------------------------- run --
    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        if self.market is None:
            underlying = str(inp.underlying or "").strip().upper()
            self._use_market(market_of(underlying, self._price_symbol(inp, self.resolution)), underlying)
        return [self._relabel(s, inp) for s in super().on_bar(state, inp)]

    def _closed(self, inp: StrategyInput):
        """TrendRider's closed candles, inside this run's session rather than NSE's."""
        now = None if inp.mode == "OfflineReplay" else _ist(inp.timestamp_utc)
        out = []
        for frame in (inp.bars.get(self.resolution) or {}).get("index") or []:
            at = _ist(getattr(frame, "timestamp_utc", None))
            if at is None or not self.session_first <= at.strftime("%H:%M") <= self.session_last:
                continue
            if now is not None and at + timedelta(minutes=self.bar_minutes) > now:
                continue
            out.append((at, frame))
        return out

    def _enter(self, state: Dict[str, Any], inp: StrategyInput, at: datetime, bar: BarFrame,
               day: List[BarFrame], day_open: float) -> List[StrategySignal]:
        if self.instrument_kind != "equity":
            signals = super()._enter(state, inp, at, bar, day, day_open)
        else:
            signals = self._enter_share(state, inp, at, bar, day, day_open)
        for sig in signals:
            ctx = sig.metadata.get("trend_rider")
            if isinstance(ctx, dict):
                ctx.update(market=self.market, instrument_kind=self.instrument_kind)
        return signals

    def _enter_share(self, state: Dict[str, Any], inp: StrategyInput, at: datetime, bar: BarFrame,
                     day: List[BarFrame], day_open: float) -> List[StrategySignal]:
        """V2's entry with the share as the "call"; a put-side break is skipped, never sold short."""
        if self._put_side_break(state, at, bar, day, day_open):
            self._skip(state, "equity_long_only")
            return []
        symbol = self._share_symbol(inp, bar)
        if symbol is None:
            self._skip(state, "no_share")
            return []
        share = OptionContract(symbol=symbol, underlying=str(inp.underlying or ""), expiry_date="9999-12-31",
                               strike_price=0.0, option_type="CE", instrument_type="EQ",
                               description="the share itself (instrument_kind equity)")
        signals = super()._enter(state, replace(inp, contracts={"buy_ce": share}), at, bar, day, day_open)
        for sig in signals:
            sig.reason = sig.reason.replace("; buying the call", f"; buying the share {symbol}")
        return signals

    def _put_side_break(self, state: Dict[str, Any], at: datetime, bar: BarFrame, day: List[BarFrame],
                        day_open: float) -> bool:
        """True where V2 would buy the put now: the same checks, in the same order, as its entry."""
        hhmm = at.strftime("%H:%M")
        if state["count"] >= self.max_entries or not self.first_entry <= hhmm <= self.last_entry:
            return False
        if state["busy_until"] and hhmm < state["busy_until"]:
            return False
        if self.levels == "opening_range":
            if len(day) <= self.or_bars:
                return False
            low = min(b.low for b in day[:self.or_bars])
        else:
            adr, prev = self._context(state, at.date().isoformat())
            if adr is None or prev is None:
                return False                    # V2 records the missing history itself
            low = prev[2]
        return bar.close < low and bar.close < day_open

    def _share_symbol(self, inp: StrategyInput, bar: BarFrame) -> Optional[str]:
        """The run's own price symbol when it is a share (NSE:HDFCBANK-EQ), else the NSE share of the underlying."""
        symbol = str(getattr(bar, "symbol", "") or "").strip()
        if symbol.upper().endswith("-EQ"):
            return symbol
        underlying = str(inp.underlying or self.underlying or "").strip().upper()
        if not underlying or underlying in INDEX_UNDERLYINGS or underlying in MCX_UNDERLYINGS:
            return None
        return f"NSE:{underlying}-EQ"

    def _relabel(self, sig: StrategySignal, inp: StrategyInput) -> StrategySignal:
        """TrendRider writes "NIFTY" into two of its reasons whatever the run trades; this names the underlying."""
        name = str(inp.underlying or "").strip() or "the index"
        reason = sig.reason or ""
        if name != "NIFTY":
            if reason.startswith("NIFTY closed "):
                reason = name + reason[len("NIFTY"):]
            elif reason.startswith("Trend Rider: NIFTY "):
                reason = f"Trend Rider: {name} " + reason[len("Trend Rider: NIFTY "):]
        if reason.startswith("Trend Rider V2: "):
            reason = "Trend Rider V2 BE: " + reason[len("Trend Rider V2: "):]
        elif reason.startswith("Trend Rider: "):
            reason = "Trend Rider V2 BE: " + reason[len("Trend Rider: "):]
        sig.reason = reason
        return sig
