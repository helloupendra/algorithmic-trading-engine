"""
Pricing a signal's legs within one shared budget (core/leg_pricing.py).

Replaces a per-leg poll of up to 35 seconds EACH, run inside the tick loop. The
properties worth pinning are: one budget for the whole signal, never invent a
price, never trust a stale quote, and never sleep past the deadline.
"""

import unittest

import _bootstrap  # noqa: F401

from core.leg_pricing import (
    apply_quotes,
    leg_price,
    resolve_leg_prices,
    symbols_needing_price,
    usable_quote,
    wait_plan,
)

NOW = 1_788_600_000.0


def fresh(price, seconds_old=1):
    """A quote stamped `seconds_old` before NOW."""
    from datetime import datetime, timezone
    stamped = datetime.fromtimestamp(NOW - seconds_old, tz=timezone.utc)
    return {"lastTradedPrice": price, "updatedUtc": stamped.isoformat().replace("+00:00", "Z")}


def leg(symbol, price=None):
    return {"symbol": symbol, "side": "SELL", "quantity": 1, "price": price}


class LegPriceTests(unittest.TestCase):
    def test_zero_is_not_a_price(self):
        """The whole point: a zero must be treated as missing, never as free."""
        self.assertIsNone(leg_price(leg("X", 0)))
        self.assertIsNone(leg_price(leg("X", 0.0)))
        self.assertIsNone(leg_price(leg("X", None)))
        self.assertIsNone(leg_price(leg("X", "not a number")))
        self.assertEqual(leg_price(leg("X", 12.5)), 12.5)

    def test_missing_symbols_are_distinct_and_ordered(self):
        legs = [leg("A"), leg("B", 10), leg("A"), leg("C"), leg("", None)]
        self.assertEqual(symbols_needing_price(legs), ["A", "C"])


class QuoteFreshnessTests(unittest.TestCase):
    def test_a_fresh_positive_quote_is_usable(self):
        self.assertEqual(usable_quote(fresh(41.5), NOW), 41.5)

    def test_a_stale_quote_is_refused(self):
        """LiveQuotesLatest is never purged — Friday's close must not price Monday."""
        self.assertIsNone(usable_quote(fresh(41.5, seconds_old=3 * 86400), NOW))

    def test_a_zero_or_negative_quote_is_refused(self):
        self.assertIsNone(usable_quote(fresh(0), NOW))
        self.assertIsNone(usable_quote(fresh(-3), NOW))

    def test_an_unstamped_quote_is_accepted(self):
        """Refusing every unstamped quote would reject far more good prices than stale ones."""
        self.assertEqual(usable_quote({"lastTradedPrice": 7.25}, NOW), 7.25)

    def test_an_unreadable_stamp_does_not_disqualify_the_price(self):
        self.assertEqual(usable_quote({"lastTradedPrice": 7.25, "updatedUtc": "junk"}, NOW), 7.25)

    def test_nothing_at_all(self):
        self.assertIsNone(usable_quote(None, NOW))
        self.assertIsNone(usable_quote({}, NOW))


class ApplyQuotesTests(unittest.TestCase):
    def test_fills_what_it_can_and_reports_the_rest(self):
        legs = [leg("A"), leg("B"), leg("C", 99)]
        still = apply_quotes(legs, {"A": fresh(10.0)}, NOW)
        self.assertEqual(legs[0]["price"], 10.0)
        self.assertIsNone(legs[1]["price"])
        self.assertEqual(legs[2]["price"], 99, "an already-priced leg is left alone")
        self.assertEqual(still, ["B"])

    def test_a_stale_quote_leaves_the_leg_unpriced(self):
        legs = [leg("A")]
        still = apply_quotes(legs, {"A": fresh(10.0, seconds_old=99999)}, NOW)
        self.assertIsNone(legs[0]["price"])
        self.assertEqual(still, ["A"])


class WaitPlanTests(unittest.TestCase):
    def test_sleeps_the_interval_while_there_is_budget(self):
        self.assertEqual(wait_plan(0.0, 10.0, 0.25), 0.25)

    def test_never_sleeps_past_the_deadline(self):
        self.assertAlmostEqual(wait_plan(9.9, 10.0, 0.25), 0.1)

    def test_stops_when_the_budget_is_spent(self):
        self.assertIsNone(wait_plan(10.0, 10.0, 0.25))
        self.assertIsNone(wait_plan(11.0, 10.0, 0.25))

    def test_a_zero_budget_means_one_attempt_and_no_sleeping(self):
        """What a CLOSE_GROUP gets: getting flat must not queue behind an entry."""
        self.assertIsNone(wait_plan(0.0, 0.0, 0.25))


