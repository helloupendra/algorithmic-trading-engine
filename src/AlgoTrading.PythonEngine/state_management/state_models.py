from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional


@dataclass
class ActiveLeg:
    symbol: str
    side: str
    quantity: int
    entry_price: Optional[float] = None
    strike: Optional[int] = None
    option_type: Optional[str] = None
    expiry_date: Optional[str] = None
    status: str = "Open"

    @staticmethod
    def from_dict(data: Dict[str, Any]) -> "ActiveLeg":
        return ActiveLeg(
            symbol=str(data.get("symbol", "")),
            side=str(data.get("side", "")),
            quantity=int(data.get("quantity", 0)),
            entry_price=float(data["entry_price"]) if data.get("entry_price") is not None else None,
            strike=int(data["strike"]) if data.get("strike") is not None else None,
            option_type=data.get("option_type"),
            expiry_date=data.get("expiry_date"),
            status=str(data.get("status", "Open")),
        )

    def to_dict(self) -> Dict[str, Any]:
        return {
            "symbol": self.symbol,
            "side": self.side,
            "quantity": self.quantity,
            "entry_price": self.entry_price,
            "strike": self.strike,
            "option_type": self.option_type,
            "expiry_date": self.expiry_date,
            "status": self.status,
        }


@dataclass
class StrategyState:
    simulation_run_id: int
    strategy_name: str
    mode: str
    exchange: str
    underlying: str

    current_group_id: Optional[str] = None
    last_trade_strike: Optional[int] = None
    atm_strike: Optional[int] = None
    active_expiry_date: Optional[str] = None
    last_underlying_price: Optional[float] = None

    ce_list: List[int] = field(default_factory=list)
    pe_list: List[int] = field(default_factory=list)
    straddle_list: List[int] = field(default_factory=list)

    active_legs: List[ActiveLeg] = field(default_factory=list)

    signal_count: int = 0

    # Recovery cursor
    last_processed_stream_id: Optional[str] = None
    last_processed_tick_time_utc: Optional[str] = None

    # bookkeeping
    version: int = 0
    last_updated_utc: Optional[str] = None
    heartbeat_utc: Optional[str] = None
    
    # Generic state variables for any strategy
    strategy_data: Dict[str, Any] = field(default_factory=dict)

    @staticmethod
    def from_dict(data: Dict[str, Any]) -> "StrategyState":
        active_legs = [
            ActiveLeg.from_dict(x) for x in data.get("active_legs", [])
        ]

        return StrategyState(
            simulation_run_id=int(data["simulation_run_id"]),
            strategy_name=str(data.get("strategy_name", "")),
            mode=str(data.get("mode", "")),
            exchange=str(data.get("exchange", "")),
            underlying=str(data.get("underlying", "")),
            current_group_id=data.get("current_group_id"),
            last_trade_strike=int(data["last_trade_strike"]) if data.get("last_trade_strike") is not None else None,
            atm_strike=int(data["atm_strike"]) if data.get("atm_strike") is not None else None,
            active_expiry_date=data.get("active_expiry_date"),
            last_underlying_price=float(data["last_underlying_price"]) if data.get("last_underlying_price") is not None else None,
            ce_list=[int(x) for x in data.get("ce_list", [])],
            pe_list=[int(x) for x in data.get("pe_list", [])],
            straddle_list=[int(x) for x in data.get("straddle_list", [])],
            active_legs=active_legs,
            signal_count=int(data.get("signal_count", 0)),
            last_processed_stream_id=data.get("last_processed_stream_id"),
            last_processed_tick_time_utc=data.get("last_processed_tick_time_utc"),
            version=int(data.get("version", 0)),
            last_updated_utc=data.get("last_updated_utc"),
            heartbeat_utc=data.get("heartbeat_utc"),
            strategy_data=data.get("strategy_data", {}),
        )

    def to_dict(self) -> Dict[str, Any]:
        return {
            "simulation_run_id": self.simulation_run_id,
            "strategy_name": self.strategy_name,
            "mode": self.mode,
            "exchange": self.exchange,
            "underlying": self.underlying,
            "current_group_id": self.current_group_id,
            "last_trade_strike": self.last_trade_strike,
            "atm_strike": self.atm_strike,
            "active_expiry_date": self.active_expiry_date,
            "last_underlying_price": self.last_underlying_price,
            "ce_list": self.ce_list,
            "pe_list": self.pe_list,
            "straddle_list": self.straddle_list,
            "active_legs": [x.to_dict() for x in self.active_legs],
            "signal_count": self.signal_count,
            "last_processed_stream_id": self.last_processed_stream_id,
            "last_processed_tick_time_utc": self.last_processed_tick_time_utc,
            "version": self.version,
            "last_updated_utc": self.last_updated_utc,
            "heartbeat_utc": self.heartbeat_utc,
            "strategy_data": self.strategy_data,
        }
