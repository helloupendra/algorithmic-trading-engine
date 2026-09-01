from __future__ import annotations

import json
import os
import sys
import time
from collections import Counter
from datetime import datetime
from typing import Any, Dict, List, Optional

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
REFRESH_SECONDS = 2            # refresh interval for dashboard
MAX_SIGNALS_TO_SHOW = 12
MAX_ORDERS_TO_SHOW = 12
MAX_POSITIONS_TO_SHOW = 12
USE_ANSI = True               # set False if your terminal shows weird characters/colors

# -----------------------------------------------------------------------------
# ANSI COLORS / FORMATTING
# -----------------------------------------------------------------------------
if USE_ANSI:
    GREEN = "\033[92m"
    RED = "\033[91m"
    YELLOW = "\033[93m"
    CYAN = "\033[96m"
    BLUE = "\033[94m"
    MAGENTA = "\033[95m"
    BOLD = "\033[1m"
    DIM = "\033[2m"
    RESET = "\033[0m"
else:
    GREEN = RED = YELLOW = CYAN = BLUE = MAGENTA = BOLD = DIM = RESET = ""

SEPARATOR = "─" * 130


class ApiClient:
    def __init__(self, base_url: str, verify_ssl: bool = False):
        self.base_url = base_url.rstrip("/")
        self.verify_ssl = verify_ssl
        self.http = requests.Session()

    def _get(self, path: str) -> Any:
        resp = self.http.get(f"{self.base_url}{path}", verify=self.verify_ssl, timeout=30)
        resp.raise_for_status()
        return resp.json()

    def _post(self, path: str, payload: Dict[str, Any] | None = None) -> Any:
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

    def get_performance(self, run_id: int) -> Dict[str, Any]:
        return self._get(f"/api/Simulator/runs/{run_id}/performance")

    def get_equity_curve(self, run_id: int) -> List[Dict[str, Any]]:
        return self._get(f"/api/Simulator/runs/{run_id}/equity-curve")


# -----------------------------------------------------------------------------
# HELPERS
# -----------------------------------------------------------------------------
def clear_screen() -> None:
    os.system("cls" if os.name == "nt" else "clear")


def safe_float(v: Any, default: float = 0.0) -> float:
    try:
        if v is None:
            return default
        return float(v)
    except Exception:
        return default


def fmt_num(value: Any, decimals: int = 2) -> str:
    if value is None:
        return "-"
    try:
        return f"{float(value):,.{decimals}f}"
    except Exception:
        return str(value)


def fmt_money(value: Any) -> str:
    if value is None:
        return "-"
    try:
        num = float(value)
        color = GREEN if num > 0 else RED if num < 0 else RESET
        return f"{color}{num:,.2f}{RESET}"
    except Exception:
        return str(value)


def fmt_dt(value: Any) -> str:
    if not value:
        return "-"
    try:
        dt = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        return dt.strftime("%Y-%m-%d %H:%M:%S")
    except Exception:
        return str(value)


def trim(s: Any, n: int) -> str:
    text = "" if s is None else str(s)
    return text if len(text) <= n else text[: n - 1] + "…"


def signal_color(signal_type: str) -> str:
    s = (signal_type or "").upper()
    if "OPEN" in s or "BUY" in s:
        return GREEN
    if "CLOSE" in s or "EXIT" in s or "SELL" in s:
        return YELLOW
    if "SHIFT" in s or "ADJUST" in s:
        return CYAN
    return RESET


def side_text(side: str) -> str:
    s = (side or "").upper()
    if s == "BUY":
        return f"{GREEN}BUY{RESET}"
    if s == "SELL":
        return f"{RED}SELL{RESET}"
    return s


def direction_text(direction: str) -> str:
    d = (direction or "").upper()
    if d == "LONG":
        return f"{GREEN}LONG{RESET}"
    if d == "SHORT":
        return f"{RED}SHORT{RESET}"
    return d


def status_text(status: str) -> str:
    s = (status or "").upper()
    if s == "OPEN" or s == "FILLED":
        return f"{GREEN}{s}{RESET}"
    if s == "CLOSED":
        return f"{YELLOW}{s}{RESET}"
    if s == "FAILED" or s == "CANCELLED":
        return f"{RED}{s}{RESET}"
    return s


