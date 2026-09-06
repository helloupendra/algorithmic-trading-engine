"""
A Fulcrum buyer must not exit the way a Fulcrum seller does.

The family's roll rule is "the ATM moved, so close and re-open". For the seller
that is right: premium was sold at the money and is no longer at the money.
Applied to the buyer it is exactly inverted — the ATM moving is the first
evidence of the move the buyer PAID for, so closing there takes every winner at
one strike step, while a quiet market (the buyer's losing case) is held through.

Cut the winners, hold the losers. These tests pin the corrected behaviour, and
pin that the seller was not touched while correcting it.
"""

import unittest

import _bootstrap  # noqa: F401

from strategies.base_strategy import OptionContract, StrategyInput
from strategies.fulcrum._exit_rules import long_exit_reason, resolve_long_exit
from strategies.variants import get_parameterised_strategies

VARIANTS = get_parameterised_strategies()
STEP = 100.0


def _contracts(strike):
    return {
        "atm_ce": OptionContract(symbol=f"CE_{strike:g}", underlying="BANKNIFTY",
                                 expiry_date="2026-09-30", strike_price=strike, option_type="CE"),
        "atm_pe": OptionContract(symbol=f"PE_{strike:g}", underlying="BANKNIFTY",
                                 expiry_date="2026-09-30", strike_price=strike, option_type="PE"),
    }


def _bar(strategy, state, spot, timestamp):
    atm = round(spot / STEP) * STEP
    return strategy.on_bar(state, StrategyInput(
        mode="LivePaper", timestamp_utc=timestamp, underlying="BANKNIFTY",
        spot_price=spot, atm_strike=atm, strike_step=STEP, lot_size=30,
        contracts=_contracts(atm),
    ))


def _kinds(signals):
    return [s.signal_type for s in signals]


class LongExitRuleTests(unittest.TestCase):
    """The decision itself, in isolation."""

    NOW = "2026-09-08T04:00:00Z"

    def test_a_one_step_move_is_not_enough_to_take_the_trade_off(self):
        # This is the seller's threshold, and the whole defect: at one step the
        # long straddle has barely started to work.
        self.assertIsNone(long_exit_reason(57000.0, self.NOW, 57100.0, "2026-09-08T04:05:00Z", STEP))

    def test_the_move_arriving_closes_the_group(self):
        reason = long_exit_reason(57000.0, self.NOW, 57200.0, "2026-09-08T04:05:00Z", STEP)
        self.assertIsNotNone(reason)
        self.assertIn("Target", reason)

    def test_it_reads_the_move_in_either_direction(self):
        down = long_exit_reason(57000.0, self.NOW, 56800.0, "2026-09-08T04:05:00Z", STEP)
        self.assertIsNotNone(down)
        self.assertIn("Target", down)

    def test_a_quiet_market_is_closed_on_time_rather_than_held(self):
        reason = long_exit_reason(57000.0, self.NOW, 57010.0, "2026-09-08T05:00:00Z", STEP)
        self.assertIsNotNone(reason)
        self.assertIn("Time stop", reason)

    def test_it_holds_while_the_move_may_still_come(self):
        self.assertIsNone(long_exit_reason(57000.0, self.NOW, 57010.0, "2026-09-08T04:30:00Z", STEP))

    def test_the_reason_says_what_happened_not_merely_that_it_closed(self):
        # Run history is read months later; "closed" alone cannot be audited.
        reason = long_exit_reason(57000.0, self.NOW, 57200.0, "2026-09-08T04:05:00Z", STEP)
        self.assertIn("57000", reason)
        self.assertIn("200", reason)

    def test_the_grid_sets_the_threshold_not_a_points_constant(self):
        # Two strikes on NIFTY's 50-grid is 100 points, not BANKNIFTY's 200.
        self.assertIsNotNone(long_exit_reason(22000.0, self.NOW, 22100.0, "2026-09-08T04:05:00Z", 50.0))
        self.assertIsNone(long_exit_reason(22000.0, self.NOW, 22050.0, "2026-09-08T04:05:00Z", 50.0))

    def test_missing_inputs_hold_rather_than_close_on_a_guess(self):
        for args in (
            (None, self.NOW, 57200.0, self.NOW, STEP),
            (57000.0, self.NOW, None, self.NOW, STEP),
            (57000.0, self.NOW, 57200.0, self.NOW, 0.0),
        ):
            self.assertIsNone(long_exit_reason(*args), args)

    def test_an_unreadable_timestamp_does_not_trigger_a_time_stop(self):
        self.assertIsNone(long_exit_reason(57000.0, "not a date", 57010.0, self.NOW, STEP))


