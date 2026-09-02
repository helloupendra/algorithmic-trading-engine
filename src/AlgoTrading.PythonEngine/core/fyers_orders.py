import os
import time
import threading
from typing import Optional, Dict, Any, List
from fyers_apiv3 import fyersModel
from core.api_client import build_session, VERIFY_SSL
from core.config import API_BASE_URL, require_app_id

_fyers_instance: Optional[fyersModel.FyersModel] = None

def get_active_session_token() -> str:
    """Fetch the active broker session token from the .NET API."""
    http = build_session()
    url = f"{API_BASE_URL}/api/auth/session"
    response = http.get(url, verify=VERIFY_SSL, timeout=10)
    response.raise_for_status()
    data = response.json()
    if data.get("isAuthenticated") and data.get("accessToken"):
        return data["accessToken"]
    raise RuntimeError("Fyers is not authenticated in the .NET API.")

def get_fyers_client() -> fyersModel.FyersModel:
    global _fyers_instance
    if _fyers_instance is None:
        client_id = require_app_id()
        access_token = get_active_session_token()
        _fyers_instance = fyersModel.FyersModel(client_id=client_id, token=access_token, is_async=False, log_path="")
    return _fyers_instance

def place_market_entry(symbol: str, qty: int, side: int = 1) -> float:
    """
    Place a market entry order and return the average traded price.
    side: 1 for Buy, -1 for Sell.
    """
    fyers = get_fyers_client()
    data = {
        "symbol": symbol,
        "qty": qty,
        "type": 2, # Market
        "side": side,
        "productType": "INTRADAY",
        "limitPrice": 0,
        "stopPrice": 0,
        "validity": "DAY",
        "disclosedQty": 0,
        "offlineOrder": False
    }
    
    response = fyers.place_order(data=data)
    if not response or response.get("s") != "ok":
        raise RuntimeError(f"Failed to place entry order for {symbol}: {response}")
    
    order_id = response.get("id")
    print(f"Entry order placed successfully. Order ID: {order_id}")
    
    # Wait briefly for execution and fetch order book to get traded price
    time.sleep(1.5)
    
    orders_resp = fyers.orderbook()
    if orders_resp and orders_resp.get("s") == "ok":
        for order in orders_resp.get("orderBook", []):
            if str(order.get("id")) == str(order_id):
                if order.get("status") == 2: # Traded
                    traded_price = order.get("tradedPrice")
                    print(f"Order {order_id} filled at {traded_price}")
                    return float(traded_price)
                else:
                    print(f"Warning: Order {order_id} status is {order.get('status')}")
                    traded = order.get("tradedPrice", 0)
                    if traded > 0:
                        return float(traded)
                        
    print("Could not retrieve exact traded price from orderbook immediately.")
    return 0.0

class SyntheticOCOManager:
    def __init__(self):
        self.active_pairs = []
        self.lock = threading.Lock()
        self.running = True
        self.thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self.thread.start()

    def add_oco(self, symbol: str, target_order_id: str, sl_order_id: str):
        with self.lock:
            self.active_pairs.append({
                "symbol": symbol,
                "target_id": target_order_id,
                "sl_id": sl_order_id
            })

    def _monitor_loop(self):
        while self.running:
            time.sleep(2)
            with self.lock:
                if not self.active_pairs:
                    continue
                pairs_to_check = list(self.active_pairs)
            
            try:
                fyers = get_fyers_client()
                orders_resp = fyers.orderbook()
                if not orders_resp or orders_resp.get("s") != "ok":
                    continue
                    
                order_book = {str(o.get("id")): o for o in orders_resp.get("orderBook", [])}
                
                pairs_to_remove = []
                for pair in pairs_to_check:
                    tid = str(pair["target_id"])
                    sid = str(pair["sl_id"])
                    
                    t_order = order_book.get(tid)
                    s_order = order_book.get(sid)
                    
                    if not t_order or not s_order:
                        continue
                        
                    # Status 2 = Traded, Status 6 = Cancelled, Status 5 = Rejected
                    terminal_statuses = [2, 5, 6]
                    
                    t_status = t_order.get("status")
                    s_status = s_order.get("status")
                    
                    if t_status == 2: # Target Hit
                        print(f"Target Hit! Cancelling SL order {sid}")
                        fyers.cancel_order(data={"id": sid})
                        pairs_to_remove.append(pair)
                    elif s_status == 2: # SL Hit
                        print(f"StopLoss Hit! Cancelling Target order {tid}")
                        fyers.cancel_order(data={"id": tid})
                        pairs_to_remove.append(pair)
                    elif t_status in [5, 6] and s_status not in terminal_statuses:
                        print(f"Target order cancelled/rejected. Cancelling SL {sid}")
                        fyers.cancel_order(data={"id": sid})
                        pairs_to_remove.append(pair)
                    elif s_status in [5, 6] and t_status not in terminal_statuses:
                        print(f"SL order cancelled/rejected. Cancelling Target {tid}")
                        fyers.cancel_order(data={"id": tid})
                        pairs_to_remove.append(pair)
                    elif t_status in terminal_statuses and s_status in terminal_statuses:
                        pairs_to_remove.append(pair)

                with self.lock:
                    for p in pairs_to_remove:
                        if p in self.active_pairs:
                            self.active_pairs.remove(p)
                            
            except Exception as e:
                print(f"Error in OCO monitor loop: {e}")

_oco_manager = SyntheticOCOManager()

def place_synthetic_oco(symbol: str, qty: int, side: int, target_price: float, sl_price: float):
    """
    Places two exit orders and tracks them.
    side: The side to close the position (e.g. -1 to close a long).
    """
    fyers = get_fyers_client()
    
    t_data = {
        "symbol": symbol,
        "qty": qty,
        "type": 1, # Limit
        "side": side,
        "productType": "INTRADAY",
        "limitPrice": round(target_price, 2),
        "stopPrice": 0,
        "validity": "DAY",
        "disclosedQty": 0,
        "offlineOrder": False
    }
    
    t_resp = fyers.place_order(data=t_data)
    if not t_resp or t_resp.get("s") != "ok":
        print(f"Failed to place Target order: {t_resp}")
        return
        
    t_id = str(t_resp.get("id"))
    print(f"Placed Target Order: {t_id} at {target_price}")
    
    s_data = {
        "symbol": symbol,
        "qty": qty,
        "type": 3, # Stop Market
        "side": side,
        "productType": "INTRADAY",
        "limitPrice": 0,
        "stopPrice": round(sl_price, 2),
        "validity": "DAY",
        "disclosedQty": 0,
        "offlineOrder": False
    }
    
    s_resp = fyers.place_order(data=s_data)
    if not s_resp or s_resp.get("s") != "ok":
        print(f"Failed to place SL order: {s_resp}. Attempting to cancel Target.")
        fyers.cancel_order(data={"id": t_id})
        return
        
    s_id = str(s_resp.get("id"))
    print(f"Placed SL Order: {s_id} at {sl_price}")
    
    _oco_manager.add_oco(symbol, t_id, s_id)
