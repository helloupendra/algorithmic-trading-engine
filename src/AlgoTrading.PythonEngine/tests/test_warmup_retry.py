"""
Warmup history that fails on the broker token must be retried, not shrugged
off. Ghost evaluates nothing for 51 bars; a run that skips warmup because the
token was dead at 09:17 spends the whole day silent.
"""

import unittest

import _bootstrap  # noqa: F401

from core.warmup_retry import fetch_warmup_bars_with_retry, is_auth_failure


class _Engine:
    """A DataEngine stand-in whose answers are scripted per construction."""
    script = []

    def __init__(self):
        self.answer = _Engine.script.pop(0)

    def get_historical_bars(self, **_):
        if isinstance(self.answer, Exception):
            raise self.answer
        return self.answer


class AuthFailureClassification(unittest.TestCase):
    def test_fyers_wordings(self):
        self.assertTrue(is_auth_failure(RuntimeError("Failed to fetch data: Could not authenticate the user")))
        self.assertTrue(is_auth_failure(RuntimeError("API is not authenticated with Broker. Please login.")))
        self.assertTrue(is_auth_failure(RuntimeError("Token is expired")))

    def test_other_errors_are_not(self):
        self.assertFalse(is_auth_failure(RuntimeError("request limit reached")))
        self.assertFalse(is_auth_failure(ConnectionError("timed out")))


class Retrying(unittest.TestCase):
    def _fetch(self, script, **kw):
        _Engine.script = list(script)
        sleeps = []
        bars = fetch_warmup_bars_with_retry(
            make_engine=_Engine, symbol="NSE:NIFTY50-INDEX", resolution="5", start_date="2026-08-26",
            end_date="2026-09-10", label="NIFTY", sleep=sleeps.append, **kw)
        return bars, sleeps

    def test_a_dead_token_is_retried_with_a_fresh_engine_until_the_sign_in_lands(self):
        dead = RuntimeError("Failed to fetch data: Could not authenticate the user")
        bars, sleeps = self._fetch([dead, dead, ["bar"]], attempts=5, delay=30)
        self.assertEqual(["bar"], bars)
        self.assertEqual([30, 30], sleeps)
        self.assertEqual([], _Engine.script)  # three engines built, one per attempt

    def test_attempts_run_out_and_the_last_error_surfaces(self):
        dead = RuntimeError("Could not authenticate the user")
        _Engine.script = [dead, dead, dead]
        with self.assertRaises(RuntimeError):
            fetch_warmup_bars_with_retry(
                make_engine=_Engine, symbol="s", resolution="5", start_date="a", end_date="b",
                label="X", attempts=3, delay=1, sleep=lambda _: None)

    def test_other_failures_are_not_retried(self):
        _Engine.script = [RuntimeError("request limit reached"), ["never"]]
        with self.assertRaises(RuntimeError):
            fetch_warmup_bars_with_retry(
                make_engine=_Engine, symbol="s", resolution="5", start_date="a", end_date="b",
                label="X", attempts=3, delay=1, sleep=lambda _: None)
        self.assertEqual([["never"]], _Engine.script)


if __name__ == "__main__":
    unittest.main()
