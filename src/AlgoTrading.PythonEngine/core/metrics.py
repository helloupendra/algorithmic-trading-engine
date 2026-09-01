from prometheus_client import start_http_server, Gauge, Counter, Histogram
import threading
import time

# Metrics definitions
REDIS_LAG = Gauge('algotrading_redis_lag_seconds', 'Time difference between tick timestamp and processing time')
ORDERS_EMITTED = Counter('algotrading_orders_emitted_total', 'Total number of orders emitted by the strategy')
STRATEGY_LOOP_DURATION = Histogram('algotrading_strategy_loop_duration_seconds', 'Time spent in a single strategy loop')
TICK_PROCESSED = Counter('algotrading_ticks_processed_total', 'Total market ticks processed')

def start_metrics_server(port=8000):
    """
    Starts the Prometheus metrics HTTP server in a daemon thread.
    """
    def run_server():
        try:
            start_http_server(port)
            print(f"Metrics server started on port {port}")
        except Exception as e:
            print(f"Failed to start metrics server: {e}")

    thread = threading.Thread(target=run_server, daemon=True)
    thread.start()
