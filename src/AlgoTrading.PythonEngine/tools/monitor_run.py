import requests
from core.api_client import api_get, api_post, api_delete, api_put
import argparse
import time
import sys
import urllib3
import os

# Disable SSL warnings for localhost
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL

def fetch_and_display(run_id: int):
    try:
        # Fetch Portfolio
        resp_port = api_get(f"{API_BASE_URL}/api/Simulator/runs/{run_id}/portfolio", verify=False)
        if resp_port.status_code == 404:
            print(f"Simulation run {run_id} not found.")
            return False
            
        resp_port.raise_for_status()
        portfolio = resp_port.json()

        # Fetch Positions
        resp_pos = api_get(f"{API_BASE_URL}/api/Simulator/runs/{run_id}/positions", verify=False)
        resp_pos.raise_for_status()
        positions = resp_pos.json()

        # Fetch Orders
        resp_ord = api_get(f"{API_BASE_URL}/api/Simulator/runs/{run_id}/orders", verify=False)
        resp_ord.raise_for_status()
        orders = resp_ord.json()

        print("\033[H\033[J") # Clear screen
        print(f"=== SIMULATION RUN ID: {run_id} ===")
        print(f"Timestamp: {time.strftime('%Y-%m-%d %H:%M:%S')}")
        
        # Display Portfolio
        print("\n--- PORTFOLIO & PnL ---")
        init_cap = portfolio.get('initialCapital', 0)
        curr_cap = portfolio.get('currentCapital', 0)
        realized = portfolio.get('realizedPnL', 0)
        unrealized = portfolio.get('unrealizedPnL', 0)
        
        print(f"Initial Capital : {init_cap:,.2f}")
        print(f"Current Capital : {curr_cap:,.2f}")
        print(f"Realized PnL    : \033[1m{realized:,.2f}\033[0m")
        print(f"Unrealized PnL  : \033[1m{unrealized:,.2f}\033[0m")
        total = realized + unrealized
        print(f"Total PnL       : \033[1;32m{total:,.2f}\033[0m" if total >= 0 else f"Total PnL       : \033[1;31m{total:,.2f}\033[0m")

        # Display Pos
        print("\n--- POSITIONS ---")
        for pos in positions:
            print(f"{pos.get('symbol')} | Qty: {pos.get('quantity')} | Entry: {pos.get('averageEntryPrice', 0):.2f}")

        # Display Orders
        print("\n--- ORDERS ---")
        for ord in orders[-5:]: # Last 5
            print(f"{ord.get('symbol')} | {ord.get('side')} | {ord.get('status')} | {ord.get('fillPrice', 0):.2f}")

        return True
    except Exception as e:
        print(f"Error: {e}")
        return False

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('run_id', type=int)
    args = parser.parse_args()
    
    while True:
        if not fetch_and_display(args.run_id):
            break
        time.sleep(2)