import os
import sys
import math
import pandas as pd
import psycopg2
from datetime import datetime, timezone, timedelta

sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from core.data_engine import DataEngine
from strategies.ghost_tangent_crossings import GhostTangentCrossingsStrategy
from strategies.base_strategy import StrategyInput

import argparse

def round_strike(price: float, step: int) -> int:
    return int(round(price / step) * step)

def main():
    parser = argparse.ArgumentParser(description="Backtest Strategy")
    parser.add_argument("--underlying", type=str, default="BANKNIFTY", choices=["BANKNIFTY", "NIFTY"])
    args = parser.parse_args()
    
    underlying = args.underlying
    if underlying == "BANKNIFTY":
        spot_symbol = "NSE:NIFTYBANK-INDEX"
        lot_size = 15
        strike_step = 100
    elif underlying == "NIFTY":
        spot_symbol = "NSE:NIFTY50-INDEX"
        lot_size = 65
        strike_step = 50
        
    print(f"Initializing Ghost Strategy Backtester for {underlying}...")
    
    # Connect to local database where historical option bars are stored
    try:
        conn = psycopg2.connect("host=localhost port=5433 dbname=algotrading user=postgres password=admin@123")
        print("Connected to Postgres database.")
    except Exception as e:
        print(f"Failed to connect to database: {e}")
        return

    # Load all historical option bars into a DataFrame for fast lookups
    print("Loading historical option bars from database (this may take a few seconds)...")
    query = f"""
    SELECT "Symbol", "TimeStampUtc", "Open", "High", "Low", "Close"
    FROM candles
    WHERE "Symbol" LIKE 'NSE:{underlying}%'
    ORDER BY "TimeStampUtc" ASC
    """
    options_df = pd.read_sql_query(query, conn)
    options_df['TimeStampUtc'] = pd.to_datetime(options_df['TimeStampUtc']).dt.tz_convert('UTC')
    options_df.set_index(['TimeStampUtc', 'Symbol'], inplace=True)
    print(f"Loaded {len(options_df)} historical option bars.")

    engine = DataEngine()
    
    # We will backtest over the last 5 days
    end_date = datetime.now(timezone.utc)
    start_date = end_date - timedelta(days=5)
    
    print(f"\nFetching 5-minute historical index bars for {underlying} from {start_date.strftime('%Y-%m-%d')} to {end_date.strftime('%Y-%m-%d')}...")
    
    try:
        index_bars = engine.get_historical_bars(
            spot_symbol, 
            "5", 
            start_date.strftime("%Y-%m-%d"), 
            end_date.strftime("%Y-%m-%d")
        )
        print(f"Downloaded {len(index_bars)} index bars for backtesting.")
    except Exception as e:
        print(f"Failed to download index bars: {e}")
        return

    # Strategy Initialization
    strategy = GhostTangentCrossingsStrategy()
    state = strategy.initialize_state()

    current_bars = []
    trades = []
    open_position = None

    print("\nStarting Simulation Loop...")
    
    for bar in index_bars:
        current_bars.append(bar)
        
        # 1. Evaluate open positions for exit
        if open_position:
            opt_sym = open_position['symbol']
            try:
                # Look up the close price of the option for this 5m candle
                # Using bar.timestamp_start because Fyers aligns them
                opt_row = options_df.loc[(bar.timestamp_start, opt_sym)]
                current_opt_price = opt_row['Close']
                
                entry_price = open_position['entry_price']
                returns = (current_opt_price - entry_price) / entry_price
                
                exit_reason = None
                
                profit = current_opt_price - entry_price
                
                # Check Target (20 points)
                if profit >= 20.0:
                    exit_reason = "Target Hit (20 pts)"
                # Check Stop Loss (10%)
                elif returns <= -0.10:
                    exit_reason = "Stop Loss Hit (10%)"
                # Check End of Day (15:15 IST = 09:45 UTC)
                elif bar.timestamp_start.hour == 9 and bar.timestamp_start.minute >= 45:
                    exit_reason = "End of Day Square-Off"

                if exit_reason:
                    open_position['exit_price'] = current_opt_price
                    open_position['exit_time'] = bar.timestamp_start
                    open_position['profit'] = profit
                    open_position['profit_pct'] = returns * 100
                    open_position['exit_reason'] = exit_reason
                    trades.append(open_position)
                    open_position = None
                    
            except KeyError:
                # Missing option data for this specific minute, skip evaluation
                pass
                
        # 2. Feed the index bar to the Ghost Strategy
        inp = StrategyInput(
            mode="Backtest",
            underlying=underlying,
            spot_price=bar.close,
            timestamp_utc=bar.timestamp_start,
            bars={"5m": {"index": current_bars}}
        )
        
        signals = strategy.on_bar(state, inp)
        
        # 3. Process new signals
        for sig in signals:
            if open_position is None and not (bar.timestamp_start.hour == 9 and bar.timestamp_start.minute >= 45):
                # We only take 1 position at a time and avoid EOD entries
                is_buy = (sig.signal_type == "BUY")
                
                # Determine ATM strike
                spot_price = bar.close
                atm_strike = round_strike(spot_price, strike_step)
                
                opt_type = "CE" if is_buy else "PE"
                expiry_str = "26SEP" if underlying == "BANKNIFTY" else "26908" # Mocked for this demo, usually resolved dynamically
                
                opt_sym = f"NSE:{underlying}{expiry_str}{atm_strike}{opt_type}"
                
                try:
                    opt_row = options_df.loc[(bar.timestamp_start, opt_sym)]
                    entry_price = opt_row['Close']
                    
                    open_position = {
                        'entry_time': bar.timestamp_start,
                        'signal_type': sig.signal_type,
                        'symbol': opt_sym,
                        'entry_price': entry_price,
                        'spot_price': spot_price
                    }
                except KeyError:
                    # No data for this option at this time
                    pass

    # Complete the simulation
    if open_position:
        print("\nSimulation ended with an open position. Closing at last traded price.")
        opt_sym = open_position['symbol']
        try:
            # Find the last price for this option in our DB
            opt_history = options_df.xs(opt_sym, level='Symbol')
            if not opt_history.empty:
                last_price = opt_history['Close'].iloc[-1]
                last_time = opt_history.index[-1]
                
                entry_price = open_position['entry_price']
                profit = last_price - entry_price
                open_position['exit_price'] = last_price
                open_position['exit_time'] = last_time
                open_position['profit'] = profit
                open_position['profit_pct'] = ((last_price - entry_price) / entry_price) * 100
                open_position['exit_reason'] = "End of Backtest"
                trades.append(open_position)
        except KeyError:
            pass

    # --- Print Report ---
    print("\n" + "="*80)
    print("BACKTEST REPORT: Ghost Tangent Crossings")
    print("="*80)
    
    if not trades:
        print("No trades were executed during this period.")
        return
        
    wins = [t for t in trades if t['profit'] > 0]
    losses = [t for t in trades if t['profit'] <= 0]
    
    total_profit_pts = sum(t['profit'] for t in trades)
    win_rate = len(wins) / len(trades) * 100 if trades else 0
    
    total_rs = total_profit_pts * lot_size
    
    max_capital_used = 0
    gross_profit_rs = 0
    gross_loss_rs = 0
    
    # Print ledger
    ist_offset = timedelta(hours=5, minutes=30)
    for i, t in enumerate(trades, 1):
        profit_rs = t['profit'] * lot_size
        capital_req = t['entry_price'] * lot_size
        max_capital_used = max(max_capital_used, capital_req)
        
        if profit_rs > 0:
            gross_profit_rs += profit_rs
        else:
            gross_loss_rs += profit_rs
            
        profit_str = f"+Rs. {profit_rs:.2f}" if profit_rs > 0 else f"-Rs. {abs(profit_rs):.2f}"
        
        # Convert UTC to IST for display
        entry_ist = t['entry_time'] + ist_offset
        exit_ist = t['exit_time'] + ist_offset
        
        # Strategy returns BUY/SELL for index, but we always BUY options
        action = "BUY "
        
        print(f"Trade {i:02d}: {entry_ist.strftime('%m-%d %H:%M')} | {action} {t['symbol']} | "
              f"Entry: {t['entry_price']:.2f} | Exit: {t['exit_price']:.2f} ({exit_ist.strftime('%H:%M')}) | "
              f"Cap: Rs.{capital_req:.2f} | PnL: {profit_str} ({t['profit_pct']:.1f}%) | {t['exit_reason']}")
              
    print("-" * 80)
    print(f"Total Trades        : {len(trades)}")
    print(f"Win Rate            : {win_rate:.1f}% ({len(wins)}W - {len(losses)}L)")
    print(f"Max Capital Reqd    : Rs. {max_capital_used:.2f} (1 Lot)")
    print(f"Gross Profit        : Rs. {gross_profit_rs:.2f}")
    print(f"Gross Loss          : Rs. {gross_loss_rs:.2f}")
    print(f"Net PnL             : Rs. {total_rs:.2f} ({total_profit_pts:.2f} points)")
    print("=" * 80 + "\n")

if __name__ == "__main__":
    main()
