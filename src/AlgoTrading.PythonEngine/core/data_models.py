from pydantic import BaseModel, Field
from typing import List, Dict, Optional, Tuple
from datetime import datetime, date

class QuoteNode(BaseModel):
    price: float
    quantity: int
    orders: int

class TickData(BaseModel):
    symbol: str
    market_type: str  # e.g., 'EQUITY', 'FNO', 'COMMODITY'
    timestamp: datetime
    last_traded_price: float
    last_traded_qty: int
    average_trade_price: float
    volume: int
    open_interest: Optional[int] = None
    
    # Order Book Depth (Bid/Ask)
    bids: List[QuoteNode] = Field(default_factory=list)
    asks: List[QuoteNode] = Field(default_factory=list)

class BarData(BaseModel):
    symbol: str
    resolution: str  # e.g., '1m', '5m', '1D'
    timestamp_start: datetime
    open: float
    high: float
    low: float
    close: float
    volume: int
    open_interest: Optional[int] = None

class OptionGreeks(BaseModel):
    iv: float
    delta: float
    gamma: float
    theta: float
    vega: float
    rho: float

class OptionContractLive(BaseModel):
    symbol: str           # e.g., NSE:BANKNIFTY26SEP57200CE
    strike: float
    option_type: str      # 'CE' or 'PE'
    expiry: date
    underlying_price: float
    
    # Live Data
    tick: TickData
    greeks: Optional[OptionGreeks] = None

class OptionChainSnapshot(BaseModel):
    underlying_symbol: str
    timestamp: datetime
    contracts: Dict[str, OptionContractLive] # Mapped by symbol
