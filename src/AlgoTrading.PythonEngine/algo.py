import sys
import os
import subprocess
import time

# Base paths
ROOT_DIR = os.path.dirname(os.path.abspath(__file__))
TOOLS_DIR = os.path.join(ROOT_DIR, "tools")
INGESTION_DIR = os.path.join(ROOT_DIR, "data_ingestion")
STRATEGIES_DIR = os.path.join(ROOT_DIR, "strategies")

def run_script(script_name, *args):
    """Helper to run a python script as a subprocess"""
    script_path = os.path.join(TOOLS_DIR, script_name)
    if not os.path.exists(script_path):
        script_path = os.path.join(INGESTION_DIR, script_name)
        if not os.path.exists(script_path):
            script_path = os.path.join(STRATEGIES_DIR, script_name)
        
    cmd = [sys.executable, script_path] + list(args)
    env = os.environ.copy()
    if "PYTHONPATH" in env:
        env["PYTHONPATH"] = f"{ROOT_DIR}{os.pathsep}{env['PYTHONPATH']}"
    else:
        env["PYTHONPATH"] = ROOT_DIR

    try:
        print("\n" + "="*50)
        subprocess.run(cmd, check=True, env=env)
        print("="*50 + "\n")
    except subprocess.CalledProcessError:
        print("\n[Error] Task failed.\n")
    except KeyboardInterrupt:
        print("\n[Returned to Main Menu]\n")

def format_symbol(symbol):
    """Automatically formats a short symbol like 'IDEA' to 'NSE:IDEA-EQ'"""
    symbol = symbol.strip().upper()
    if ":" not in symbol and "-" not in symbol:
        return f"NSE:{symbol}-EQ"
    return symbol

def clear_screen():
    """Cross-platform terminal clear"""
    os.system('cls' if os.name == 'nt' else 'clear')

def main():
    while True:
        clear_screen()
        print("\n" + "="*40)
        print("   🔥 ALGOTRADING CONTROL CENTER 🔥")
        print("="*40)
        print("[1] Start Live Data Ingestor")
        print("[2] Open Live Prices Monitor")
        print("[3] Add Single Stock to Watchlist")
        print("[4] Add Equity Group to Watchlist")
        print("[5] Add Option Chain to Watchlist")
        print("[6] Clear Entire Watchlist")
        print("[7] Start Live Strategy Runner")
        print("[8] Open Live Strategy Dashboard")
        print("[9] Exit")
        print("="*40)
        
        try:
            choice = input("\nSelect an option (1-9): ").strip()
            
            if choice == "1":
                print("\nStarting Ingestor... (Press Ctrl+C to return to menu)")
                run_script("fyers_live_stream.py")
                
            elif choice == "2":
                print("\nOpening Live Monitor... (Press Ctrl+C to return to menu)")
                run_script("monitor_all_live_prices.py")
                
            elif choice == "3":
                symbol = input("\nEnter stock symbol (e.g. IDEA or RELIANCE): ").strip()
                if symbol:
                    formatted_symbol = format_symbol(symbol)
                    print(f"Auto-formatted to: {formatted_symbol}")
                    run_script("add_group_to_watchlist.py", "--symbol", formatted_symbol)
                
            elif choice == "4":
                group = input("\nEnter Group Name (e.g. BANKNIFTY_CONSTITUENTS): ").strip()
                if group:
                    run_script("add_group_to_watchlist.py", "--group", group)
                    
            elif choice == "5":
                index = input("\nEnter Index Name (e.g. BANKNIFTY or NIFTY50): ").strip()
                if index:
                    strikes = input("Enter number of strikes on each side [Default 10]: ").strip()
                    if not strikes:
                        strikes = "10"
                    run_script("add_option_chain.py", "--index", index.upper(), "--strikes", strikes)
                    
            elif choice == "6":
                confirm = input("\nAre you sure you want to clear the watchlist? (y/n): ").strip().lower()
                if confirm == 'y':
                    run_script("clear_watchlist.py")
                    
            elif choice == "7":
                print("\nStarting Strategy Runner...")
                print("Available Strategies: ExampleStraddle, LogicEngine")
                strategy = input("Enter Strategy Name [Default: LogicEngine]: ").strip()
                if not strategy:
                    strategy = "LogicEngine"
                user_id = input("Enter User ID [Default: 1]: ").strip()
                if not user_id:
                    user_id = "1"
                    
                print("\nRunning... (Press Ctrl+C to return to menu)")
                run_script("execution_runner.py", "--strategy", strategy, "--user-id", user_id)

            elif choice == "8":
                print("\nOpening Live Strategy Dashboard...")
                user_id = input("Enter User ID to track latest run [Default: 1]: ").strip()
                if not user_id:
                    user_id = "1"
                print("\nOpening Dashboard... (Press Ctrl+C to return to menu)")
                run_script("strategy_live_terminal_dashboard_v2.py", "--user-id", user_id)

            elif choice == "9":
                print("\nExiting Control Center. Goodbye!\n")
                break
                
            else:
                print("\n[Invalid Selection] Please choose a number between 1 and 9.")
                
        except KeyboardInterrupt:
            print("\nExiting Control Center. Goodbye!\n")
            break

if __name__ == "__main__":
    main()
