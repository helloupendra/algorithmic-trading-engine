import json
import sys
import argparse
from datetime import datetime

def parse_utc(utc_str):
    try:
        return datetime.strptime(utc_str, "%Y-%m-%dT%H:%M:%S.%fZ")
    except ValueError:
        return datetime.strptime(utc_str, "%Y-%m-%dT%H:%M:%SZ")

def analyze_orders(json_filepath):
    with open(json_filepath, 'r') as f:
        orders = json.load(f)

    # Sort orders chronologically by filledUtc
    orders.sort(key=lambda x: x.get('filledUtc', ''))

    open_positions = {}
    completed_trades = []
    
    total_pnl = 0.0

    for order in orders:
        if order.get('status') != 'Filled':
            continue
            
        symbol = order['symbol']
        side = order['side']
        qty = order['quantity']
        price = order['fillPrice']
        time = parse_utc(order['filledUtc'])

        if symbol not in open_positions or open_positions[symbol]['qty'] == 0:
            # New Entry
            open_positions[symbol] = {
                'entry_time': time,
                'entry_side': side,
                'entry_price': price,
                'qty': qty
            }
        else:
            pos = open_positions[symbol]
            if pos['entry_side'] != side:
                # This is an EXIT
                # Calculate PNL
                if pos['entry_side'] == 'BUY':
                    pnl = (price - pos['entry_price']) * qty
                else: # Entry was SELL
                    pnl = (pos['entry_price'] - price) * qty
                
                completed_trades.append({
                    'symbol': symbol,
                    'entry_time': pos['entry_time'],
                    'entry_side': pos['entry_side'],
                    'entry_price': pos['entry_price'],
                    'exit_time': time,
                    'exit_side': side,
                    'exit_price': price,
                    'qty': qty,
                    'pnl': pnl
                })
                
                total_pnl += pnl
                open_positions[symbol]['qty'] -= qty

    print("=== COMPLETED TRADES ===")
    for t in completed_trades:
        print(f"{t['symbol']} | {t['entry_side']} -> {t['exit_side']} | {t['qty']} | PNL: {t['pnl']:.2f}")

    print(f"\nTOTAL REALIZED PNL: {total_pnl:.2f}")

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('file', type=str)
    args = parser.parse_args()
    analyze_orders(args.file)