class ExitParameterTests(unittest.TestCase):
    def test_defaults_apply_when_nothing_is_configured(self):
        resolved = resolve_long_exit(None)
        self.assertEqual(resolved["target_steps"], 2.0)
        self.assertEqual(resolved["max_hold_minutes"], 45.0)

    def test_a_run_can_override_both(self):
        resolved = resolve_long_exit({"target_steps": 3, "max_hold_minutes": 20})
        self.assertEqual(resolved["target_steps"], 3.0)
        self.assertEqual(resolved["max_hold_minutes"], 20.0)

    def test_junk_and_non_positive_values_fall_back_rather_than_disabling_the_exit(self):
        # A zero or a stray string must not silently leave a long straddle with
        # no exit at all — that is the failure this whole module exists to stop.
        for bad in ({"target_steps": 0}, {"target_steps": -1}, {"target_steps": "abc"}):
            self.assertEqual(resolve_long_exit(bad)["target_steps"], 2.0, bad)


class BuyVariantBehaviourTests(unittest.TestCase):
    """The rule as the strategy actually applies it, bar by bar."""

    def test_a_buy_variant_holds_through_the_atm_change_that_would_roll_a_seller(self):
        strategy = VARIANTS["FulcrumBuy"]()
        state = strategy.initialize_state()

        opened = _bar(strategy, state, 57000.0, "2026-09-08T04:00:00Z")
        self.assertEqual(_kinds(opened), ["OPEN_GROUP"])

        # The ATM has moved a full strike — a seller closes here.
        held = _bar(strategy, state, 57100.0, "2026-09-08T04:05:00Z")
        self.assertEqual(held, [], "the buy variant took its winner off at one strike step")

    def test_a_buy_variant_closes_once_the_move_arrives(self):
        strategy = VARIANTS["FulcrumBuy"]()
        state = strategy.initialize_state()
        _bar(strategy, state, 57000.0, "2026-09-08T04:00:00Z")

        signals = _bar(strategy, state, 57200.0, "2026-09-08T04:05:00Z")
        self.assertIn("CLOSE_GROUP", _kinds(signals))
        closing = next(s for s in signals if s.signal_type == "CLOSE_GROUP")
        self.assertIn("Target", closing.reason)

    def test_a_buy_variant_closes_a_position_that_is_only_paying_decay(self):
        strategy = VARIANTS["FulcrumBuy"]()
        state = strategy.initialize_state()
        _bar(strategy, state, 57000.0, "2026-09-08T04:00:00Z")

        signals = _bar(strategy, state, 57010.0, "2026-09-08T05:00:00Z")
        self.assertIn("CLOSE_GROUP", _kinds(signals))
        self.assertIn("Time stop", next(s for s in signals if s.signal_type == "CLOSE_GROUP").reason)

    def test_closing_reopens_so_the_strategy_keeps_running(self):
        strategy = VARIANTS["FulcrumBuy"]()
        state = strategy.initialize_state()
        _bar(strategy, state, 57000.0, "2026-09-08T04:00:00Z")

        signals = _bar(strategy, state, 57200.0, "2026-09-08T04:05:00Z")
        self.assertEqual(_kinds(signals), ["CLOSE_GROUP", "OPEN_GROUP"])

    def test_the_reopened_group_starts_its_own_clock_and_strike(self):
        strategy = VARIANTS["FulcrumBuy"]()
        state = strategy.initialize_state()
        _bar(strategy, state, 57000.0, "2026-09-08T04:00:00Z")
        _bar(strategy, state, 57200.0, "2026-09-08T04:05:00Z")

        self.assertEqual(state["group_entry_strike"], 57200.0)
        self.assertEqual(state["group_entry_utc"], "2026-09-08T04:05:00Z")
        # ...so the fresh group is not immediately closed by the old one's move.
        self.assertEqual(_bar(strategy, state, 57250.0, "2026-09-08T04:07:00Z"), [])

    def test_a_buy_variant_never_sells_when_it_closes(self):
        strategy = VARIANTS["FulcrumBuy"]()
        state = strategy.initialize_state()
        _bar(strategy, state, 57000.0, "2026-09-08T04:00:00Z")
        signals = _bar(strategy, state, 57200.0, "2026-09-08T04:05:00Z")

        closing = next(s for s in signals if s.signal_type == "CLOSE_GROUP")
        # Closing a long IS a sell; the OPENING legs are what must never be one.
        opening = next(s for s in signals if s.signal_type == "OPEN_GROUP")
        self.assertTrue(all(leg["side"] == "BUY" for leg in opening.legs))
        self.assertTrue(all(leg["side"] == "SELL" for leg in closing.legs))


