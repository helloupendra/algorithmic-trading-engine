"""
core/metrics.py

Prometheus metrics for the live runner. `start_metrics_server` binds one
explicit port; `start_metrics_server_auto` walks a port range so several
runners (one per strategy/underlying) can coexist on one host without the
API having to hand out ports. Both bind synchronously and raise `OSError`
when nothing could be bound — the caller decides whether that is fatal (the
runner logs and continues without metrics).
"""

from typing import Optional, Tuple

from prometheus_client import start_http_server, Gauge, Counter, Histogram

# Metrics definitions
REDIS_LAG = Gauge('algotrading_redis_lag_seconds', 'Time difference between tick timestamp and processing time')
ORDERS_EMITTED = Counter('algotrading_orders_emitted_total', 'Total number of orders emitted by the strategy')
STRATEGY_LOOP_DURATION = Histogram('algotrading_strategy_loop_duration_seconds', 'Time spent in a single strategy loop')
TICK_PROCESSED = Counter('algotrading_ticks_processed_total', 'Total market ticks processed')

# `--metrics-port 0` means "pick the first free port in this range".
AUTO_METRICS_PORT_RANGE: Tuple[int, int] = (8000, 8019)


def start_metrics_server(port: int = 8000) -> int:
    """
    Starts the Prometheus metrics HTTP server (prometheus_client serves it
    from a daemon thread). Binds synchronously so a port clash surfaces here
    as `OSError` instead of dying silently inside a thread. Returns the port.
    """
    start_http_server(port)
    print(f"Metrics server started on port {port}")
    return port


def start_metrics_server_auto(
    first_port: Optional[int] = None,
    last_port: Optional[int] = None,
) -> int:
    """
    Try `first_port..last_port` (inclusive, default 8000..8019) in order and
    serve metrics on the first one that binds. Returns the bound port; raises
    `OSError` when every port in the range is taken.
    """
    lo = AUTO_METRICS_PORT_RANGE[0] if first_port is None else int(first_port)
    hi = AUTO_METRICS_PORT_RANGE[1] if last_port is None else int(last_port)
    if hi < lo:
        raise ValueError(f"metrics port range is empty: {lo}..{hi}")

    last_error: Optional[BaseException] = None
    for port in range(lo, hi + 1):
        try:
            return start_metrics_server(port)
        except OSError as ex:
            # EADDRINUSE / EACCES: another runner (or anything else) owns it.
            last_error = ex
            print(f"Metrics port {port} unavailable ({ex}); trying next")
    raise OSError(f"no free metrics port in {lo}..{hi}") from last_error
