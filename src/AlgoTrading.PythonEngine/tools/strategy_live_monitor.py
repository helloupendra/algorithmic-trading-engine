from __future__ import annotations

import json
import os
import sys
import time
from collections import Counter
from datetime import datetime
from typing import Any, Dict, List

import requests
import urllib3

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

# -----------------------------------------------------------------------------
# CONFIG
# -----------------------------------------------------------------------------
import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL
VERIFY_SSL = False
SIMULATION_RUN_ID = 1          # <- change this to your run id
REFRESH_SECONDS = 2            # how often to refresh terminal
MAX_ORDERS_TO_SHOW = 10
MAX_POSITIONS_TO_SHOW = 10

# Optional: enable ANSI colors in most modern Windows terminals
GREEN = "\033[92m"
RED = "\033[91m"
YELLOW = "\033[93m"
CYAN = "\033[96m"
BOLD = "\033[1m"
RESET = "\033[0m"


class ApiClient:
    def __init__(self, base_url: str, verify_ssl: bool = False):
        self.base_url = base_url.rstrip("/")
        self.verify_ssl = verify_ssl
        self.http = requests.Session()

    def _get(self, path: str):
        resp = self.http.get(f"{self.base_url}{path}", verify=self.verify_ssl, timeout=30)
        resp.raise_for_status()
        return resp.json()

    def _post(self, path: str, payload: Dict[str, Any] | None = None):
        resp = self.http.post(
            f"{self.base_url}{path}",
            json=payload,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def refresh_portfolio(self, run_id: int) -> Dict[str, Any]:
        return self._post(f"/api/Simulator/runs/{run_id}/portfolio/refresh")

    def get_orders(self, run_id: int) -> List[Dict[str, Any]]:
        return self._get(f"/api/Simulator/runs/{run_id}/orders")

    def get_positions(self, run_id: int) -> List[Dict[str, Any]]:
        return self._get(f"/api/Simulator/runs/{run_id}/positions")

    def get_signals(self, run_id: int) -> List[Dict[str, Any]]:
        return self._get(f"/api/Simulator/runs/{run_id}/signals")

    def get_portfolio(self, run_id: int) -> Dict[str, Any]:
        return self._get(f"/api/Simulator/runs/{run_id}/portfolio")

    def get_performance(self, run_id: int) -> Dict[str, Any]:
        return self._get(f"/api/Simulator/runs/{run_id}/performance")


def clear_screen() -> None:
    os.system("cls" if os.name == "nt" else "clear")


def fmt_money(value: Any) -> str:
    if value is None:
        return "-"
    try:
        num = float(value)
        color = GREEN if num > 0 else RED if num < 0 else RESET
        return f"{color}{num:,.2f}{RESET}"
    except Exception:
        return str(value)


def fmt_num(value: Any) -> str:
    if value is None:
        return "-"
    try:
        return f"{float(value):,.2f}"
    except Exception:
        return str(value)


def fmt_dt(value: Any) -> str:
    if not value:
        return "-"
    try:
        # handles ISO strings; leaves timezone suffix intact if present
        dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        return dt.strftime("%Y-%m-%d %H:%M:%S")
    except Exception:
        return str(value)


def side_arrow(side: str) -> str:
    s = (side or "").upper()
    return "↑ BUY" if s == "BUY" else "↓ SELL" if s == "SELL" else s


def summarize_orders(orders: List[Dict[str, Any]]) -> str:
    cnt = Counter((o.get("status", "?"), o.get("side", "?")) for o in orders)
    chunks = []
    for (status, side), n in sorted(cnt.items()):
        chunks.append(f"{status}/{side}:{n}")
    return ", ".join(chunks) if chunks else "None"


def print_header(run_id: int) -> None:
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    print(f"{BOLD}{CYAN}Strategy Live Monitor{RESET}  |  RunId: {run_id}  |  Local Time: {now}")
    print("=" * 110)


def print_portfolio(portfolio: Dict[str, Any], perf: Dict[str, Any] | None = None) -> None:
    print(f"{BOLD}PORTFOLIO SUMMARY{RESET}")
    print(f"Strategy        : {portfolio.get('strategyName', '-')}")
    print(f"Run Status      : {portfolio.get('runStatus', '-')}")
    print(f"Initial Capital : {fmt_money(portfolio.get('initialCapital'))}")
    print(f"Used Capital    : {fmt_money(portfolio.get('usedCapital'))}")
    print(f"Available Cap   : {fmt_money(portfolio.get('availableCapital'))}")
    print(f"Realized PnL    : {fmt_money(portfolio.get('realizedPnl'))}")
    print(f"Unrealized PnL  : {fmt_money(portfolio.get('unrealizedPnl'))}")
    print(f"Total PnL       : {fmt_money(portfolio.get('totalPnl'))}")
    print(f"Current Equity  : {fmt_money(portfolio.get('currentEquity'))}")
    print(f"Return %        : {fmt_num(portfolio.get('returnPercent'))}%")
    print(f"Orders          : total={portfolio.get('totalOrders', 0)} | filled={portfolio.get('filledOrders', 0)}")
    print(f"Positions       : open={portfolio.get('openPositions', 0)} | closed={portfolio.get('closedPositions', 0)}")
    if perf:
        print(f"Win Rate        : {fmt_num(perf.get('winRatePercent'))}%")
        print(f"Max Drawdown    : {fmt_num(perf.get('maxDrawdownPercent'))}%")
        print(f"Profit Factor   : {fmt_num(perf.get('profitFactor'))}")
        print(f"Expectancy      : {fmt_money(perf.get('expectancy'))}")
    print("-" * 110)



def print_group_summaries(portfolio: Dict[str, Any]) -> None:
    groups = portfolio.get("groups") or []
    print(f"{BOLD}GROUP SUMMARY{RESET}")
    if not groups:
        print("No groups yet.")
        print("-" * 110)
        return

    print(f"{'GroupId':32} {'Status':8} {'Open':>4} {'Closed':>6} {'UsedCap':>14} {'Realized':>14} {'Unrealized':>14}")
    for g in groups:
        print(
            f"{str(g.get('groupId','-'))[:32]:32} "
            f"{str(g.get('status','-')):8} "
            f"{int(g.get('openPositionCount',0)):>4} "
            f"{int(g.get('closedPositionCount',0)):>6} "
            f"{fmt_num(g.get('usedCapital')).rjust(14)} "
            f"{fmt_num(g.get('realizedPnl')).rjust(14)} "
            f"{fmt_num(g.get('unrealizedPnl')).rjust(14)}"
        )
    print("-" * 110)



def print_positions(positions: List[Dict[str, Any]]) -> None:
    print(f"{BOLD}CURRENT POSITIONS{RESET}")
    if not positions:
        print("No positions.")
        print("-" * 110)
        return

    positions_sorted = sorted(
        positions,
        key=lambda x: (x.get("status") != "Open", x.get("groupId", ""), x.get("symbol", ""))
    )[:MAX_POSITIONS_TO_SHOW]

    print(
        f"{'Status':8} {'Direction':8} {'Qty':>4} {'Symbol':32} {'Avg':>10} {'Mark':>10} {'Realized':>12} {'Unrealized':>12} {'GroupId':20}"
    )
    for p in positions_sorted:
        print(
            f"{str(p.get('status','-')):8} "
            f"{str(p.get('direction','-')):8} "
            f"{int(p.get('quantity',0)):>4} "
            f"{str(p.get('symbol','-'))[:32]:32} "
            f"{fmt_num(p.get('averagePrice')).rjust(10)} "
            f"{fmt_num(p.get('lastMarkPrice')).rjust(10)} "
            f"{fmt_num(p.get('realizedPnl')).rjust(12)} "
            f"{fmt_num(p.get('unrealizedPnl')).rjust(12)} "
            f"{str(p.get('groupId','-'))[:20]:20}"
        )
    if len(positions) > MAX_POSITIONS_TO_SHOW:
        print(f"... showing first {MAX_POSITIONS_TO_SHOW} of {len(positions)} positions")
    print("-" * 110)



def print_orders(orders: List[Dict[str, Any]]) -> None:
    print(f"{BOLD}RECENT ORDERS{RESET}")
    if not orders:
        print("No orders.")
        print("-" * 110)
        return

    print(f"Summary: {summarize_orders(orders)}")
    rows = orders[:MAX_ORDERS_TO_SHOW]
    print(f"{'Created':19} {'Status':8} {'Side':8} {'Qty':>4} {'Symbol':32} {'ReqPx':>10} {'FillPx':>10} {'GroupId':20}")
    for o in rows:
        print(
            f"{fmt_dt(o.get('createdUtc')):19} "
            f"{str(o.get('status','-')):8} "
            f"{side_arrow(o.get('side','-')):8} "
            f"{int(o.get('quantity',0)):>4} "
            f"{str(o.get('symbol','-'))[:32]:32} "
            f"{fmt_num(o.get('requestedPrice')).rjust(10)} "
            f"{fmt_num(o.get('fillPrice')).rjust(10)} "
            f"{str(o.get('groupId','-'))[:20]:20}"
        )
    if len(orders) > MAX_ORDERS_TO_SHOW:
        print(f"... showing latest {MAX_ORDERS_TO_SHOW} of {len(orders)} orders")
    print("-" * 110)



def main() -> int:
    client = ApiClient(API_BASE_URL, VERIFY_SSL)
    last_error = None

    while True:
        try:
            # Refresh MTM before reading views so PnL/mark prices/equity snapshots stay current.
            portfolio = client.refresh_portfolio(SIMULATION_RUN_ID)
            orders = client.get_orders(SIMULATION_RUN_ID)
            positions = client.get_positions(SIMULATION_RUN_ID)

            perf = None
            try:
                perf = client.get_performance(SIMULATION_RUN_ID)
            except Exception:
                perf = None

            clear_screen()
            print_header(SIMULATION_RUN_ID)
            print_portfolio(portfolio, perf)
            print_group_summaries(portfolio)
            print_positions(positions)
            print_orders(orders)
            print(f"Refresh every {REFRESH_SECONDS}s | Press Ctrl+C to stop")
            last_error = None

        except KeyboardInterrupt:
            print("\nStopped by user.")
            return 0
        except Exception as ex:
            clear_screen()
            print_header(SIMULATION_RUN_ID)
            print(f"{RED}{BOLD}ERROR{RESET}: {ex}")
            print("Check these things:")
            print("1) API is running")
            print("2) SIMULATION_RUN_ID exists")
            print("3) /api/Simulator/runs/{id}/portfolio/refresh is working")
            last_error = ex

        time.sleep(REFRESH_SECONDS)


if __name__ == "__main__":
    sys.exit(main())
