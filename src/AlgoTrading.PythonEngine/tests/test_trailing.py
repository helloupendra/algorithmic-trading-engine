"""
Trailing stop-loss at the three risk levels.

`TrailStateTests` cover the state machine on its own (arming with and without a
trigger, peak tracking, the exact `peak - trail` boundary, reset and prune);
the engine classes drive the same rules through a full replay, on BUY and SELL
legs, in points and in percent, and check the reason strings the live guard has
to match character for character.
"""

import unittest
from typing import Any, Callable, Dict, List

import _bootstrap  # noqa: F401

from backtest.timeutil import iso_utc
from backtest.trailing import TrailLevels, TrailState, TrailTracker
from test_engine import (
    CE,
    CE_NAME,
    DAY1,
    LOT_SIZE,
    PE,
    PE_NAME,
    EngineRunner,
    bar_start,
    close_signals,
    make_api,
    open_ce,
    open_legs,
    reason_of,
    risk,
    run_row,
)

STRADDLE_BUY = [("atm_ce", "BUY", 1), ("atm_pe", "BUY", 1)]


def series(values: List[float]) -> Callable[[int], float]:
    """Price of bar i, holding the last value once the script runs out."""
    return lambda i: values[i] if i < len(values) else values[-1]


class TrailStateTests(unittest.TestCase):
    def test_no_trigger_arms_as_soon_as_the_value_is_positive(self):
        state = TrailState()
        self.assertIsNone(state.update(0.0, 10.0, None))       # flat is not "in profit"
        self.assertFalse(state.armed)
        self.assertIsNone(state.update(-5.0, 10.0, None))
        self.assertFalse(state.armed)
        self.assertIsNone(state.update(4.0, 10.0, None))
        self.assertTrue(state.armed)
        self.assertEqual(state.peak, 4.0)

    def test_a_trigger_arms_when_the_value_reaches_it_exactly(self):
        state = TrailState()
        self.assertIsNone(state.update(19.9, 10.0, 20.0))
        self.assertFalse(state.armed)
        self.assertIsNone(state.update(20.0, 10.0, 20.0))
        self.assertTrue(state.armed)
        self.assertEqual(state.peak, 20.0)

    def test_the_peak_only_ever_rises(self):
        state = TrailState()
        state.update(10.0, 100.0, None)
        state.update(30.0, 100.0, None)
        state.update(12.0, 100.0, None)
        self.assertEqual(state.peak, 30.0)

    def test_trips_at_exactly_peak_minus_trail_and_not_a_tick_earlier(self):
        state = TrailState()
        state.update(20.0, 10.0, None)
        self.assertIsNone(state.update(10.1, 10.0, None))
        trip = state.update(10.0, 10.0, None)
        self.assertIsNotNone(trip)
        self.assertEqual((trip.value, trip.peak, trip.trail, trip.drawdown), (10.0, 20.0, 10.0, 10.0))

    def test_a_value_below_the_trigger_never_trips_however_far_it_falls(self):
        state = TrailState()
        for value in (5.0, 15.0, 19.0, -50.0):
            self.assertIsNone(state.update(value, 1.0, 20.0))
        self.assertFalse(state.armed)

    def test_tracker_keeps_subjects_apart_and_ignores_unset_rules(self):
        tracker = TrailTracker()
        self.assertIsNone(tracker.evaluate("a", 100.0, None))          # no trail configured
        self.assertIsNone(tracker.evaluate("a", 100.0, 0))
        self.assertIsNone(tracker.evaluate("a", None, 10.0))           # no mark yet
        self.assertEqual(len(tracker), 0)

        tracker.evaluate("a", 20.0, 10.0)
        tracker.evaluate("b", 5.0, 10.0)
        self.assertEqual(tracker.peak("a"), 20.0)
        self.assertEqual(tracker.peak("b"), 5.0)
        self.assertIsNone(tracker.evaluate("b", 4.0, 10.0))            # b has not given back 10 yet
        self.assertIsNotNone(tracker.evaluate("a", 10.0, 10.0))

    def test_a_rule_change_resets_the_peak_so_the_trail_re_arms_from_here(self):
        tracker = TrailTracker()
        tracker.evaluate("run", 5000.0, 1500.0)
        self.assertEqual(tracker.peak("run"), 5000.0)
        # The user edits the rule mid-run: peaks are dropped, the trail re-arms
        # from the current P&L, so 3,500 is no longer a 1,500 give-back.
        tracker.reset("run")
        self.assertFalse(tracker.armed("run"))
        self.assertIsNone(tracker.evaluate("run", 3500.0, 1500.0))
        self.assertEqual(tracker.peak("run"), 3500.0)
        self.assertIsNotNone(tracker.evaluate("run", 2000.0, 1500.0))

        tracker.reset()
        self.assertEqual(len(tracker), 0)

    def test_prune_drops_subjects_that_are_gone(self):
        levels = TrailLevels()
        levels.leg_points.evaluate(("g1", CE), 20.0, 5.0)
        levels.leg_percent.evaluate(("g1", CE), 20.0, 5.0)
        levels.group.evaluate("g1", 500.0, 100.0)
        levels.prune_legs([("g2", PE)])
        levels.prune_groups(["g2"])
        self.assertEqual(len(levels.leg_points), 0)
        self.assertEqual(len(levels.leg_percent), 0)
        self.assertEqual(len(levels.group), 0)