def summarize_orders(orders: List[Dict[str, Any]]) -> str:
    cnt = Counter((o.get("status", "?"), o.get("side", "?")) for o in orders)
    parts = [f"{status}/{side}:{n}" for (status, side), n in sorted(cnt.items())]
    return ", ".join(parts) if parts else "None"


def compact_json(text: Any, max_len: int = 80) -> str:
    if text is None:
        return ""
    if not isinstance(text, str):
        try:
            text = json.dumps(text)
        except Exception:
            text = str(text)
    return trim(text.replace("\n", " ").replace("  ", " "), max_len)


def sparkline(values: List[float], width: int = 30) -> str:
    if not values:
        return "(no equity snapshots)"
    chars = "▁▂▃▄▅▆▇█"
    if len(values) > width:
        # downsample evenly
        step = len(values) / width
        sampled = [values[int(i * step)] for i in range(width)]
    else:
        sampled = values
    mn, mx = min(sampled), max(sampled)
    if mx == mn:
        return chars[0] * len(sampled)
    out = []
    for v in sampled:
        idx = int((v - mn) / (mx - mn) * (len(chars) - 1))
        out.append(chars[idx])
    return "".join(out)


# -----------------------------------------------------------------------------
# RENDERERS
# -----------------------------------------------------------------------------
def print_header(run_id: int) -> None:
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    print(f"{BOLD}{CYAN}ALGO TRADING LIVE TERMINAL DASHBOARD{RESET}")
    print(f"RunId: {run_id}   Local Time: {now}")
    print(SEPARATOR)


def print_portfolio(portfolio: Dict[str, Any], perf: Optional[Dict[str, Any]], equity_curve: List[Dict[str, Any]]) -> None:
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

    eq_vals = [safe_float(x.get('currentEquity')) for x in equity_curve]
    if eq_vals:
        print(f"Equity Curve     : {sparkline(eq_vals, width=48)}")
    else:
        print("Equity Curve     : (no snapshots yet)")

    print(SEPARATOR)


def print_group_summary(portfolio: Dict[str, Any]) -> None:
    groups = portfolio.get('groups') or []
    print(f"{BOLD}{BLUE}GROUP SUMMARY{RESET}")
    if not groups:
        print("No groups yet.")
        print(SEPARATOR)
        return

    header = f"{'GroupId':30} {'Status':8} {'Open':>4} {'Closed':>6} {'UsedCap':>14} {'Realized':>14} {'Unrealized':>14}"
    print(header)
    print("-" * len(header))
    for g in groups:
        print(
            f"{trim(g.get('groupId','-'),30):30} "
            f"{status_text(g.get('status','-')):8} "
            f"{int(g.get('openPositionCount',0)):>4} "
            f"{int(g.get('closedPositionCount',0)):>6} "
            f"{fmt_num(g.get('usedCapital')).rjust(14)} "
            f"{fmt_num(g.get('realizedPnl')).rjust(14)} "
            f"{fmt_num(g.get('unrealizedPnl')).rjust(14)}"
        )
    print(SEPARATOR)


def print_signals(signals: List[Dict[str, Any]]) -> None:
    print(f"{BOLD}{BLUE}RECENT SIGNALS{RESET}")
    if not signals:
        print("No signals.")
        print(SEPARATOR)
        return

    rows = signals[-MAX_SIGNALS_TO_SHOW:]
    header = f"{'Time':19} {'Type':16} {'Strategy':10} {'GroupId':28} {'Metadata':50}"
    print(header)
    print("-" * len(header))
    for s in rows:
        s_type = str(s.get('signalType', '-'))
        color = signal_color(s_type)
        print(
            f"{fmt_dt(s.get('timestampUtc')):19} "
            f"{color}{trim(s_type,16):16}{RESET} "
            f"{trim(s.get('strategyName','-'),10):10} "
            f"{trim(s.get('groupId','-'),28):28} "
            f"{compact_json(s.get('metadataJson',''),50):50}"
        )
    print(SEPARATOR)