class ResolveTests(unittest.TestCase):
    def setUp(self):
        self.clock = [NOW]
        self.slept = []

    def now(self):
        return self.clock[0]

    def sleep(self, seconds):
        self.slept.append(seconds)
        self.clock[0] += seconds

    def test_already_priced_legs_never_poll_at_all(self):
        calls = []
        legs = [leg("A", 10), leg("B", 20)]
        missing = resolve_leg_prices(
            legs, lambda: calls.append(1) or {}, self.now, self.sleep, budget_seconds=10
        )
        self.assertEqual(missing, [])
        self.assertEqual(calls, [], "no round trip when there is nothing to ask for")
        self.assertEqual(self.slept, [])

    def test_returns_as_soon_as_every_leg_is_priced(self):
        legs = [leg("A"), leg("B")]
        missing = resolve_leg_prices(
            legs,
            lambda: {"A": fresh(1.0), "B": fresh(2.0)},
            self.now, self.sleep, budget_seconds=10,
        )
        self.assertEqual(missing, [])
        self.assertEqual(self.slept, [], "priced on the first look, so it never sleeps")

    def test_one_budget_covers_all_legs_not_one_each(self):
        """Four legs and a 1s budget must cost ~1s in total, not 4s."""
        legs = [leg(s) for s in ("A", "B", "C", "D")]
        missing = resolve_leg_prices(
            legs, lambda: {}, self.now, self.sleep,
            budget_seconds=1.0, poll_interval=0.25,
        )
        self.assertEqual(missing, ["A", "B", "C", "D"])
        self.assertAlmostEqual(sum(self.slept), 1.0, places=6)

    def test_a_partial_fill_still_reports_the_rest(self):
        legs = [leg("A"), leg("B")]
        missing = resolve_leg_prices(
            legs, lambda: {"A": fresh(5.0)}, self.now, self.sleep,
            budget_seconds=0.5, poll_interval=0.25,
        )
        self.assertEqual(legs[0]["price"], 5.0)
        self.assertEqual(missing, ["B"])

    def test_a_price_that_arrives_mid_wait_is_picked_up(self):
        legs = [leg("A")]
        rounds = {"n": 0}

        def fetch():
            rounds["n"] += 1
            return {"A": fresh(3.5)} if rounds["n"] >= 3 else {}

        missing = resolve_leg_prices(
            legs, fetch, self.now, self.sleep, budget_seconds=10, poll_interval=0.25
        )
        self.assertEqual(missing, [])
        self.assertEqual(legs[0]["price"], 3.5)
        self.assertEqual(len(self.slept), 2, "two sleeps, then the third look succeeds")

    def test_a_failing_fetch_costs_a_round_not_the_run(self):
        legs = [leg("A")]

        def fetch():
            raise RuntimeError("API down")

        missing = resolve_leg_prices(
            legs, fetch, self.now, self.sleep, budget_seconds=0.5, poll_interval=0.25
        )
        self.assertEqual(missing, ["A"], "reported as unpriced, not raised")

    def test_housekeeping_runs_during_the_wait(self):
        """The feed watchdog must not go blind while a signal waits for prices."""
        beats = []
        legs = [leg("A")]
        resolve_leg_prices(
            legs, lambda: {}, self.now, self.sleep,
            budget_seconds=1.0, poll_interval=0.25,
            on_poll=lambda: beats.append(1),
        )
        self.assertGreaterEqual(len(beats), 4)

    def test_a_throwing_housekeeping_does_not_break_pricing(self):
        legs = [leg("A")]

        def boom():
            raise RuntimeError("status print failed")

        missing = resolve_leg_prices(
            legs, lambda: {"A": fresh(2.0)}, self.now, self.sleep,
            budget_seconds=1.0, on_poll=boom,
        )
        self.assertEqual(missing, [])

    def test_it_never_invents_a_price(self):
        """The API refuses an unpriced opening group. Python must not paper over that."""
        legs = [leg("A"), leg("B")]
        resolve_leg_prices(
            legs, lambda: {}, self.now, self.sleep, budget_seconds=0.5
        )
        self.assertIsNone(legs[0]["price"])
        self.assertIsNone(legs[1]["price"])


if __name__ == "__main__":
    unittest.main()