class LegTrailingTests(EngineRunner, unittest.TestCase):
    """Leg level: closes that leg only, the run goes on."""

    def counts(self, summary: Dict[str, Any]) -> Dict[str, int]:
        return {k: summary[k] for k in ("legStops", "legTargets", "legTrailStops",
                                        "groupStops", "groupTargets", "groupTrailStops")}

    def test_buy_leg_gives_back_the_trail_in_points(self):
        # Long CE 100 -> 120 (+20) -> back to 110 (+10): peak 20, trail 10, trips at exactly peak - trail.
        api = make_api(series([100.0, 110.0, 120.0, 118.0, 114.0, 110.0]), pe_price=lambda i: 80.0)
        outcome, _ = self.run_engine(api, {0: open_legs(STRADDLE_BUY)},
                                     run_row(DAY1, DAY1, risk=risk(leg={"trailStopLossPoints": 10})))

        self.assertEqual(outcome.status, "Completed")
        self.assertIsNone(outcome.stop_reason)                 # the run continued
        self.assertEqual(outcome.summary["barsProcessed"], 75)
        closes = close_signals(api)
        self.assertEqual(len(closes), 2)                       # the trailing exit, then the EOD square-off of the PE
        trail_close = closes[0]
        self.assertEqual(trail_close["timestampUtc"], iso_utc(bar_start(DAY1, 9, 40)))
        self.assertEqual(trail_close["legs"], [{"symbol": CE, "side": "SELL", "quantity": 1, "price": 110.0}])
        self.assertEqual(reason_of(trail_close),
                         f"Leg trailing stop hit: {CE_NAME} +10.0 pts fell 10.0 pts from peak +20.0 pts (trail 10 pts)")
        self.assertEqual(closes[1]["legs"][0]["symbol"], PE)   # the flat PE never armed and stayed open
        self.assertAlmostEqual(outcome.ledger.closed[0].realized, 10.0 * LOT_SIZE)
        self.assertEqual(self.counts(outcome.summary)["legTrailStops"], 1)
        self.assertTrue(any(n.startswith("Risk rules closed 1 leg (trailing stop)")
                            for n in outcome.summary["dataNotes"]))

    def test_sell_leg_gives_back_the_trail_in_percent(self):
        # Short PE 80 -> 72 (+10%) -> 76 (+5%): peak 10%, trail 5%, trips at bar 4.
        api = make_api(lambda i: 100.0, pe_price=series([80.0, 76.0, 72.0, 74.0, 76.0]))
        outcome, _ = self.run_engine(api, {0: open_legs([("atm_ce", "BUY", 1), ("atm_pe", "SELL", 1)])},
                                     run_row(DAY1, DAY1, risk=risk(leg={"trailStopLossPercent": 5})))
        closes = close_signals(api)
        self.assertEqual(closes[0]["timestampUtc"], iso_utc(bar_start(DAY1, 9, 35)))
        self.assertEqual(closes[0]["legs"], [{"symbol": PE, "side": "BUY", "quantity": 1, "price": 76.0}])
        self.assertEqual(reason_of(closes[0]),
                         f"Leg trailing stop hit: {PE_NAME} +5.0% fell 5.0% from peak +10.0% (trail 5%)")
        self.assertEqual(closes[1]["legs"][0]["symbol"], CE)
        self.assertEqual(self.counts(outcome.summary)["legTrailStops"], 1)

    def test_a_leg_that_never_reaches_the_trigger_is_never_trailed_out(self):
        # Peak is +20 pts; the trigger asks for 25, so the trail never arms and
        # the leg rides all the way back down to 60 without a trailing exit.
        api = make_api(series([100.0, 120.0, 110.0, 90.0, 60.0]), pe_price=lambda i: 80.0)
        outcome, _ = self.run_engine(
            api, {0: open_ce(1)},
            run_row(DAY1, DAY1, risk=risk(leg={"trailStopLossPoints": 10, "trailTriggerPoints": 25})))
        closes = close_signals(api)
        self.assertEqual(len(closes), 1)
        self.assertIn("End-of-day square-off", reason_of(closes[0]))
        self.assertEqual(self.counts(outcome.summary)["legTrailStops"], 0)
        self.assertFalse(any(n.startswith("Risk rules") for n in outcome.summary["dataNotes"]))

    def test_the_trigger_arms_the_trail_and_the_leg_then_trips(self):
        # Same prices, trigger 20: bar 2 reaches exactly +20 and arms it.
        api = make_api(series([100.0, 110.0, 120.0, 118.0, 114.0, 110.0]), pe_price=lambda i: 80.0)
        outcome, _ = self.run_engine(
            api, {0: open_ce(1)},
            run_row(DAY1, DAY1, risk=risk(leg={"trailStopLossPoints": 10, "trailTriggerPoints": 20})))
        closes = close_signals(api)
        self.assertEqual(closes[0]["timestampUtc"], iso_utc(bar_start(DAY1, 9, 40)))
        self.assertEqual(reason_of(closes[0]),
                         f"Leg trailing stop hit: {CE_NAME} +10.0 pts fell 10.0 pts from peak +20.0 pts (trail 10 pts)")
        self.assertEqual(self.counts(outcome.summary)["legTrailStops"], 1)

    def test_the_fixed_stop_loss_is_checked_before_the_trailing_stop(self):
        # Bar 2 would satisfy both rules (-10 pts is a 30-point give-back from
        # the +20 peak); the fixed stop is the one reported.
        api = make_api(series([100.0, 120.0, 90.0]), pe_price=lambda i: 80.0)
        outcome, _ = self.run_engine(
            api, {0: open_ce(1)},
            run_row(DAY1, DAY1, risk=risk(leg={"stopLossPoints": 5, "trailStopLossPoints": 10})))
        closes = close_signals(api)
        self.assertEqual(closes[0]["timestampUtc"], iso_utc(bar_start(DAY1, 9, 25)))
        self.assertEqual(reason_of(closes[0]), f"Leg stop-loss hit: {CE_NAME} −10.0 pts (−10.0%) ≤ −5 pts")
        self.assertEqual(self.counts(outcome.summary), {"legStops": 1, "legTargets": 0, "legTrailStops": 0,
                                                        "groupStops": 0, "groupTargets": 0, "groupTrailStops": 0})

    def test_points_and_percent_keep_separate_peaks(self):
        # Points trail 30 never trips; the percent trail (5% of an 80 entry = 4
        # points) does, so the percent rule is the one that closes the leg.
        api = make_api(lambda i: 100.0, pe_price=series([80.0, 76.0, 72.0, 74.0, 76.0]))
        outcome, _ = self.run_engine(
            api, {0: open_legs([("atm_ce", "BUY", 1), ("atm_pe", "SELL", 1)])},
            run_row(DAY1, DAY1, risk=risk(leg={"trailStopLossPoints": 30, "trailStopLossPercent": 5})))
        closes = close_signals(api)
        self.assertEqual(closes[0]["legs"][0]["symbol"], PE)
        self.assertTrue(reason_of(closes[0]).endswith("(trail 5%)"), reason_of(closes[0]))
        self.assertEqual(self.counts(outcome.summary)["legTrailStops"], 1)


