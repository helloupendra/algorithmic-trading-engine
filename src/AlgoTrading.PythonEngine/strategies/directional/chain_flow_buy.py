"""
strategies/directional/chain_flow_buy.py

Option BUYING on an index when price, open-interest flow, option volume and
implied volatility agree.

A 5-minute index candle sets the direction. The option chain has to confirm
it: writers adding puts and leaving calls near the money for a CE buy (the
mirror for a PE), the option being bought trading heavier than usual, its
build-up reading long build-up or short covering, and implied volatility not
already inflated, because a buyer who pays for a volatility spike loses to its
unwinding even when the direction is right.

The chain is read from the platform's own recording (GET /api/OptionChain/view:
the newest per-minute capture with live quotes laid over it) by this strategy
itself, throttled. Keeping the fetch here, rather than in the runner, leaves
every other strategy's code path untouched.

Every closed candle prints one DATA line: chain age, the gap between the
chain's spot and the index candle, ATM IV and the OI flow. It is how a run
also proves the data it trades on is real and current; a check that fails
blocks the entry and says which one.

There is no exit here, as in CrudeMomentum: the run's risk rules close the leg
(a leg target and stop-loss in premium points or percent), and the platform
squares off at 15:30.
"""

from __future__ import annotations

import time
import uuid
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional, Tuple

from strategies import indicators
from strategies.base_strategy import (
    BaseStrategy,
    ContractRequirement,
    DataRequirement,
    StrategyInput,
    StrategySignal,
)

IST = timezone(timedelta(hours=5, minutes=30))

#: Build-up readings that mean the premium of that option is being bid up.
#: Long build-up: price up, OI up. Short covering: price up, OI down.
RISING_PREMIUM = frozenset({"LongBuildUp", "ShortCovering"})


# ------------------------------------------------------------------ pure rules --
# Module-level and pure, so the tests pin every rule with plain numbers.

def hhmm(moment: datetime) -> int:
    """09:30 IST as 930."""
    local = moment.astimezone(IST)
    return local.hour * 100 + local.minute


def chain_rows(chain: Dict[str, Any]) -> List[Dict[str, Any]]:
    return [row for row in (chain.get("strikes") or []) if row.get("strikePrice") is not None]


def near_money(chain: Dict[str, Any], atm: float, count: int) -> List[Dict[str, Any]]:
    """The `count` listed strikes either side of `atm`, and `atm` itself."""
    rows = sorted(chain_rows(chain), key=lambda r: float(r["strikePrice"]))
    if not rows:
        return []
    index = min(range(len(rows)), key=lambda i: abs(float(rows[i]["strikePrice"]) - atm))
    return rows[max(0, index - count): index + count + 1]


def oi_by_strike(rows: List[Dict[str, Any]]) -> Dict[str, Tuple[Optional[float], Optional[float]]]:
    """strike -> (call OI, put OI); an unknown OI stays None, never zero."""
    result: Dict[str, Tuple[Optional[float], Optional[float]]] = {}
    for row in rows:
        call = row.get("call") or {}
        put = row.get("put") or {}
        result[str(float(row["strikePrice"]))] = (
            float(call["openInterest"]) if call.get("openInterest") is not None else None,
            float(put["openInterest"]) if put.get("openInterest") is not None else None,
        )
    return result


def oi_flow(now: Dict[str, Tuple[Optional[float], Optional[float]]],
            then: Dict[str, Tuple[Optional[float], Optional[float]]]) -> Optional[Dict[str, float]]:
    """
    Put OI added minus call OI added over the same strikes between two samples.

    Only strikes known in both samples count: the near-money window moves with
    the index, and summing a different set of strikes each time would read the
    move itself as a change in open interest.
    """
    call_change = put_change = total = 0.0
    matched = 0
    for strike, (call_now, put_now) in now.items():
        before = then.get(strike)
        if before is None or None in (call_now, put_now, before[0], before[1]):
            continue
        call_change += call_now - before[0]
        put_change += put_now - before[1]
        total += call_now + put_now
        matched += 1
    if matched == 0 or total <= 0:
        return None
    net = put_change - call_change
    return {"call_change": call_change, "put_change": put_change, "net": net,
            "net_pct": 100.0 * net / total, "strikes": matched}


def atm_iv(chain: Dict[str, Any]) -> Optional[float]:
    header = chain.get("header") or {}
    value = header.get("atTheMoneyIv")
    try:
        iv = float(value) if value is not None else None
    except (TypeError, ValueError):
        iv = None
    return iv if iv and iv > 0 else None


