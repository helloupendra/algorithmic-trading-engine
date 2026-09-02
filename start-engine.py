import subprocess
import sys
import os
import threading
import signal

ROOT_DIR = os.path.dirname(os.path.abspath(__file__))

def stream_output(process, name, color_code):
    """Reads stdout from a process and prefixes it with a colored name."""
    while True:
        line = process.stdout.readline()
        if not line:
            break
        print(f"\033[{color_code}m[{name}]\033[0m {line.decode('utf-8', errors='replace')}", end='')

def main():
    print("\nStarting AlgoTrading Engine (API + Frontend + Ingestor)...\n")

    env = os.environ.copy()
    if "PYTHONPATH" in env:
        env["PYTHONPATH"] = f"{ROOT_DIR}{os.pathsep}{env['PYTHONPATH']}"
    else:
        env["PYTHONPATH"] = ROOT_DIR

    frontend_cmd = ["npm", "run", "dev"]
    # Adjust for windows npm command
    if os.name == 'nt':
        frontend_cmd[0] = "npm.cmd"

    processes = []
    
    try:
        # Start API
        api_proc = subprocess.Popen(
            ["dotnet", "run", "--project", "src/AlgoTrading.Api"],
            cwd=ROOT_DIR,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT
        )
        processes.append(api_proc)
        threading.Thread(target=stream_output, args=(api_proc, "API", "34"), daemon=True).start()

        # Start Frontend
        web_proc = subprocess.Popen(
            frontend_cmd,
            cwd=os.path.join(ROOT_DIR, "web"),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT
        )
        processes.append(web_proc)
        threading.Thread(target=stream_output, args=(web_proc, "WEB", "32"), daemon=True).start()

        # Wait for API to warm up before starting Ingestor
        import time
        print("\n\033[93mWaiting 8 seconds for .NET API to start before launching Ingestor...\033[0m\n")
        time.sleep(8)

        # Start Ingestor
        ingestor_proc = subprocess.Popen(
            [sys.executable, "src/AlgoTrading.PythonEngine/market_data/live/fyers_streamer.py"],
            cwd=ROOT_DIR,
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT
        )
        processes.append(ingestor_proc)
        threading.Thread(target=stream_output, args=(ingestor_proc, "INGESTOR", "33"), daemon=True).start()

        print("\n\033[96mAll processes started. Press Ctrl+C to stop all.\033[0m\n")

        # Wait for any process to exit
        for p in processes:
            p.wait()

    except KeyboardInterrupt:
        print("\nStopping all processes...")
    finally:
        for p in processes:
            if p.poll() is None:
                p.terminate()
        print("Engine stopped.")

if __name__ == "__main__":
    main()
