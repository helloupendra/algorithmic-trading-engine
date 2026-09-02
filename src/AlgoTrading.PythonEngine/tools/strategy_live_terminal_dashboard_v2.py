from __future__ import annotations

import json, math, os, sys, time, argparse
from collections import Counter
from datetime import datetime
from typing import Any, Dict, List, Optional

import requests, urllib3
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL
from core.api_client import build_session

VERIFY_SSL = False
REFRESH_SECONDS = 2
MAX_SIGNALS_TO_SHOW = 12
MAX_ORDERS_TO_SHOW = 12
MAX_POSITIONS_TO_SHOW = 12
USE_ANSI = True
UNDERLYING = "BANKNIFTY"
SPOT_SYMBOL = "NSE:NIFTYBANK-INDEX"
STRIKE_STEP = 100

if USE_ANSI:
    GREEN = "\033[92m"; RED = "\033[91m"; YELLOW = "\033[93m"; CYAN = "\033[96m"
    BLUE = "\033[94m"; MAGENTA = "\033[95m"; BOLD = "\033[1m"; RESET = "\033[0m"
else:
    GREEN = RED = YELLOW = CYAN = BLUE = MAGENTA = BOLD = RESET = ""

SEPARATOR = "─" * 140

class ApiClient:
    def __init__(self, base_url: str, verify_ssl: bool = False):
        self.base_url = base_url.rstrip("/")
        self.verify_ssl = verify_ssl
        self.http = build_session()

    def _get(self, path: str, params: Optional[Dict[str, Any]] = None):
        r = self.http.get(f"{self.base_url}{path}", params=params, verify=self.verify_ssl, timeout=30)
        r.raise_for_status()
        return r.json()

    def _post(self, path: str, payload: Optional[Dict[str, Any]] = None):
        r = self.http.post(f"{self.base_url}{path}", json=payload, verify=self.verify_ssl, timeout=30)
        r.raise_for_status()
        return r.json()

    def refresh_portfolio(self, run_id: int): return self._post(f"/api/Simulator/runs/{run_id}/portfolio/refresh")
    def get_orders(self, run_id: int): return self._get(f"/api/Simulator/runs/{run_id}/orders")
    def get_positions(self, run_id: int): return self._get(f"/api/Simulator/runs/{run_id}/positions")
    def get_signals(self, run_id: int): return self._get(f"/api/Simulator/runs/{run_id}/signals")
    def get_performance(self, run_id: int): return self._get(f"/api/Simulator/runs/{run_id}/performance")
    def get_equity_curve(self, run_id: int): return self._get(f"/api/Simulator/runs/{run_id}/equity-curve")
    def get_runs(self, user_id: Optional[int] = None): 
        params = {"userId": user_id} if user_id else None
        return self._get("/api/Simulator/runs", params=params)
    def get_latest_quote(self, symbol: str): return self._get("/api/LiveData/latest", params={"symbol": symbol})

def clear_screen(): os.system("cls" if os.name == "nt" else "clear")

def safe_float(v: Any, default: float = 0.0) -> float:
    try: return default if v is None else float(v)
    except Exception: return default

def round_to_step(price: float, step: int = 100) -> int: return int(math.ceil(price / step) * step)

def fmt_num(value: Any, decimals: int = 2) -> str:
    if value is None: return "-"
    try: return f"{float(value):,.{decimals}f}"
    except Exception: return str(value)

def fmt_money(value: Any) -> str:
    if value is None: return "-"
    try:
        num = float(value)
        color = GREEN if num > 0 else RED if num < 0 else RESET
        return f"{color}{num:,.2f}{RESET}"
    except Exception: return str(value)

def fmt_dt(value: Any) -> str:
    if not value: return "-"
    try:
        dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        return dt.strftime("%Y-%m-%d %H:%M:%S")
    except Exception: return str(value)

def trim(s: Any, n: int) -> str:
    text = "" if s is None else str(s)
    return text if len(text) <= n else text[: n - 1] + "…"

