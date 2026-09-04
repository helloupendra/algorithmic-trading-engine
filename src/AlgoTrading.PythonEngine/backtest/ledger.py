"""
backtest/ledger.py

Paper ledger for the replay. Mirrors the netting rules of the C# paper engine
(PaperTradingService) so the runner's view of P&L matches what it persists:

  - positions are keyed by (group_id, symbol); quantities are LOTS;
  - P&L = price difference x lots x lot size; SHORT positions profit when the
    price falls;
  - a same-direction fill averages the entry price, an opposite fill reduces
    the position and books realized P&L, a remainder opens a reverse position
    (never for CLOSE_GROUP, which is reduce-only: legs with no open position
    or that would increase a position are ignored and counted);
  - charges are a flat amount per lot per fill when `charges_per_lot` > 0;
  - risk helpers for the engine: per-position entry/side/lots/avg and
    `pnl_points` / `pnl_percent` (signed, profit positive), per-group P&L
    (`group_pnl`) and reduce-only exits for chosen legs (`close_positions`).

Pure Python, no I/O.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Dict, Iterable, List, Optional, Tuple

from strategies.base_strategy import StrategySignal

LONG = "LONG"
SHORT = "SHORT"


@dataclass
class LedgerPosition:
    group_id: str
    symbol: str
    side: str                      # LONG | SHORT
    lots: int
    avg_price: float
    opened_utc: str
    lot_size: int
    realized: float = 0.0
    unrealized: float = 0.0
    last_mark: Optional[float] = None
    charges: float = 0.0
    status: str = "Open"           # Open | Closed
    closed_utc: Optional[str] = None
    exit_price: Optional[float] = None
    exit_reason: Optional[str] = None
    lots_opened: int = 0           # total lots ever added (reporting only)

    @property
    def quantity(self) -> int:
        return self.lots * self.lot_size

    @property
    def pnl(self) -> float:
        return self.realized + self.unrealized

    def pnl_for(self, price: float, lots: Optional[int] = None) -> float:
        """P&L of `lots` (default: all) at `price` versus the average entry."""
        count = self.lots if lots is None else lots
        diff = (price - self.avg_price) if self.side == LONG else (self.avg_price - price)
        return diff * count * self.lot_size

    @property
    def entry_price(self) -> float:
        """Average entry premium (alias of `avg_price`, the API's AveragePrice)."""
        return self.avg_price

    @property
    def leg_side(self) -> str:
        """The leg's order side as the strategy expressed it: BUY for LONG, SELL for SHORT."""
        return "BUY" if self.side == LONG else "SELL"

    @property
    def entry_value(self) -> float:
        """Entry premium x quantity (lots x lot size)."""
        return abs(self.avg_price) * self.quantity

    @property
    def current_value(self) -> Optional[float]:
        """Last mark x quantity; None until the position has been marked."""
        return None if self.last_mark is None else abs(self.last_mark) * self.quantity

    def pnl_points(self, mark: Optional[float] = None) -> Optional[float]:
        """Signed premium points from entry (profit positive), see `pnl_points()`."""
        return pnl_points(self, mark)

    def pnl_percent(self, mark: Optional[float] = None) -> Optional[float]:
        """Signed % of the entry premium (profit positive), see `pnl_percent()`."""
        return pnl_percent(self, mark)


def pnl_points(position: LedgerPosition, mark: Optional[float] = None) -> Optional[float]:
    """
    Premium points between `mark` (default: the position's last mark) and the
    entry, signed so that profit is positive: LONG -> mark - entry, SHORT ->
    entry - mark. None when no mark is known.
    """
    price = position.last_mark if mark is None else mark
    if price is None:
        return None
    diff = float(price) - position.avg_price
    return diff if position.side == LONG else -diff


def pnl_percent(position: LedgerPosition, mark: Optional[float] = None) -> Optional[float]:
    """`pnl_points` as a percentage of the entry premium; None without a mark or a zero entry."""
    points = pnl_points(position, mark)
    if points is None or position.avg_price == 0:
        return None
    return points / abs(position.avg_price) * 100.0


@dataclass
class Fill:
    at_utc: str
    group_id: str
    symbol: str
    side: str                      # BUY | SELL
    lots: int
    price: float
    charges: float
    realized: float                # realized P&L booked by this fill


@dataclass
class ApplyResult:
    signal_type: str
    group_id: str
    legs: List[Dict[str, Any]] = field(default_factory=list)          # legs actually filled (quantity may be capped)
    ignored: List[Tuple[Dict[str, Any], str]] = field(default_factory=list)
    fills: List[Fill] = field(default_factory=list)
    realized_delta: float = 0.0
    closed: List[LedgerPosition] = field(default_factory=list)       # positions closed by this signal

    @property
    def applied(self) -> bool:
        return bool(self.legs)


def _side_for(leg_side: str) -> str:
    return LONG if str(leg_side).upper() == "BUY" else SHORT


class PaperLedger:
    """Positions in lots x lot size, with realized/unrealized P&L and charges."""

    def __init__(self, lot_size: int, charges_per_lot: float = 0.0) -> None:
        if lot_size is None or int(lot_size) < 1:
            raise ValueError("lot_size must be >= 1")
        self.lot_size = int(lot_size)
        self.charges_per_lot = max(0.0, float(charges_per_lot or 0.0))
        self._open: Dict[Tuple[str, str], LedgerPosition] = {}
        self.closed: List[LedgerPosition] = []
        self.fills: List[Fill] = []
        self.charges = 0.0
        self.ignored_legs = 0
        self._sequence = 0          # insertion order of open positions

    # --- state --------------------------------------------------------------

    def open_positions(self) -> List[LedgerPosition]:
        return list(self._open.values())

    def all_positions(self) -> List[LedgerPosition]:
        return self.closed + self.open_positions()

    def open_symbols(self) -> List[str]:
        seen: List[str] = []
        for pos in self._open.values():
            if pos.symbol not in seen:
                seen.append(pos.symbol)
        return seen

    def open_groups(self) -> List[str]:
        seen: List[str] = []
        for pos in self._open.values():
            if pos.group_id not in seen:
                seen.append(pos.group_id)
        return seen

    def has_open(self) -> bool:
        return bool(self._open)

    def position(self, group_id: str, symbol: str) -> Optional[LedgerPosition]:
        return self._open.get((group_id, symbol))

    def open_keys(self) -> List[Tuple[str, str]]:
        """(group_id, symbol) of every open position, in opening order."""
        return list(self._open.keys())

    # --- groups -------------------------------------------------------------

    def group_open_positions(self, group_id: str) -> List[LedgerPosition]:
        """The open legs of one group."""
        return [p for p in self._open.values() if p.group_id == group_id]

    def group_positions(self, group_id: str) -> List[LedgerPosition]:
        """Every position of one group, closed first, then open."""
        return [p for p in self.closed if p.group_id == group_id] + self.group_open_positions(group_id)

    def group_pnl(self, group_id: str) -> float:
        """
        The group's P&L as the group risk rule sees it: realized of every
        position ever in the group + unrealized of its open legs (charges are
        a run-level cost and stay out of the per-group number).
        """
        positions = self.group_positions(group_id)
        return sum(p.realized for p in positions) + sum(p.unrealized for p in positions if p.status == "Open")

    def group_pnls(self) -> Dict[str, float]:
        """{group_id: group_pnl} for every group that still has an open leg."""
        return {group_id: self.group_pnl(group_id) for group_id in self.open_groups()}

    def realized_pnl(self) -> float:
        return sum(p.realized for p in self.closed) + sum(p.realized for p in self._open.values())

    def unrealized_pnl(self) -> float:
        return sum(p.unrealized for p in self._open.values())

    def total_pnl(self) -> float:
        """Realized + unrealized - charges: the number the SL/target rule watches."""
        return self.realized_pnl() + self.unrealized_pnl() - self.charges

    def used_capital(self) -> float:
        """Premium notional of the open book (avg x lots x lot size)."""
        return sum(abs(p.avg_price) * p.lots * p.lot_size for p in self._open.values())

    @property
    def trades(self) -> int:
        """Closed positions (the platform's definition of a trade)."""
        return len(self.closed)

    # --- fills --------------------------------------------------------------

    def apply(self, signal_type: str, group_id: str, legs: List[Dict[str, Any]], t: str,
              reason: Optional[str] = None) -> ApplyResult:
        """
        Fill the legs of one OPEN_GROUP / CLOSE_GROUP signal at `t` (ISO UTC).
        Every leg needs `symbol`, `side` (BUY/SELL), `quantity` (lots) and a
        numeric `price`; the engine prices legs before calling this.
        """
        kind = str(signal_type or "").upper()
        if kind not in ("OPEN_GROUP", "CLOSE_GROUP"):
            raise ValueError(f"unsupported signal type {signal_type!r}")
        reduce_only = kind == "CLOSE_GROUP"
        group = str(group_id or "")
        result = ApplyResult(signal_type=kind, group_id=group)

        for leg in legs or []:
            symbol = str(leg.get("symbol") or "").strip()
            side = str(leg.get("side") or "").upper()
            try:
                lots = int(leg.get("quantity") or 0)
            except (TypeError, ValueError):
                lots = 0
            price = leg.get("price")

            if not symbol or side not in ("BUY", "SELL") or lots <= 0:
                self._ignore(result, leg, "invalid leg")
                continue

            key = (group, symbol)
            pos = self._open.get(key)
            wanted = _side_for(side)

            if pos is None and reduce_only:
                self._ignore(result, leg, "no open position")
                continue
            if price is None:
                self._ignore(result, leg, "no price")
                continue
            price = float(price)

            if pos is None:
                self._open_position(result, group, symbol, wanted, lots, price, t)
                continue

            if pos.side == wanted:
                if reduce_only:
                    self._ignore(result, leg, "would increase the position")
                    continue
                self._average_into(result, pos, lots, price, t, side)
                continue

            closing = min(lots, pos.lots)
            self._reduce(result, pos, closing, price, t, side, reason)
            remainder = lots - closing
            if remainder > 0 and not reduce_only:
                self._open_position(result, group, symbol, wanted, remainder, price, t)

        return result

    def _ignore(self, result: ApplyResult, leg: Dict[str, Any], why: str) -> None:
        self.ignored_legs += 1
        result.ignored.append((dict(leg), why))

    def _record_fill(self, result: ApplyResult, group: str, symbol: str, side: str,
                     lots: int, price: float, t: str, realized: float) -> float:
        charge = self.charges_per_lot * lots
        self.charges += charge
        fill = Fill(at_utc=t, group_id=group, symbol=symbol, side=side, lots=lots,
                    price=price, charges=charge, realized=realized)
        self.fills.append(fill)
        result.fills.append(fill)
        result.realized_delta += realized
        self._merge_leg(result, symbol, side, lots, price)
        return charge

    @staticmethod
    def _merge_leg(result: ApplyResult, symbol: str, side: str, lots: int, price: float) -> None:
        """One posted leg per (symbol, side) even when a fill was split (reduce + reverse)."""
        for leg in result.legs:
            if leg["symbol"] == symbol and leg["side"] == side and leg["price"] == price:
                leg["quantity"] += lots
                return
        result.legs.append({"symbol": symbol, "side": side, "quantity": lots, "price": price})

    def _open_position(self, result: ApplyResult, group: str, symbol: str, side: str,
                       lots: int, price: float, t: str) -> None:
        self._sequence += 1
        pos = LedgerPosition(group_id=group, symbol=symbol, side=side, lots=lots, avg_price=price,
                             opened_utc=t, lot_size=self.lot_size, last_mark=price, lots_opened=lots)
        pos.charges += self._record_fill(result, group, symbol, "BUY" if side == LONG else "SELL",
                                         lots, price, t, 0.0)
        self._open[(group, symbol)] = pos

    def _average_into(self, result: ApplyResult, pos: LedgerPosition, lots: int, price: float,
                      t: str, side: str) -> None:
        total = pos.lots + lots
        pos.avg_price = (pos.avg_price * pos.lots + price * lots) / total
        pos.lots = total
        pos.lots_opened += lots
        pos.last_mark = price
        pos.unrealized = pos.pnl_for(price)
        pos.charges += self._record_fill(result, pos.group_id, pos.symbol, side, lots, price, t, 0.0)

    def _reduce(self, result: ApplyResult, pos: LedgerPosition, lots: int, price: float,
                t: str, side: str, reason: Optional[str]) -> None:
        realized = pos.pnl_for(price, lots)
        pos.realized += realized
        pos.lots -= lots
        pos.last_mark = price
        pos.charges += self._record_fill(result, pos.group_id, pos.symbol, side, lots, price, t, realized)
        if pos.lots == 0:
            pos.unrealized = 0.0
            pos.status = "Closed"
            pos.closed_utc = t
            pos.exit_price = price
            pos.exit_reason = reason
            del self._open[(pos.group_id, pos.symbol)]
            self.closed.append(pos)
            result.closed.append(pos)
        else:
            pos.unrealized = pos.pnl_for(price)

    # --- marks / square-off -------------------------------------------------

    def mark(self, prices: Dict[str, float], t: str) -> Dict[str, float]:
        """
        Update the last mark and unrealized P&L of every open position whose
        symbol has a price. Returns the {symbol: price} map actually applied.
        """
        applied: Dict[str, float] = {}
        for pos in self._open.values():
            price = prices.get(pos.symbol)
            if price is None:
                continue
            price = float(price)
            pos.last_mark = price
            pos.unrealized = pos.pnl_for(price)
            applied[pos.symbol] = price
        return applied

    def mark_prices(self) -> Dict[str, float]:
        """Current marks of the open book ({symbol: last mark})."""
        marks: Dict[str, float] = {}
        for pos in self._open.values():
            if pos.last_mark is not None:
                marks[pos.symbol] = pos.last_mark
        return marks

    def close_positions(self, keys: Iterable[Tuple[str, str]], price_lookup: Callable[[str], Optional[float]],
                        t: str, reason: str, strategy_name: str = "",
                        metadata: Optional[Dict[str, Any]] = None) -> List[StrategySignal]:
        """
        Reduce-only exits for the open positions named by `keys`
        ((group_id, symbol) pairs; unknown or already-closed keys are skipped,
        duplicates collapse): one CLOSE_GROUP signal per group, each leg
        priced by `price_lookup` and falling back to the position's last
        mark, then its entry, so an exit is never dropped for want of a
        price. `metadata` is merged into every signal's metadata next to
        `group_id`. The signals are NOT applied; feed them through `apply`
        like any other signal.
        """
        by_group: Dict[str, List[LedgerPosition]] = {}
        seen: set = set()
        for key in keys:
            key = (str(key[0]), str(key[1]))
            if key in seen:
                continue
            seen.add(key)
            pos = self._open.get(key)
            if pos is None:
                continue
            by_group.setdefault(pos.group_id, []).append(pos)

        signals: List[StrategySignal] = []
        for group_id, positions in by_group.items():
            legs = []
            for pos in positions:
                price = price_lookup(pos.symbol)
                source = "candle"
                if price is None:
                    price, source = pos.last_mark, "last mark"
                if price is None:
                    price, source = pos.avg_price, "entry price"
                legs.append({
                    "symbol": pos.symbol,
                    "side": "SELL" if pos.side == LONG else "BUY",
                    "quantity": pos.lots,
                    "price": float(price),
                    "price_source": source,
                })
            meta: Dict[str, Any] = {"group_id": group_id}
            meta.update(metadata or {})
            signals.append(StrategySignal(
                strategy_name=strategy_name,
                signal_type="CLOSE_GROUP",
                timestamp_utc=t,
                reason=reason,
                legs=legs,
                metadata=meta,
            ))
        return signals

    def flatten_all(self, price_lookup: Callable[[str], Optional[float]], t: str, reason: str,
                    strategy_name: str = "") -> List[StrategySignal]:
        """
        One CLOSE_GROUP signal per open group (see `close_positions`), tagged
        `square_off`. The signals are NOT applied.
        """
        return self.close_positions(self.open_keys(), price_lookup, t, reason, strategy_name,
                                    metadata={"square_off": True})

    # --- reporting ------------------------------------------------------------

    def snapshot(self, t: str) -> Dict[str, Any]:
        """
        Equity point in the shape of POST /runs/{id}/equity-snapshots items.
        `charges` is cumulative so the API nets it into equity the same way
        `total_pnl()` does — the curve then agrees with the SL/target rule.
        """
        return {
            "snapshotUtc": t,
            "realizedPnl": round(self.realized_pnl(), 4),
            "unrealizedPnl": round(self.unrealized_pnl(), 4),
            "charges": round(self.charges, 4),
            "usedCapital": round(self.used_capital(), 4),
            "openPositions": len(self._open),
            "closedPositions": len(self.closed),
        }
