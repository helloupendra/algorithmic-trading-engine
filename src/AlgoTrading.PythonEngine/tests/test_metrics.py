"""
Metrics port selection: an explicit port that is taken raises (the runner
logs and continues), and auto mode walks the range to the first free port.
Uses ephemeral ports so it never collides with runners on 8000..8019.

And where it listens: loopback unless METRICS_BIND_ADDRESS says otherwise
(27 Sep 2026: the default 0.0.0.0 put up to twenty runner ports on the
server's public address).
"""

import os
import socket
import unittest
from unittest import mock

import _bootstrap  # noqa: F401

import core.metrics as metrics
from core.metrics import start_metrics_server, start_metrics_server_auto


def _hold_port() -> socket.socket:
    """Bind an ephemeral port where the metrics server binds (another runner, in effect) and keep it open."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind(("127.0.0.1", 0))
    sock.listen(1)
    return sock


def _free_port() -> int:
    holder = _hold_port()
    port = holder.getsockname()[1]
    holder.close()
    return port


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


class MetricsBindAddressTests(unittest.TestCase):
    def test_it_listens_on_loopback_not_on_every_interface(self) -> None:
        bound = []
        real = metrics.start_http_server

        def recording(port, addr="0.0.0.0", **kw):
            server, thread = real(port, addr=addr, **kw)
            bound.append(server.socket.getsockname()[0])
            return server, thread

        env = {k: v for k, v in os.environ.items() if k != "METRICS_BIND_ADDRESS"}
        with mock.patch.dict(os.environ, env, clear=True), mock.patch.object(metrics, "start_http_server", recording):
            port = start_metrics_server_auto(_free_port(), 65535)
        self.assertEqual(["127.0.0.1"], bound)
        with socket.create_connection(("127.0.0.1", port), timeout=2):
            pass

    def test_the_address_can_be_set_for_a_scraper_that_needs_another(self) -> None:
        calls = []
        with mock.patch.object(metrics, "start_http_server", lambda port, addr: calls.append((port, addr))):
            with mock.patch.dict(os.environ, {"METRICS_BIND_ADDRESS": " 0.0.0.0 "}):
                start_metrics_server(9100)
            with mock.patch.dict(os.environ, {"METRICS_BIND_ADDRESS": ""}):
                start_metrics_server_auto(9101, 9101)
            start_metrics_server(9102, addr="172.17.0.1")
        self.assertEqual([(9100, "0.0.0.0"), (9101, "127.0.0.1"), (9102, "172.17.0.1")], calls)


if __name__ == "__main__":
    unittest.main()