def signal_color(s: str) -> str:
    s = (s or "").upper()
    if "OPEN" in s or "BUY" in s: return GREEN
    if "CLOSE" in s or "EXIT" in s or "SELL" in s: return YELLOW
    if "SHIFT" in s or "ADJUST" in s: return CYAN
    return RESET

def side_text(s: str) -> str:
    s = (s or "").upper()
    return f"{GREEN}BUY{RESET}" if s == "BUY" else f"{RED}SELL{RESET}" if s == "SELL" else s

def direction_text(d: str) -> str:
    d = (d or "").upper()
    return f"{GREEN}LONG{RESET}" if d == "LONG" else f"{RED}SHORT{RESET}" if d == "SHORT" else d

def status_text(s: str) -> str:
    s = (s or "").upper()
    if s in {"OPEN", "FILLED"}: return f"{GREEN}{s}{RESET}"
    if s == "CLOSED": return f"{YELLOW}{s}{RESET}"
    if s in {"FAILED", "CANCELLED"}: return f"{RED}{s}{RESET}"
    return s

def summarize_orders(orders: List[Dict[str, Any]]) -> str:
    cnt = Counter((o.get("status", "?"), o.get("side", "?")) for o in orders)
    return ", ".join([f"{a}/{b}:{n}" for (a,b), n in sorted(cnt.items())]) if cnt else "None"

def compact_json(text: Any, max_len: int = 90) -> str:
    if text is None: return ""
    if not isinstance(text, str):
        try: text = json.dumps(text)
        except Exception: text = str(text)
    return trim(text.replace("\n", " ").replace("  ", " "), max_len)

def sparkline(values: List[float], width: int = 56) -> str:
    if not values: return "(no equity snapshots)"
    chars = "▁▂▃▄▅▆▇█"
    sampled = values
    if len(values) > width:
        step = len(values) / width
        sampled = [values[int(i * step)] for i in range(width)]
    mn, mx = min(sampled), max(sampled)
    if mx == mn: return chars[0] * len(sampled)
    return "".join(chars[int((v - mn) / (mx - mn) * (len(chars) - 1))] for v in sampled)

def latest_open_group(portfolio: Dict[str, Any]) -> Optional[Dict[str, Any]]:
    groups = portfolio.get("groups") or []
    open_groups = [g for g in groups if str(g.get("status", "")).upper() == "OPEN"]
    if not open_groups: return None
    open_groups.sort(key=lambda g: (int(g.get("openPositionCount", 0)), str(g.get("groupId", ""))), reverse=True)
    return open_groups[0]

def current_group_legs(positions: List[Dict[str, Any]], group_id: str) -> List[Dict[str, Any]]:
    return [p for p in positions if p.get("groupId") == group_id and str(p.get("status", "")).upper() == "OPEN"]

def print_header(run_id: int):
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    print(f"{BOLD}{CYAN}ALGO TRADING FULL-SCREEN LIVE TERMINAL{RESET}")
    print(f"RunId: {run_id}   Local Time: {now}")
    print(SEPARATOR)

