"""
core/metrics.py

Prometheus metrics for the live runner. `start_metrics_server` binds one
explicit port; `start_metrics_server_auto` walks a port range so several
runners (one per strategy/underlying) can coexist on one host without the
API having to hand out ports. Both bind synchronously and raise `OSError`
when nothing could be bound — the caller decides whether that is fatal (the
runner logs and continues without metrics).

The server binds to loopback (127.0.0.1) unless METRICS_BIND_ADDRESS says
otherwise. prometheus_client binds 0.0.0.0 by default, so every runner's
metrics — up to twenty ports, 8000..8019 — listened on the server's public
address (found by the Sentinel review of 27 Sep 2026): its open-port rule would
have opened a HIGH incident per port on every trading day, and only the AWS
security group stood between them and the internet. A scraper that needs
another address says so: the Prometheus container in docker-compose.yml
reaches the host through host.docker.internal:host-gateway, which on Linux is
the Docker bridge, not loopback — it needs METRICS_BIND_ADDRESS set to that
bridge's gateway address (or 0.0.0.0, with the port closed in the firewall).
"""

import os
from typing import Optional, Tuple

from prometheus_client import start_http_server, Gauge, Counter, Histogram

# Metrics definitions
REDIS_LAG = Gauge('algotrading_redis_lag_seconds', 'Time difference between tick timestamp and processing time')
ORDERS_EMITTED = Counter('algotrading_orders_emitted_total', 'Total number of orders emitted by the strategy')
SIGNALS_FILTERED = Counter("algotrading_signals_filtered_total", "Opening signals blocked by the run market-context filters")
STRATEGY_LOOP_DURATION = Histogram('algotrading_strategy_loop_duration_seconds', 'Time spent in a single strategy loop')
TICK_PROCESSED = Counter('algotrading_ticks_processed_total', 'Total market ticks processed')
TICK_ERRORS = Counter('algotrading_tick_errors_total',
                      'Ticks the strategy loop raised on and skipped (ERROR PROCESSING TICK)')

# `--metrics-port 0` means "pick the first free port in this range".
AUTO_METRICS_PORT_RANGE: Tuple[int, int] = (8000, 8019)

# Where the metrics server listens unless METRICS_BIND_ADDRESS names another address.
DEFAULT_METRICS_BIND_ADDRESS = "127.0.0.1"


def metrics_bind_address() -> str:
    """The address the metrics server binds: METRICS_BIND_ADDRESS, or loopback."""
    return (os.environ.get("METRICS_BIND_ADDRESS") or "").strip() or DEFAULT_METRICS_BIND_ADDRESS


def start_metrics_server(port: int = 8000, addr: Optional[str] = None) -> int:
    """
    Starts the Prometheus metrics HTTP server (prometheus_client serves it
    from a daemon thread) on ``addr`` (default: `metrics_bind_address()`).
    Binds synchronously so a port clash surfaces here as `OSError` instead of
    dying silently inside a thread. Returns the port.
    """
    address = addr or metrics_bind_address()
    start_http_server(port, addr=address)
    print(f"Metrics server started on {address}:{port}")
    return port


def start_metrics_server_auto(
    first_port: Optional[int] = None,
    last_port: Optional[int] = None,
    addr: Optional[str] = None,
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

    address = addr or metrics_bind_address()
    last_error: Optional[BaseException] = None
    for port in range(lo, hi + 1):
        try:
            return start_metrics_server(port, addr=address)
        except OSError as ex:
            # EADDRINUSE / EACCES: another runner (or anything else) owns it.
            last_error = ex
            print(f"Metrics port {port} unavailable ({ex}); trying next")
    raise OSError(f"no free metrics port in {lo}..{hi}") from last_error