def print_positions(positions: List[Dict[str, Any]]) -> None:
    print(f"{BOLD}{BLUE}POSITIONS{RESET}")
    if not positions:
        print("No positions.")
        print(SEPARATOR)
        return

    rows = sorted(
        positions,
        key=lambda x: (x.get('status') != 'Open', x.get('groupId', ''), x.get('symbol', ''))
    )[:MAX_POSITIONS_TO_SHOW]

    header = f"{'Status':8} {'Dir':8} {'Qty':>4} {'Symbol':32} {'Avg':>10} {'Mark':>10} {'Realized':>12} {'Unrealized':>12} {'GroupId':20}"
    print(header)
    print("-" * len(header))
    for p in rows:
        print(
            f"{status_text(p.get('status','-')):8} "
            f"{direction_text(p.get('direction','-')):8} "
            f"{int(p.get('quantity',0)):>4} "
            f"{trim(p.get('symbol','-'),32):32} "
            f"{fmt_num(p.get('averagePrice')).rjust(10)} "
            f"{fmt_num(p.get('lastMarkPrice')).rjust(10)} "
            f"{fmt_num(p.get('realizedPnl')).rjust(12)} "
            f"{fmt_num(p.get('unrealizedPnl')).rjust(12)} "
            f"{trim(p.get('groupId','-'),20):20}"
        )
    if len(positions) > MAX_POSITIONS_TO_SHOW:
        print(f"... showing first {MAX_POSITIONS_TO_SHOW} of {len(positions)} positions")
    print(SEPARATOR)


def print_orders(orders: List[Dict[str, Any]]) -> None:
    print(f"{BOLD}{BLUE}RECENT ORDERS{RESET}")
    if not orders:
        print("No orders.")
        print(SEPARATOR)
        return

    print(f"Summary: {summarize_orders(orders)}")
    rows = orders[:MAX_ORDERS_TO_SHOW]
    header = f"{'Created':19} {'Status':8} {'Side':8} {'Qty':>4} {'Symbol':32} {'ReqPx':>10} {'FillPx':>10} {'GroupId':20}"
    print(header)
    print("-" * len(header))
    for o in rows:
        print(
            f"{fmt_dt(o.get('createdUtc')):19} "
            f"{status_text(o.get('status','-')):8} "
            f"{side_text(o.get('side','-')):8} "
            f"{int(o.get('quantity',0)):>4} "
            f"{trim(o.get('symbol','-'),32):32} "
            f"{fmt_num(o.get('requestedPrice')).rjust(10)} "
            f"{fmt_num(o.get('fillPrice')).rjust(10)} "
            f"{trim(o.get('groupId','-'),20):20}"
        )
    if len(orders) > MAX_ORDERS_TO_SHOW:
        print(f"... showing latest {MAX_ORDERS_TO_SHOW} of {len(orders)} orders")
    print(SEPARATOR)


# -----------------------------------------------------------------------------
# MAIN LOOP
# -----------------------------------------------------------------------------
def main() -> int:
    client = ApiClient(API_BASE_URL, VERIFY_SSL)

    while True:
        try:
            portfolio = client.refresh_portfolio(SIMULATION_RUN_ID)
            signals = client.get_signals(SIMULATION_RUN_ID)
            orders = client.get_orders(SIMULATION_RUN_ID)
            positions = client.get_positions(SIMULATION_RUN_ID)

            perf = None
            try:
                perf = client.get_performance(SIMULATION_RUN_ID)
            except Exception:
                perf = None

            equity_curve = []
            try:
                equity_curve = client.get_equity_curve(SIMULATION_RUN_ID)
            except Exception:
                equity_curve = []

            clear_screen()
            print_header(SIMULATION_RUN_ID)
            print_portfolio(portfolio, perf, equity_curve)
            print_group_summary(portfolio)
            print_signals(signals)
            print_positions(positions)
            print_orders(orders)
            print(f"Refresh every {REFRESH_SECONDS}s | Press Ctrl+C to stop")

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
            print("4) /signals /orders /positions /performance endpoints are available")

        time.sleep(REFRESH_SECONDS)


if __name__ == "__main__":
    sys.exit(main())