def print_top_market_banner(spot_quote: Optional[Dict[str, Any]], portfolio: Dict[str, Any], positions: List[Dict[str, Any]]):
    print(f"{BOLD}{MAGENTA}LIVE UNDERLYING + CURRENT ACTIVE GROUP{RESET}")
    if spot_quote:
        ltp = safe_float(spot_quote.get("lastTradedPrice"))
        atm = round_to_step(ltp, STRIKE_STEP)
        updated = fmt_dt(spot_quote.get("updatedUtc"))
    else:
        ltp = 0.0; atm = 0; updated = "-"
    group = latest_open_group(portfolio)
    if group:
        group_id = str(group.get("groupId", "-"))
        group_status = status_text(group.get("status", "-"))
        group_realized = fmt_money(group.get("realizedPnl"))
        group_unrealized = fmt_money(group.get("unrealizedPnl"))
        legs = current_group_legs(positions, group_id)
        legs_text = ", ".join([f"{direction_text(p.get('direction','-'))} {trim(p.get('symbol','-'), 26)} @ {fmt_num(p.get('averagePrice'))}" for p in legs[:4]]) or "No open legs"
    else:
        group_id = "No open group"; group_status = "-"; group_realized = "-"; group_unrealized = "-"; legs_text = "No open legs"
    print(f"Underlying       : {UNDERLYING}")
    print(f"Spot Symbol      : {SPOT_SYMBOL}")
    print(f"Live Spot Price  : {fmt_money(ltp)}")
    print(f"Current ATM      : {BOLD}{CYAN}{atm if atm else '-'}{RESET}")
    print(f"Spot Updated     : {updated}")
    print(f"Current GroupId  : {trim(group_id, 60)}")
    print(f"Group Status     : {group_status}")
    print(f"Group Realized   : {group_realized} | Group Unrealized: {group_unrealized}")
    print(f"Open Legs        : {legs_text}")
    print(SEPARATOR)

def print_portfolio(portfolio: Dict[str, Any], perf: Optional[Dict[str, Any]], equity_curve: List[Dict[str, Any]]):
    print(f"{BOLD}{BLUE}PORTFOLIO SUMMARY{RESET}")
    print(f"Strategy         : {portfolio.get('strategyName', '-')}")
    print(f"Run Status       : {status_text(portfolio.get('runStatus', '-'))}")
    print(f"Initial Capital  : {fmt_money(portfolio.get('initialCapital'))}")
    print(f"Used Capital     : {fmt_money(portfolio.get('usedCapital'))}")
    print(f"Available Capital: {fmt_money(portfolio.get('availableCapital'))}")
    print(f"Realized PnL     : {fmt_money(portfolio.get('realizedPnl'))}")
    print(f"Unrealized PnL   : {fmt_money(portfolio.get('unrealizedPnl'))}")
    print(f"Total PnL        : {fmt_money(portfolio.get('totalPnl'))}")
    print(f"Current Equity   : {fmt_money(portfolio.get('currentEquity'))}")
    print(f"Return %         : {fmt_num(portfolio.get('returnPercent'))}%")
    print(f"Orders           : total={portfolio.get('totalOrders', 0)} | filled={portfolio.get('filledOrders', 0)}")
    print(f"Positions        : open={portfolio.get('openPositions', 0)} | closed={portfolio.get('closedPositions', 0)}")
    if perf:
        print(f"Win Rate         : {fmt_num(perf.get('winRatePercent'))}%")
        print(f"Max Drawdown     : {fmt_num(perf.get('maxDrawdownPercent'))}%")
        print(f"Profit Factor    : {fmt_num(perf.get('profitFactor'))}")
        print(f"Expectancy       : {fmt_money(perf.get('expectancy'))}")
        print(f"Avg Win / Loss   : {fmt_money(perf.get('averageWin'))} / {fmt_money(-safe_float(perf.get('averageLoss')))}")
    eq_vals = [safe_float(x.get('currentEquity')) for x in equity_curve]
    print(f"Equity Curve     : {sparkline(eq_vals)}" if eq_vals else "Equity Curve     : (no snapshots yet)")
    print(SEPARATOR)

def get_strategy_state(run_id: int) -> Dict[str, Any]:
    try:
        import redis
        from state_management.state_store import StrategyStateStore
        rc = redis.Redis(host="localhost", port=6379, db=0, decode_responses=True)
        store = StrategyStateStore(rc, run_id)
        loaded = store.load()
        if loaded:
            return loaded.strategy_data or {}
    except Exception:
        pass
    return {}