def pick_leg(chain: Dict[str, Any], atm: float, side: str, target_delta: float, search_strikes: int,
             min_premium: float) -> Optional[Dict[str, Any]]:
    """
    The CE (side "call") or PE (side "put") near the money whose |delta| is
    closest to `target_delta`, among legs that have a price of at least
    `min_premium` and a delta. None when no leg qualifies.
    """
    best: Optional[Tuple[float, Dict[str, Any], float]] = None
    for row in near_money(chain, atm, search_strikes):
        leg = row.get(side) or {}
        delta, price, symbol = leg.get("delta"), leg.get("lastTradedPrice"), leg.get("symbol")
        if delta is None or price is None or not symbol or float(price) < min_premium:
            continue
        distance = abs(abs(float(delta)) - target_delta)
        if best is None or distance < best[0]:
            best = (distance, leg, float(row["strikePrice"]))
    if best is None:
        return None
    return {**best[1], "strikePrice": best[2]}


def data_check(chain: Optional[Dict[str, Any]], index_close: float, now_utc: datetime,
               max_age_seconds: float, max_spot_gap_pct: float) -> Tuple[bool, str, Dict[str, Any]]:
    """
    Whether the chain can be traded on: present, fresh, and describing the same
    market as the index candle. Returns (ok, why-not, the numbers checked).
    """
    if not chain:
        return False, "no option chain", {}
    header = chain.get("header") or {}
    captured = indicators.parse_utc(header.get("snapshotCapturedUtc") or chain.get("asOfUtc"))
    facts: Dict[str, Any] = {}
    if captured is None:
        return False, "the chain carries no capture time", facts
    age = (now_utc - captured).total_seconds()
    facts["chain_age_s"] = round(age)
    spot = (header.get("spot") or {}).get("lastPrice") or chain.get("spotPrice")
    try:
        spot = float(spot) if spot is not None else None
    except (TypeError, ValueError):
        spot = None
    if spot:
        facts["chain_spot"] = spot
        facts["spot_gap_pct"] = round(100.0 * abs(spot - index_close) / index_close, 3) if index_close else None
    facts["atm_iv"] = atm_iv(chain)
    facts["live_legs"] = header.get("liveLegs")

    if age > max_age_seconds:
        return False, f"chain is {age:.0f}s old (limit {max_age_seconds:.0f}s)", facts
    if not spot or not index_close:
        return False, "no spot price to compare the chain with", facts
    if facts["spot_gap_pct"] is not None and facts["spot_gap_pct"] > max_spot_gap_pct:
        return False, f"chain spot {spot:.2f} is {facts['spot_gap_pct']}% from the index close {index_close:.2f}", facts
    if facts["atm_iv"] is None:
        return False, "the chain has no ATM IV", facts
    return True, "", facts


def volume_surge(bars: List[Any], multiple: float, lookback: int) -> Optional[Tuple[float, float]]:
    """(signal bar volume, mean of the `lookback` bars before it) when it is at least `multiple` times that mean."""
    if len(bars) < lookback + 2:
        return None
    signal = float(getattr(bars[-2], "volume", 0.0) or 0.0)
    before = [float(getattr(b, "volume", 0.0) or 0.0) for b in bars[-2 - lookback:-2]]
    mean = sum(before) / len(before) if before else 0.0
    if mean <= 0 or signal < multiple * mean:
        return None
    return signal, mean


# ------------------------------------------------------------------- strategy --