class SellVariantUnchangedTests(unittest.TestCase):
    """The correction must not have reached the seller."""

    def test_a_sell_variant_still_rolls_on_any_atm_change(self):
        strategy = VARIANTS["Fulcrum"]()
        state = strategy.initialize_state()
        _bar(strategy, state, 57000.0, "2026-09-08T04:00:00Z")

        signals = _bar(strategy, state, 57100.0, "2026-09-08T04:05:00Z")
        self.assertEqual(_kinds(signals), ["CLOSE_GROUP", "OPEN_GROUP"])
        self.assertIn("ATM shifted", next(s for s in signals if s.signal_type == "CLOSE_GROUP").reason)

    def test_a_sell_variant_is_not_subject_to_the_time_stop(self):
        strategy = VARIANTS["Fulcrum"]()
        state = strategy.initialize_state()
        _bar(strategy, state, 57000.0, "2026-09-08T04:00:00Z")

        # Hours later, ATM unmoved: the seller is being paid and stays put.
        self.assertEqual(_bar(strategy, state, 57010.0, "2026-09-08T09:00:00Z"), [])


class AdjustingVariantTimeStopTests(unittest.TestCase):
    """
    The multi-straddle variants adjust when the spot MOVES past their threshold.
    For a buyer that already realises a winner and needs no correction — but it
    means a still market never reaches any exit at all, and a long straddle in a
    still market is the one position that is guaranteed to be losing.
    """

    ADJUSTING = ["Fulcrum2StraddleBuy20", "Fulcrum3StraddleBuy175",
                 "FulcrumMultiBuy50", "FulcrumQtyAdjustmentBuy"]

    SELLERS = ["Fulcrum2Straddle20", "Fulcrum3Straddle175",
               "FulcrumMulti50", "FulcrumQtyAdjustment"]

    @staticmethod
    def _open(name):
        strategy = VARIANTS[name]()
        state = strategy.initialize_state()
        _bar(strategy, state, 57342.0, "2026-09-08T04:00:00Z")
        return strategy, state

    def test_a_still_market_eventually_closes_a_long_group(self):
        for name in self.ADJUSTING:
            with self.subTest(name):
                strategy, state = self._open(name)
                self.assertIsNotNone(state["current_group_id"], f"{name} opened nothing")

                later = _bar(strategy, state, 57343.0, "2026-09-08T05:00:00Z")
                self.assertEqual(_kinds(later), ["CLOSE_GROUP"], f"{name} held a decaying position")
                self.assertIn("Time stop", later[0].reason)

    def test_a_seller_is_never_closed_by_the_time_stop(self):
        for name in self.SELLERS:
            with self.subTest(name):
                strategy, state = self._open(name)
                # Hours in a still market: exactly what the seller wants.
                later = _bar(strategy, state, 57343.0, "2026-09-08T09:00:00Z")
                self.assertNotIn("CLOSE_GROUP", _kinds(later), f"{name} closed a winning short")

    def test_the_position_is_still_held_before_the_stop(self):
        for name in self.ADJUSTING:
            with self.subTest(name):
                strategy, state = self._open(name)
                early = _bar(strategy, state, 57343.0, "2026-09-08T04:30:00Z")
                self.assertNotIn("CLOSE_GROUP", _kinds(early))

    def test_the_close_reverses_every_leg_that_was_held(self):
        for name in self.ADJUSTING:
            with self.subTest(name):
                strategy, state = self._open(name)
                held = {leg["symbol"]: leg["side"] for leg in state["current_group_legs"]}

                closed = _bar(strategy, state, 57343.0, "2026-09-08T05:00:00Z")[0]
                self.assertEqual(len(closed.legs), len(held), f"{name} closed a different leg count")
                for leg in closed.legs:
                    opposite = "SELL" if held[leg["symbol"]] == "BUY" else "BUY"
                    self.assertEqual(leg["side"], opposite, f"{name} {leg['symbol']}")

    def test_state_is_cleared_so_no_phantom_position_is_adjusted(self):
        # A group closed in the market but still recorded in state would have
        # the next bar adjusting legs that are no longer held.
        for name in self.ADJUSTING:
            with self.subTest(name):
                strategy, state = self._open(name)
                _bar(strategy, state, 57343.0, "2026-09-08T05:00:00Z")

                self.assertIsNone(state["current_group_id"])
                self.assertEqual(state["current_group_legs"], [])
                self.assertIsNone(state["group_entry_utc"])
                for key in ("st0", "st1", "st2"):
                    self.assertEqual(state.get(key, 0), 0, f"{name} left {key} set")

    def test_it_opens_again_afterwards_rather_than_stopping_for_the_day(self):
        for name in self.ADJUSTING:
            with self.subTest(name):
                strategy, state = self._open(name)
                _bar(strategy, state, 57343.0, "2026-09-08T05:00:00Z")

                resumed = _bar(strategy, state, 57550.0, "2026-09-08T05:05:00Z")
                self.assertIn("OPEN_GROUP", _kinds(resumed), f"{name} never traded again")
                self.assertEqual(state["group_entry_utc"], "2026-09-08T05:05:00Z")


if __name__ == "__main__":
    unittest.main()
