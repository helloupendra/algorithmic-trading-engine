"""
The option-chain poller (market_data/live/option_chain_poller.py).

This is the only path by which open interest ever enters the platform — the
broker's tick feed does not carry it — so what matters is that it is honest
about failing. A poller that silently writes nulls looks identical to a quiet
market, and the day it was wrong cannot be recovered afterwards.

The module talks to the platform API at import time; that is stubbed.
"""

import sys
import types
import unittest
from unittest import mock

import _bootstrap  # noqa: F401


def _install_stubs():
    api_module = types.ModuleType("core.api_client")
    api_module.build_session = lambda *a, **k: mock.MagicMock()
    api_module.PlatformApiClient = mock.MagicMock
    sys.modules.setdefault("core.api_client_real", None)
    return api_module


from market_data.live import option_chain_poller as poller  # noqa: E402


class FieldNameTests(unittest.TestCase):
    """
    Brokers rename these between versions — `oi`, `openInterest` and
    `open_interest` have all been the same field. A poller that knows only one
    spelling fails by writing nulls rather than by complaining.
    """

    def test_the_first_recognised_spelling_wins(self):
        self.assertEqual(poller._first_number({"oi": 1234}, "oi", "openInterest"), 1234.0)
        self.assertEqual(poller._first_number({"openInterest": 99}, "oi", "openInterest"), 99.0)
        self.assertEqual(poller._first_number({"open_interest": 7}, "oi", "openInterest", "open_interest"), 7.0)

    def test_absent_blank_and_unparseable_all_read_as_missing(self):
        for row in ({}, {"oi": None}, {"oi": ""}, {"oi": "not a number"}):
            self.assertIsNone(poller._first_number(row, "oi"), row)

    def test_a_zero_is_a_value_not_a_missing_field(self):
        # Open interest genuinely reaching zero is information.
        self.assertEqual(poller._first_number({"oi": 0}, "oi"), 0.0)


class NormalisationTests(unittest.TestCase):
    def test_an_option_row_becomes_a_snapshot(self):
        row = {
            "symbol": "NSE:BANKNIFTY26SEP57600CE",
            "ltp": 538.3, "ltpch": 10.2, "oi": 194000, "volume": 2840000,
            "bid": 537.5, "ask": 539.0,
        }
        out = poller.normalise_chain_row(row, spot=57369.65)

        self.assertIsNotNone(out)
        self.assertEqual(out["underlying"], "BANKNIFTY")
        self.assertEqual(out["strikePrice"], 57600.0)
        self.assertEqual(out["optionType"], "CE")
        self.assertEqual(out["spotPrice"], 57369.65)
        # The day's move comes from the chain call, so a build-up reading is
        # available on the very first round.
        self.assertEqual(out["priceChange"], 10.2)

    def test_the_underlying_row_is_not_mistaken_for_a_strike(self):
        # The response mixes the index itself in with the option rows.
        self.assertIsNone(poller.normalise_chain_row({"symbol": "NSE:NIFTYBANK-INDEX", "ltp": 57369.65}, 57369.65))
        self.assertIsNone(poller.normalise_chain_row({"symbol": ""}, 0))

    def test_a_bse_contract_normalises_too(self):
        out = poller.normalise_chain_row({"symbol": "BSE:SENSEX26SEP81000PE", "ltp": 640.0, "oi": 5000}, 81237.0)
        self.assertIsNotNone(out)
        self.assertEqual(out["underlying"], "SENSEX")
        self.assertEqual(out["optionType"], "PE")

    def test_open_interest_is_left_absent_not_zeroed(self):
        """
        The chain response has no OI in it at all — it arrives from the separate
        depth call. Absent, rather than zero: a zero would be read as "nothing is
        written at this strike", which is a claim the chain call cannot make. The
        `oi` in the fixture is ignored for the same reason it is ignored in
        production — the chain endpoint does not actually populate it.
        """
        out = poller.normalise_chain_row(
            {"symbol": "NSE:BANKNIFTY26SEP57600CE", "ltp": 538.3, "oi": 194000}, 57369.65)
        self.assertNotIn("openInterest", out)
        self.assertNotIn("volume", out)

    def test_a_missing_price_field_is_null_rather_than_zero(self):
        out = poller.normalise_chain_row({"symbol": "NSE:BANKNIFTY26SEP57600CE"}, 57369.65)
        self.assertIsNone(out["lastTradedPrice"])
        self.assertIsNone(out["priceChange"])
        self.assertIsNone(out["bidPrice"])