def print_strategy_info(strategy_name: str, run_id: int):
    if strategy_name == "GhostTangentCrossings":
        state = get_strategy_state(run_id)
        buy_trig = state.get("target_buy_trigger")
        sell_trig = state.get("target_sell_trigger")
        
        buy_str = fmt_num(buy_trig) if buy_trig else "Waiting for pivot..."
        sell_str = fmt_num(sell_trig) if sell_trig else "Waiting for pivot..."
        
        print(f"{BOLD}{YELLOW}STRATEGY INSIGHT: Ghost Tangent Crossings{RESET}")
        print("Execution Trigger: As soon as the live BankNifty spot price crosses and closes beyond")
        print("one of these active tangent lines, the strategy will instantly generate a BUY or SELL signal.")
        print(f"Live Triggers -> Upper Breakout (Buy CE) > {buy_str} | Lower Breakout (Buy PE) < {sell_str}")
        print(SEPARATOR)

def print_group_summary(portfolio: Dict[str, Any]):
    groups = portfolio.get("groups") or []
    print(f"{BOLD}{BLUE}GROUP SUMMARY{RESET}")
    if not groups:
        print("No groups yet.")
        print(SEPARATOR)
        return
    header = f"{'GroupId':30} {'Status':8} {'Open':>4} {'Closed':>6} {'UsedCap':>14} {'Realized':>14} {'Unrealized':>14}"
    print(header); print("-" * len(header))
    for g in groups:
        print(f"{trim(g.get('groupId','-'),30):30} {status_text(g.get('status','-')):8} {int(g.get('openPositionCount',0)):>4} {int(g.get('closedPositionCount',0)):>6} {fmt_num(g.get('usedCapital')).rjust(14)} {fmt_num(g.get('realizedPnl')).rjust(14)} {fmt_num(g.get('unrealizedPnl')).rjust(14)}")
    print(SEPARATOR)

def print_signals(signals: List[Dict[str, Any]]):
    print(f"{BOLD}{BLUE}RECENT SIGNALS{RESET}")
    if not signals:
        print("No signals.")
        print(SEPARATOR)
        return
    rows = signals[-MAX_SIGNALS_TO_SHOW:]
    header = f"{'Time':19} {'Type':16} {'Strategy':10} {'GroupId':28} {'Metadata':60}"
    print(header); print("-" * len(header))
    for s in rows:
        st = str(s.get("signalType", "-")); color = signal_color(st)
        print(f"{fmt_dt(s.get('timestampUtc')):19} {color}{trim(st,16):16}{RESET} {trim(s.get('strategyName','-'),10):10} {trim(s.get('groupId','-'),28):28} {compact_json(s.get('metadataJson',''),60):60}")
    print(SEPARATOR)

def print_positions(positions: List[Dict[str, Any]]):
    print(f"{BOLD}{BLUE}POSITIONS{RESET}")
    if not positions:
        print("No positions.")
        print(SEPARATOR)
        return
    rows = sorted(positions, key=lambda x: (x.get('status') != "Open", x.get('groupId', ""), x.get('symbol', "")))[:MAX_POSITIONS_TO_SHOW]
    header = f"{'Status':8} {'Dir':8} {'Qty':>4} {'Symbol':32} {'Avg':>10} {'Mark':>10} {'Realized':>12} {'Unrealized':>12} {'GroupId':20}"
    print(header); print("-" * len(header))
    for p in rows:
        print(f"{status_text(p.get('status','-')):8} {direction_text(p.get('direction','-')):8} {int(p.get('quantity',0)):>4} {trim(p.get('symbol','-'),32):32} {fmt_num(p.get('averagePrice')).rjust(10)} {fmt_num(p.get('lastMarkPrice')).rjust(10)} {fmt_num(p.get('realizedPnl')).rjust(12)} {fmt_num(p.get('unrealizedPnl')).rjust(12)} {trim(p.get('groupId','-'),20):20}")
    if len(positions) > MAX_POSITIONS_TO_SHOW: print(f"... showing first {MAX_POSITIONS_TO_SHOW} of {len(positions)} positions")
    print(SEPARATOR)