class GroupAndOverallTrailingTests(EngineRunner, unittest.TestCase):
    def test_group_trailing_stop_closes_that_group_and_the_run_goes_on(self):
        # g1 = long CE: P&L 0 -> 300 -> 600 -> 540 -> 300 (lot size 30).
        # g2 = short PE, flat, untouched until the EOD square-off.
        api = make_api(series([100.0, 110.0, 120.0, 118.0, 110.0]), pe_price=lambda i: 80.0)
        outcome, _ = self.run_engine(api, {0: open_ce(1), 1: open_legs([("atm_pe", "SELL", 1)], "g2")},
                                     run_row(DAY1, DAY1, risk=risk(group={"trailStopLoss": 300})))

        self.assertIsNone(outcome.stop_reason)
        self.assertEqual(outcome.summary["barsProcessed"], 75)
        closes = close_signals(api)
        self.assertEqual(closes[0]["groupId"], "g1")
        self.assertEqual(closes[0]["timestampUtc"], iso_utc(bar_start(DAY1, 9, 35)))
        self.assertEqual(reason_of(closes[0]),
                         "Group trailing stop hit: g1 P&L ₹300 fell ₹300 from peak ₹600 (trail ₹300)")
        self.assertEqual(closes[1]["groupId"], "g2")
        self.assertEqual(outcome.summary["groupTrailStops"], 1)
        self.assertEqual(outcome.summary["groupStops"], 0)
        self.assertTrue(any("1 group (trailing stop)" in n for n in outcome.summary["dataNotes"]))

    def test_group_trailing_re_arms_for_a_reused_group_id(self):
        # g1 trails out at bar 4; the strategy re-opens g1 at bar 5 and the new
        # position starts from a fresh peak instead of the old one.
        api = make_api(series([100.0, 110.0, 120.0, 118.0, 110.0, 110.0, 120.0, 118.0, 110.0]),
                       pe_price=lambda i: 80.0)
        outcome, _ = self.run_engine(api, {0: open_ce(1), 5: open_ce(1)},
                                     run_row(DAY1, DAY1, risk=risk(group={"trailStopLoss": 300})))
        closes = close_signals(api)
        self.assertEqual([c["groupId"] for c in closes], ["g1", "g1"])
        self.assertEqual([c["timestampUtc"] for c in closes],
                         [iso_utc(bar_start(DAY1, 9, 35)), iso_utc(bar_start(DAY1, 9, 55))])
        self.assertEqual(outcome.summary["groupTrailStops"], 2)

    def test_overall_trailing_stop_flattens_everything_and_ends_the_run(self):
        api = make_api(series([100.0, 110.0, 120.0, 118.0, 110.0]), pe_price=lambda i: 80.0)
        outcome, strategy = self.run_engine(
            api, {0: open_ce(1), 6: open_ce(1, "never")},
            run_row(DAY1, DAY1, risk=risk(overall={"trailStopLoss": 300, "trailTrigger": 500})))

        self.assertEqual(outcome.status, "Completed")
        self.assertEqual(outcome.stop_reason,
                         "Trailing stop hit: P&L ₹300 fell ₹300 from peak ₹600 (trail ₹300)")
        self.assertEqual(outcome.summary["stopReason"], outcome.stop_reason)
        self.assertTrue(outcome.summary["overallTrailStop"])
        self.assertEqual(outcome.summary["barsProcessed"], 5)
        self.assertEqual(len(strategy.inputs), 5)              # bar 6's entry never happened
        self.assertFalse(outcome.ledger.has_open())
        self.assertAlmostEqual(outcome.ledger.realized_pnl(), 300.0)
        self.assertTrue(any(n.startswith("Risk rules ended the run on the overall trailing stop")
                            for n in outcome.summary["dataNotes"]))

    def test_overall_trailing_never_arms_below_its_trigger(self):
        api = make_api(series([100.0, 110.0, 120.0, 118.0, 110.0]), pe_price=lambda i: 80.0)
        outcome, _ = self.run_engine(
            api, {0: open_ce(1)},
            run_row(DAY1, DAY1, risk=risk(overall={"trailStopLoss": 300, "trailTrigger": 700})))
        self.assertIsNone(outcome.stop_reason)
        self.assertFalse(outcome.summary["overallTrailStop"])
        self.assertEqual(outcome.summary["barsProcessed"], 75)

    def test_the_overall_stop_loss_is_checked_before_the_overall_trail(self):
        # Bar 3 satisfies both: total P&L -600 is past the 500 stop and a 1,200
        # give-back from the +600 peak. The fixed stop is the one reported.
        api = make_api(series([100.0, 110.0, 120.0, 80.0]), pe_price=lambda i: 80.0)
        outcome, _ = self.run_engine(
            api, {0: open_ce(1)},
            run_row(DAY1, DAY1, risk=risk(overall={"stopLoss": 500, "trailStopLoss": 300})))
        self.assertEqual(outcome.stop_reason, "Stop loss hit: P&L −600 ≤ −500")
        self.assertFalse(outcome.summary["overallTrailStop"])

    def test_leg_group_and_overall_trails_report_in_that_order_on_one_bar(self):
        # Bar 4: the leg trail closes the CE, the group trail then closes the
        # rest of g1, and the overall trail ends the run — all with their own wording.
        api = make_api(series([100.0, 110.0, 120.0, 118.0, 110.0]), pe_price=lambda i: 80.0)
        logs: List[str] = []
        outcome, _ = self.run_engine(
            api, {0: open_legs(STRADDLE_BUY)},
            run_row(DAY1, DAY1, risk=risk(overall={"trailStopLoss": 300}, group={"trailStopLoss": 300},
                                          leg={"trailStopLossPoints": 10})),
            log=logs.append)
        closes = close_signals(api)
        self.assertEqual(closes[0]["legs"][0]["symbol"], CE)
        self.assertTrue(reason_of(closes[0]).startswith("Leg trailing stop hit"), reason_of(closes[0]))
        self.assertEqual(closes[1]["legs"][0]["symbol"], PE)
        self.assertTrue(reason_of(closes[1]).startswith("Group trailing stop hit"), reason_of(closes[1]))
        self.assertTrue(outcome.stop_reason.startswith("Trailing stop hit"), outcome.stop_reason)
        self.assertEqual(outcome.summary["legTrailStops"], 1)
        self.assertEqual(outcome.summary["groupTrailStops"], 1)
        self.assertTrue(outcome.summary["overallTrailStop"])

        risk_lines = [line for line in logs if line.startswith("[RISK]")]
        self.assertEqual(len(risk_lines), 2)
        config = [line for line in logs if line.startswith("[CONFIG]") and "risk=" in line][0]
        self.assertIn("overall SL — · target — · trail ₹300", config)
        self.assertIn("leg SL — · target — · trail 10 pts", config)


if __name__ == "__main__":
    unittest.main()