class ChainFlowBuyStrategy(BaseStrategy):
    """
    Buys a near-the-money CE or PE when a 5-minute index trend candle is
    confirmed by open-interest flow, option volume, build-up and a calm IV.
    """

    name = "ChainFlowBuy"
    description = (
        "Intraday option BUYING on NIFTY, BANKNIFTY and SENSEX (and FINNIFTY / MIDCPNIFTY), confirmed by the option "
        "chain. On each closed 5-minute index candle it looks for a trend candle: close above the 9 and 21 EMA "
        "stacked upwards and above the session open for a CE (the mirror for a PE). It buys only when the chain "
        "agrees: over the last `flow_lookback_minutes`, put OI added minus call OI added across the strikes near "
        "the money is at least `flow_min_pct` of their OI (writers backing the move), the option being bought "
        "traded at least `volume_multiple` times its recent 5-minute volume, its build-up reads long build-up or "
        "short covering, and ATM IV is no more than `iv_max_rise_pct` above the session's lowest reading. The "
        "strike is the one near the money whose delta is closest to `target_delta`. Entries only between "
        "`entry_start` and `entry_end` IST (earlier cut-off on expiry day), at most `max_trades_per_session` a day, "
        "and not again until the trend candle condition resets. Every candle logs a DATA line (chain age, spot "
        "gap, ATM IV, OI flow) and a failed data check blocks the entry. No built-in exit: set a leg target and "
        "stop-loss when you start it; the platform squares off at 15:30."
    )
    category = "Directional"
    supported_underlyings: List[str] = ["NIFTY", "BANKNIFTY", "SENSEX", "FINNIFTY", "MIDCPNIFTY"]
    instrument_kind = "options"
    legs_summary = "Buy one near-ATM CE (bullish) or PE (bearish), chosen by delta"
    default_lots = 1
    default_params: Dict[str, Any] = {
        "ema_fast": 9,
        "ema_slow": 21,
        "flow_strikes": 3,
        "flow_lookback_minutes": 15,
        "flow_min_pct": 1.0,
        "volume_multiple": 1.5,
        "volume_lookback": 6,
        "require_buildup": True,
        "iv_max_rise_pct": 20.0,
        "target_delta": 0.5,
        "delta_search_strikes": 3,
        "min_premium": 5.0,
        "entry_start": "09:30",
        "entry_end": "14:45",
        "expiry_day_entry_end": "13:30",
        "max_trades_per_session": 3,
        "max_chain_age_seconds": 180,
        "max_spot_gap_pct": 0.35,
        "chain_refresh_seconds": 20,
    }

    @classmethod
    def get_data_requirements(cls) -> List[DataRequirement]:
        return [
            DataRequirement(symbol_type="index", resolution="5m"),
            DataRequirement(symbol_type="atm_ce", resolution="5m"),
            DataRequirement(symbol_type="atm_pe", resolution="5m"),
        ]

    @classmethod
    def get_contract_requirements(cls, params: Optional[Dict[str, Any]] = None) -> List[ContractRequirement]:
        # Both sides at the money: their 5-minute volume is the surge check, and
        # declaring them keeps them subscribed on the live feed.
        return [
            ContractRequirement(key="atm_ce", option_type="CE"),
            ContractRequirement(key="atm_pe", option_type="PE"),
        ]

    def __init__(self, params: Dict[str, Any] = None):
        merged = {**self.default_params, **(params or {})}
        self.params = merged
        self.lots = self.lots_from(params, self.default_lots)
        self._api = None
        self._chain: Optional[Dict[str, Any]] = None
        self._chain_fetched_at = 0.0
        self._chain_error_said = ""

    def _p(self, key: str) -> Any:
        return self.params.get(key, self.default_params[key])

    @staticmethod
    def _clock(text: str) -> int:
        hours, minutes = str(text).split(":")
        return int(hours) * 100 + int(minutes)

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "session_date": None,
            "trades_this_session": 0,
            # "CE" or "PE" once an entry is taken on that side; cleared when the
            # trend candle condition for that side stops holding.
            "disarmed_side": None,
            "last_evaluated_bar": None,
            "oi_samples": [],      # [[epoch seconds, {strike: [call OI, put OI]}], ...]
            "iv_low": None,
            "last_group_id": None,
        }

    # ---------------------------------------------------------------- chain --

    def _fetch_chain(self, underlying: str) -> Optional[Dict[str, Any]]:
        """The platform's chain view, at most once every `chain_refresh_seconds`."""
        if time.monotonic() - self._chain_fetched_at < float(self._p("chain_refresh_seconds")) and self._chain:
            return self._chain
        try:
            if self._api is None:
                # Imported here, not at the top: discovery imports every strategy
                # module and must not open API sessions while doing it.
                from core.api_client import PlatformApiClient
                from core.config import API_BASE_URL, VERIFY_SSL
                self._api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)
            resp = self._api.http.get(
                f"{self._api.base_url}/api/OptionChain/view",
                params={"underlying": underlying},
                verify=self._api.verify_ssl,
                timeout=15,
            )
            resp.raise_for_status()
            self._chain = resp.json()
            self._chain_fetched_at = time.monotonic()
            self._chain_error_said = ""
        except Exception as ex:  # a failed fetch is a skipped candle, never a crashed run
            message = f"{type(ex).__name__}: {ex}"
            if message != self._chain_error_said:
                print(f"[{self.name}] option chain fetch failed: {message}", flush=True)
                self._chain_error_said = message
        return self._chain

    # ----------------------------------------------------------------- loop --

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals: List[StrategySignal] = []
        metadata = inp.metadata or {}
        if metadata.get("source") == "warmup":
            return signals

        bars = inp.bars.get("5m", {}).get("index", [])
        slow = int(self._p("ema_slow"))
        if len(bars) < slow + 2:
            return signals

        signal_bar = bars[-2]
        stamp = getattr(signal_bar, "timestamp_utc", None)
        if stamp is None or state.get("last_evaluated_bar") == stamp:
            return signals
        state["last_evaluated_bar"] = stamp

        bar_start = indicators.parse_utc(stamp)
        session = indicators.session_date(signal_bar)
        if bar_start is None or session is None:
            return signals
        if state.get("session_date") != session:
            state.update(session_date=session, trades_this_session=0, disarmed_side=None,
                         oi_samples=[], iv_low=None)

        # The chain is the platform's recording of the live market. A replay has
        # no way to ask for "now", so it sits out rather than trading on today's
        # chain against an old candle.
        if inp.mode != "LivePaper" or metadata.get("chain") == "none":
            return signals

        now_utc = datetime.now(timezone.utc)
        decided_at = bar_start + timedelta(minutes=5)

        history = bars[: len(bars) - 1]
        closes = [float(b.close) for b in history]
        ema_fast = indicators.ema(closes, int(self._p("ema_fast")))
        ema_slow = indicators.ema(closes, slow)
        session_bars = [b for b in history if indicators.session_date(b) == session]
        if ema_fast is None or ema_slow is None or not session_bars:
            return signals

        close, open_ = float(signal_bar.close), float(signal_bar.open)
        session_open = float(session_bars[0].open)
        bullish = close > open_ and close > ema_fast > ema_slow and close > session_open
        bearish = close < open_ and close < ema_fast < ema_slow and close < session_open

        # Re-arm a side once its trend candle condition stops holding.
        disarmed = state.get("disarmed_side")
        if (disarmed == "CE" and not (close > ema_fast > ema_slow)) or \
           (disarmed == "PE" and not (close < ema_fast < ema_slow)):
            state["disarmed_side"] = None

        chain = self._fetch_chain(inp.underlying)
        ok, why, facts = data_check(chain, close, now_utc, float(self._p("max_chain_age_seconds")),
                                    float(self._p("max_spot_gap_pct")))

        # OI and IV history for the session, sampled once per closed candle.
        flow = None
        if ok:
            atm = float((chain.get("header") or {}).get("atTheMoneyStrike") or chain.get("atTheMoneyStrike") or close)
            window = near_money(chain, atm, int(self._p("flow_strikes")) + 2)
            sample_now = oi_by_strike(window)
            samples = state.setdefault("oi_samples", [])
            lookback_s = 60.0 * float(self._p("flow_lookback_minutes"))
            then = None
            for at, snapshot in samples:
                if decided_at.timestamp() - at >= lookback_s:
                    then = snapshot
            if then is not None:
                flow = oi_flow({k: v for k, v in sample_now.items()
                                if k in oi_by_strike(near_money(chain, atm, int(self._p("flow_strikes"))))},
                               {k: tuple(v) for k, v in then.items()})
            samples.append([decided_at.timestamp(), {k: list(v) for k, v in sample_now.items()}])
            # Two lookbacks of history is all a decision ever reads.
            del samples[:-max(3, int(2 * lookback_s / 300) + 2)]
            iv = facts.get("atm_iv")
            if iv is not None:
                state["iv_low"] = iv if state.get("iv_low") is None else min(state["iv_low"], iv)

        flow_text = f"{flow['net_pct']:+.2f}% over {flow['strikes']} strikes" if flow else "building history"
        print(
            f"[{self.name}] DATA {inp.underlying} {decided_at.astimezone(IST):%H:%M} "
            f"index C {close:.2f} | chain {'OK' if ok else 'BLOCKED: ' + why} | "
            f"age {facts.get('chain_age_s', '—')}s, spot {facts.get('chain_spot', '—')} "
            f"(gap {facts.get('spot_gap_pct', '—')}%), ATM IV {facts.get('atm_iv', '—')} "
            f"(session low {state.get('iv_low') if state.get('iv_low') is not None else '—'}), "
            f"live legs {facts.get('live_legs', '—')}, OI flow {flow_text} | "
            f"trend {'UP' if bullish else 'DOWN' if bearish else 'none'}",
            flush=True,
        )

        if not ok or flow is None or not (bullish or bearish):
            return signals

        # ---- the entry gates, cheapest first --------------------------------
        clock = hhmm(decided_at)
        expiry = str(chain.get("expiryDate") or "")[:10]
        last_entry = self._clock(self._p("expiry_day_entry_end") if expiry == session else self._p("entry_end"))
        if clock < self._clock(self._p("entry_start")) or clock > last_entry:
            return signals
        if state.get("trades_this_session", 0) >= int(self._p("max_trades_per_session")):
            return signals

        side = "CE" if bullish else "PE"
        if state.get("disarmed_side") == side:
            return signals

        min_flow = float(self._p("flow_min_pct"))
        if (side == "CE" and flow["net_pct"] < min_flow) or (side == "PE" and flow["net_pct"] > -min_flow):
            return signals

        iv, iv_low = facts.get("atm_iv"), state.get("iv_low")
        if iv is None or iv_low is None or iv > iv_low * (1.0 + float(self._p("iv_max_rise_pct")) / 100.0):
            print(f"[{self.name}] {inp.underlying} {side} skipped: ATM IV {iv} is more than "
                  f"{self._p('iv_max_rise_pct')}% above the session low {iv_low}", flush=True)
            return signals

        option_bars = inp.bars.get("5m", {}).get("atm_ce" if side == "CE" else "atm_pe", [])
        surge = volume_surge(option_bars, float(self._p("volume_multiple")), int(self._p("volume_lookback")))
        if surge is None:
            return signals

        atm = float((chain.get("header") or {}).get("atTheMoneyStrike") or chain.get("atTheMoneyStrike") or close)
        leg = pick_leg(chain, atm, "call" if side == "CE" else "put", float(self._p("target_delta")),
                       int(self._p("delta_search_strikes")), float(self._p("min_premium")))
        if leg is None:
            return signals
        if bool(self._p("require_buildup")) and leg.get("buildUp") not in RISING_PREMIUM:
            print(f"[{self.name}] {inp.underlying} {side} skipped: {leg.get('symbol')} build-up is "
                  f"{leg.get('buildUp')}, not long build-up or short covering", flush=True)
            return signals

        group_id = str(uuid.uuid4())
        reason = (
            f"{side} buy: 5m trend {'up' if side == 'CE' else 'down'} (C {close:.2f}, EMA{self._p('ema_fast')} "
            f"{ema_fast:.2f}, EMA{slow} {ema_slow:.2f}); OI flow {flow['net_pct']:+.2f}% (puts {flow['put_change']:+.0f}, "
            f"calls {flow['call_change']:+.0f}); ATM volume {surge[0]:.0f} vs {surge[1]:.0f} avg; "
            f"IV {iv:.1f} (low {iv_low:.1f}); {leg.get('symbol')} delta {float(leg['delta']):.2f}, "
            f"build-up {leg.get('buildUp')}, LTP {float(leg['lastTradedPrice']):.2f}."
        )
        signals.append(StrategySignal(
            strategy_name=self.name,
            signal_type="OPEN_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=reason,
            symbol=leg["symbol"],
            price=None,
            legs=[{"symbol": leg["symbol"], "side": "BUY", "quantity": self.lots, "price": None}],
            metadata={
                "group_id": group_id,
                "strategy_type": "Directional",
                "direction": "BUY",
                "option_side": side,
                "signal_bar_utc": str(stamp),
                "index_close": close,
                "ema_fast": round(ema_fast, 2),
                "ema_slow": round(ema_slow, 2),
                "oi_flow_pct": round(flow["net_pct"], 3),
                "oi_put_change": flow["put_change"],
                "oi_call_change": flow["call_change"],
                "atm_volume": surge[0],
                "atm_volume_mean": round(surge[1], 1),
                "atm_iv": iv,
                "iv_session_low": iv_low,
                "strike": leg.get("strikePrice"),
                "delta": leg.get("delta"),
                "build_up": leg.get("buildUp"),
                "chain_ltp": leg.get("lastTradedPrice"),
                "chain_age_s": facts.get("chain_age_s"),
                "spot_gap_pct": facts.get("spot_gap_pct"),
                "underlying_price": close,
            },
        ))
        state["trades_this_session"] = state.get("trades_this_session", 0) + 1
        state["disarmed_side"] = side
        state["last_group_id"] = group_id
        return signals
