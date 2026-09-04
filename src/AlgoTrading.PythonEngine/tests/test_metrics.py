"""
Metrics port selection: an explicit port that is taken raises (the runner
logs and continues), and auto mode walks the range to the first free port.
Uses ephemeral ports so it never collides with runners on 8000..8019.
"""

import socket
import unittest

import _bootstrap  # noqa: F401

from core.metrics import start_metrics_server, start_metrics_server_auto


def _hold_port() -> socket.socket:
    """Bind an ephemeral port on all interfaces and keep it open."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("0.0.0.0", 0))
    sock.listen(1)
    return sock


class MetricsPortTests(unittest.TestCase):
    def test_explicit_port_in_use_raises(self) -> None:
        holder = _hold_port()
        try:
            with self.assertRaises(OSError):
                start_metrics_server(holder.getsockname()[1])
        finally:
            holder.close()

    def test_auto_skips_taken_port_and_binds_next(self) -> None:
        holder = _hold_port()
        taken = holder.getsockname()[1]
        try:
            bound = start_metrics_server_auto(taken, taken + 5)
            self.assertNotEqual(bound, taken)
            self.assertTrue(taken < bound <= taken + 5)

            # The server is really listening on the returned port.
            with socket.create_connection(("127.0.0.1", bound), timeout=2) as probe:
                probe.sendall(b"GET /metrics HTTP/1.0\r\nHost: localhost\r\n\r\n")
                head = probe.recv(64)
            self.assertTrue(head.startswith(b"HTTP/1."))
        finally:
            holder.close()

    def test_auto_raises_when_range_exhausted(self) -> None:
        holder = _hold_port()
        taken = holder.getsockname()[1]
        try:
            with self.assertRaises(OSError):
                start_metrics_server_auto(taken, taken)
        finally:
            holder.close()

    def test_auto_rejects_empty_range(self) -> None:
        with self.assertRaises(ValueError):
            start_metrics_server_auto(8010, 8000)


if __name__ == "__main__":
    unittest.main()