def print_orders(orders: List[Dict[str, Any]]):
    print(f"{BOLD}{BLUE}RECENT ORDERS{RESET}")
    if not orders:
        print("No orders.")
        print(SEPARATOR)
        return
    print(f"Summary: {summarize_orders(orders)}")
    rows = orders[:MAX_ORDERS_TO_SHOW]
    header = f"{'Created':19} {'Status':8} {'Side':8} {'Qty':>4} {'Symbol':32} {'ReqPx':>10} {'FillPx':>10} {'GroupId':20}"
    print(header); print("-" * len(header))
    for o in rows:
        print(f"{fmt_dt(o.get('createdUtc')):19} {status_text(o.get('status','-')):8} {side_text(o.get('side','-')):8} {int(o.get('quantity',0)):>4} {trim(o.get('symbol','-'),32):32} {fmt_num(o.get('requestedPrice')).rjust(10)} {fmt_num(o.get('fillPrice')).rjust(10)} {trim(o.get('groupId','-'),20):20}")
    if len(orders) > MAX_ORDERS_TO_SHOW: print(f"... showing latest {MAX_ORDERS_TO_SHOW} of {len(orders)} orders")
    print(SEPARATOR)

def main() -> int:
    parser = argparse.ArgumentParser(description="Live Terminal Dashboard for a specific user or run.")
    parser.add_argument("--user-id", type=int, help="Automatically track the latest run for this User ID.")
    parser.add_argument("--run-id", type=int, help="Directly track a specific SimulationRunId.")
    args = parser.parse_args()

    if not args.user_id and not args.run_id:
        print(f"{RED}Error: You must provide either --user-id or --run-id{RESET}")
        return 1

    client = ApiClient(API_BASE_URL, VERIFY_SSL)
    current_run_id = args.run_id

    while True:
        try:
            # If we don't have a specific run_id, fetch the latest one for the user
            if not current_run_id and args.user_id:
                runs = client.get_runs(args.user_id)
                if runs:
                    # Find the first one that is "Running" or "Pending", otherwise just grab the newest
                    active_runs = [r for r in runs if r.get("status") in ("Running", "Pending", "LivePaper")]
                    if active_runs:
                        current_run_id = active_runs[0].get("id")
                    else:
                        current_run_id = runs[0].get("id")
                else:
                    clear_screen()
                    print(f"{YELLOW}Waiting for User {args.user_id} to start a strategy run...{RESET}")
                    time.sleep(REFRESH_SECONDS)
                    continue

            portfolio = client.refresh_portfolio(current_run_id)
            signals = client.get_signals(current_run_id)
            orders = client.get_orders(current_run_id)
            positions = client.get_positions(current_run_id)
            try: perf = client.get_performance(current_run_id)
            except Exception: perf = None
            try: equity_curve = client.get_equity_curve(current_run_id)
            except Exception: equity_curve = []
            try: spot_quote = client.get_latest_quote(SPOT_SYMBOL)
            except Exception: spot_quote = None
            clear_screen()
            print_header(current_run_id)
            print_top_market_banner(spot_quote, portfolio, positions)
            print_portfolio(portfolio, perf, equity_curve)
            print_strategy_info(portfolio.get('strategyName', ''), current_run_id)
            print_group_summary(portfolio)
            print_signals(signals)
            print_positions(positions)
            print_orders(orders)
            print(f"Refresh every {REFRESH_SECONDS}s | Press Ctrl+C to stop")
        except KeyboardInterrupt:
            print("\nStopped by user.")
            return 0
        except Exception as ex:
            clear_screen(); 
            if current_run_id:
                print_header(current_run_id)
            print(f"{RED}{BOLD}ERROR{RESET}: {ex}")
            print("Check these things:")
            print("1) API is running")
            print(f"2) Run ID {current_run_id} exists")
            print(f"3) /api/Simulator/runs/{current_run_id}/portfolio/refresh is working")
            print("4) /signals /orders /positions /performance /equity-curve endpoints are available")
            print(f"5) {SPOT_SYMBOL} exists in /api/LiveData/latest")
        time.sleep(REFRESH_SECONDS)

if __name__ == "__main__":
    sys.exit(main())