class DepthMergeTests(unittest.TestCase):
    """
    Open interest does not come from the chain call — it is fetched one contract
    at a time from the depth endpoint, which allows about ten calls a second
    against a chain of forty-odd strikes. So a round can only refresh a slice,
    and the rest carry their last known figure.
    """

    def setUp(self):
        self.p = poller.OptionChainPoller.__new__(poller.OptionChainPoller)
        self.p._depth_cursor = 0
        self.p._last_depth = {}
        self.p._consecutive_failures = 0
        self.asked = []

        def fetch_depth(symbol, token):
            self.asked.append(symbol)
            return self.depths.get(symbol)

        self.p.fetch_depth = fetch_depth
        self.depths = {}

    @staticmethod
    def _rows(*strikes, spot=57000.0):
        return [
            {"symbol": f"NSE:BANKNIFTY26SEP{int(k)}CE", "strikePrice": float(k), "spotPrice": spot}
            for k in strikes
        ]

    def test_the_money_is_asked_about_before_the_wings(self):
        # A round-robin over the raw list would leave the at-the-money strike
        # waiting behind strikes two thousand points out, which is exactly the
        # row everyone is looking at.
        rows = self._rows(50000, 57100, 64000, 56900, spot=57000.0)

        order = self.p._depth_slice(rows, 57000.0)

        self.assertEqual(order[:2], ["NSE:BANKNIFTY26SEP56900CE", "NSE:BANKNIFTY26SEP57100CE"])

    def test_a_contract_never_seen_stays_null_rather_than_zero(self):
        rows = self._rows(57000)
        self.p._merge_open_interest(rows, "token")
        self.assertIsNone(rows[0].get("openInterest"))

    def test_a_contract_not_polled_this_round_carries_its_last_figure(self):
        rows = self._rows(57000)
        self.depths["NSE:BANKNIFTY26SEP57000CE"] = {
            "openInterest": 301980, "previousDayOpenInterest": 308940, "volume": 12000}

        self.p._merge_open_interest(rows, "token")
        self.assertEqual(rows[0]["openInterest"], 301980)

        # Next round the broker answers with nothing for it — the figure from a
        # few seconds ago is still true, and truer than a null.
        self.depths.clear()
        fresh = self._rows(57000)
        self.p._merge_open_interest(fresh, "token")
        self.assertEqual(fresh[0]["openInterest"], 301980)
        self.assertEqual(fresh[0]["previousDayOpenInterest"], 308940)

    def test_a_rate_limit_ends_the_round_without_losing_what_was_read(self):
        rows = self._rows(57000, 57100, 57200, 57300)
        self.depths["NSE:BANKNIFTY26SEP57000CE"] = {"openInterest": 111, "volume": 1}

        calls = {"n": 0}

        def fetch_depth(symbol, token):
            calls["n"] += 1
            if calls["n"] > 1:
                raise poller.RateLimited("slow down")
            return self.depths.get(symbol)

        self.p.fetch_depth = fetch_depth
        self.p._merge_open_interest(rows, "token")

        # It stopped asking rather than hammering through the rest...
        self.assertEqual(calls["n"], 2)
        # ...and the one contract it did read is merged in.
        self.assertEqual(rows[0]["openInterest"], 111)

    def test_the_cursor_advances_so_the_whole_chain_is_covered(self):
        strikes = [57000 + 100 * i for i in range(20)]
        rows = self._rows(*strikes, spot=57000.0)

        seen = set()
        for _ in range(20):
            seen.update(self.p._depth_slice(rows, 57000.0))

        self.assertEqual(len(seen), len(rows))


class BackoffTests(unittest.TestCase):
    """
    The usual failure is an expired daily token, which stays true for hours. At
    the normal interval that is the same line seven hundred times an hour.
    """

    def setUp(self):
        self.p = poller.OptionChainPoller.__new__(poller.OptionChainPoller)
        self.p._consecutive_failures = 0
        self.p._logged_shape = False

    def test_a_healthy_poller_uses_the_normal_interval(self):
        self.assertEqual(self.p.backoff_seconds(), poller.POLL_SECONDS)

    def test_the_wait_grows_while_the_broker_keeps_refusing(self):
        waits = []
        for n in range(1, 6):
            self.p._consecutive_failures = n
            waits.append(self.p.backoff_seconds())

        self.assertEqual(waits, sorted(waits), "backoff must not shrink while failing")
        self.assertGreater(waits[-1], waits[0])

    def test_the_wait_is_capped(self):
        self.p._consecutive_failures = 500
        self.assertLessEqual(self.p.backoff_seconds(), poller.MAX_BACKOFF_SECONDS)

    def test_success_returns_to_the_normal_interval(self):
        self.p._consecutive_failures = 6
        self.assertGreater(self.p.backoff_seconds(), poller.POLL_SECONDS)
        self.p._consecutive_failures = 0
        self.assertEqual(self.p.backoff_seconds(), poller.POLL_SECONDS)

    def test_the_first_failure_is_always_reported(self):
        with mock.patch("builtins.print") as printed:
            self.p._report_failure("BANKNIFTY", "HTTP 401")
            self.assertTrue(printed.called, "the first failure must be visible")

    def test_a_long_run_of_the_same_failure_is_not_repeated_every_round(self):
        printed = []
        with mock.patch("builtins.print", side_effect=lambda *a, **k: printed.append(a)):
            for n in range(60):
                self.p._consecutive_failures = n
                self.p._report_failure("BANKNIFTY", "HTTP 401")

        self.assertLess(len(printed), 12, "an hour of the same line buries everything else")
        self.assertGreater(len(printed), 0, "but it must never go completely silent")


if __name__ == "__main__":
    unittest.main()
