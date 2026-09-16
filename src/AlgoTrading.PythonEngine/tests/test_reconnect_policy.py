"""
A feed that cannot keep a connection must back off, not hammer the vendor.

2026-09-16: yesterday's Dhan feed ran into a session whose token had died. It
connected, subscribed 267 symbols, was dropped at once, and the 20-second
watchdog rebuilt it — for an hour, until Dhan answered "Too many requests from
this IP hence client id is blocked". The desk then had no Dhan data at all,
while the process still looked alive.
"""

import unittest

from core.live.reconnect_policy import (
    BASE_DELAY_SECONDS,
    MAX_DELAY_SECONDS,
    RATE_LIMIT_DELAY_SECONDS,
    describe,
    is_rate_limited,
    reconnect_delay,
)


class DelayTests(unittest.TestCase):
    def test_a_working_connection_reconnects_at_once(self):
        self.assertEqual(0.0, reconnect_delay(0))

    def test_tickless_cycles_double_the_wait_up_to_the_cap(self):
        waits = [reconnect_delay(n) for n in range(1, 9)]
        self.assertEqual(BASE_DELAY_SECONDS, waits[0])
        self.assertEqual([5.0, 10.0, 20.0, 40.0, 80.0, 160.0], waits[:6])
        self.assertTrue(all(b >= a for a, b in zip(waits, waits[1:])))
        self.assertEqual(MAX_DELAY_SECONDS, waits[-1])

    def test_never_longer_than_the_cap(self):
        self.assertEqual(MAX_DELAY_SECONDS, reconnect_delay(50))
        self.assertEqual(MAX_DELAY_SECONDS, reconnect_delay(50, "429 Too Many Requests"))


class RateLimitTests(unittest.TestCase):
    def test_the_vendors_own_words_are_recognised(self):
        for text in ("Handshake status 429 Too Many Requests",
                     "Too many requests from this IP hence client id is blocked",
                     "rate limit exceeded"):
            self.assertTrue(is_rate_limited(text), text)
        self.assertFalse(is_rate_limited("connection dropped without a reason"))
        self.assertFalse(is_rate_limited(None))

    def test_a_rate_limited_vendor_gets_a_real_pause_from_the_first_failure(self):
        self.assertEqual(RATE_LIMIT_DELAY_SECONDS, reconnect_delay(1, "429 Too Many Requests"))
        self.assertEqual(RATE_LIMIT_DELAY_SECONDS, reconnect_delay(2, "client id is blocked"))

    def test_the_log_line_names_the_cause(self):
        self.assertIn("rate-limiting", describe(3, 60.0, "429 Too Many Requests"))
        self.assertIn("token", describe(3, 60.0, "429 Too Many Requests"))
        self.assertIn("no ticks", describe(3, 20.0, "connection dropped without a reason"))
