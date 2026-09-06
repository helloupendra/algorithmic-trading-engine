"""
Fulcrum variants must mean the same thing on every underlying.

Every one of these strategies was written in BANKNIFTY's numbers — 100-point
strikes, 2,000-point wings, thresholds of 20/50/70/175 points. Run unchanged on
NIFTY they traded a grid that does not exist and hedged 8% out of the money; on
a ₹150 stock they were nonsense.

Two properties are pinned here. First, BANKNIFTY still does exactly what it did
— this is a refactor, not a change of strategy. Second, the same strategy on a
different grid makes the *same decision* at the equivalent distance.
"""

import pathlib
import unittest

import _bootstrap  # noqa: F401

from strategies.base_strategy import OptionContract, StrategyInput
from strategies.variants import get_parameterised_strategies

VARIANTS = get_parameterised_strategies()

# Variants that name explicit strikes in their legs. "Fulcrum" trades only the
# ATM contracts the platform hands it, so it has no strikes of its own to check.
STRIKE_NAMING = [
    "Fulcrum2Straddle20",
    "Fulcrum3Straddle175",
    "FulcrumMulti50",
    "FulcrumMulti70",
    "FulcrumMulti90",
    "FulcrumQtyAdjustment",
]


def contract(symbol, strike, option_type):
    return OptionContract(
        symbol=symbol, underlying="X", expiry_date="2026-09-30",
        strike_price=strike, option_type=option_type,
    )


def one_bar(name, underlying, spot, step):
    strategy = VARIANTS[name]()
    state = strategy.initialize_state()
    inp = StrategyInput(
        mode="LivePaper", timestamp_utc="2026-09-08T04:00:00Z",
        underlying=underlying, spot_price=spot, atm_strike=None,
        strike_step=step, lot_size=30,
        contracts={
            "atm_ce": contract("CE_SYM", spot, "CE"),
            "atm_pe": contract("PE_SYM", spot, "PE"),
        },
    )
    return strategy.on_bar(state, inp)


def strikes_of(signals):
    out = set()
    for sig in signals:
        for leg in sig.legs:
            symbol = leg["symbol"]
            if "_" in symbol:
                try:
                    out.add(float(symbol.rsplit("_", 1)[1]))
                except ValueError:
                    pass
    return out


class BankniftyRegressionTests(unittest.TestCase):
    """A refactor must not move a single BANKNIFTY strike."""

    def test_two_straddle_strikes_and_wings_are_unchanged(self):
        strikes = strikes_of(one_bar("Fulcrum2Straddle20", "BANKNIFTY", 57342.0, 100.0))
        self.assertEqual(strikes, {55000.0, 57300.0, 57400.0, 59500.0})

    def test_three_straddle_strikes_and_wings_are_unchanged(self):
        strikes = strikes_of(one_bar("Fulcrum3Straddle175", "BANKNIFTY", 57342.0, 100.0))
        self.assertEqual(strikes, {55000.0, 57200.0, 57300.0, 57400.0, 59500.0})

    def test_multi_strikes_and_wings_are_unchanged(self):
        strikes = strikes_of(one_bar("FulcrumMulti50", "BANKNIFTY", 57342.0, 100.0))
        self.assertEqual(strikes, {55000.0, 57300.0, 57400.0, 59500.0})


class GridScalingTests(unittest.TestCase):
    def test_every_variant_produces_legs_on_every_grid(self):
        for name in STRIKE_NAMING:
            for underlying, spot, step in (
                ("BANKNIFTY", 57342.0, 100.0),
                ("NIFTY", 24387.0, 50.0),
                ("SENSEX", 81237.0, 100.0),
                ("MIDCPNIFTY", 12363.0, 25.0),
                ("ADANIPOWER", 153.7, 2.5),
            ):
                signals = one_bar(name, underlying, spot, step)
                self.assertTrue(signals, f"{name} produced nothing on {underlying}")
                self.assertTrue(strikes_of(signals), f"{name} named no strikes on {underlying}")

    def test_every_strike_sits_on_the_underlyings_grid(self):
        """A strike off the grid names a contract that does not exist."""
        for name in STRIKE_NAMING:
            for underlying, spot, step in (
                ("NIFTY", 24387.0, 50.0),
                ("MIDCPNIFTY", 12363.0, 25.0),
                ("ADANIPOWER", 153.7, 2.5),
            ):
                for strike in strikes_of(one_bar(name, underlying, spot, step)):
                    self.assertAlmostEqual(
                        strike / step, round(strike / step), places=6,
                        msg=f"{name} on {underlying}: {strike} is not a multiple of {step}",
                    )

    def test_hedges_are_a_similar_distance_on_every_underlying(self):
        """
        The reason hedges became a share of spot. Under the old fixed 2,000
        points these ranged from 3.5% on BANKNIFTY to 8.3% on NIFTY.
        """
        for name in STRIKE_NAMING:
            for underlying, spot, step in (
                ("BANKNIFTY", 57342.0, 100.0),
                ("NIFTY", 24387.0, 50.0),
                ("SENSEX", 81237.0, 100.0),
                ("ADANIPOWER", 153.7, 2.5),
            ):
                strikes = strikes_of(one_bar(name, underlying, spot, step))
                furthest_below = (spot - min(strikes)) / spot
                furthest_above = (max(strikes) - spot) / spot
                for label, pct in (("put", furthest_below), ("call", furthest_above)):
                    self.assertGreater(pct, 0.02, f"{name}/{underlying}: {label} wing too near ({pct:.1%})")
                    self.assertLess(pct, 0.07, f"{name}/{underlying}: {label} wing too far ({pct:.1%})")


class ThresholdScalingTests(unittest.TestCase):
    def test_a_legacy_points_parameter_is_still_understood(self):
        """
        A run saved before thresholds became step multiples carries points. Read
        as steps it would mean 50 STRIKES, so it has to be converted, not taken
        at face value.
        """
        from strategies.fulcrum.fulcrum_multi import FulcrumMultiStraddleStrategy

        legacy = FulcrumMultiStraddleStrategy({"adjustment_threshold": 50, "minor_threshold": 10})
        self.assertAlmostEqual(legacy.adjustment_steps, 0.5)
        self.assertAlmostEqual(legacy.minor_steps, 0.1)

    def test_the_new_spelling_wins_when_both_are_given(self):
        from strategies.fulcrum.fulcrum_multi import FulcrumMultiStraddleStrategy

        both = FulcrumMultiStraddleStrategy({"adjustment_steps": 0.9, "adjustment_threshold": 50})
        self.assertAlmostEqual(both.adjustment_steps, 0.9)

    def test_the_registered_variants_keep_their_named_thresholds(self):
        """FulcrumMulti50 must still be the 50-point variant on a 100-point grid."""
        for name, expected_steps in (("FulcrumMulti50", 0.5), ("FulcrumMulti70", 0.7), ("FulcrumMulti90", 0.9)):
            self.assertAlmostEqual(VARIANTS[name]().adjustment_steps, expected_steps, msg=name)


SELL_VARIANTS = ["Fulcrum", "Fulcrum2Straddle20", "Fulcrum3Straddle175",
                 "FulcrumMulti50", "FulcrumMulti70", "FulcrumMulti90", "FulcrumQtyAdjustment"]
BUY_VARIANTS = ["FulcrumBuy", "Fulcrum2StraddleBuy20", "Fulcrum3StraddleBuy175",
                "FulcrumMultiBuy50", "FulcrumMultiBuy70", "FulcrumMultiBuy90", "FulcrumQtyAdjustmentBuy"]


def legs_of(signals):
    return [leg for sig in signals for leg in sig.legs]


class BuyVariantTests(unittest.TestCase):
    """
    The buy variants are the same five classes registered the other way round,
    not five more files. What matters is that the direction really inverts and
    that the wings are gone.
    """

    def test_both_families_are_registered(self):
        for name in SELL_VARIANTS + BUY_VARIANTS:
            self.assertIn(name, VARIANTS)

    def test_a_buy_variant_never_sells(self):
        for name in BUY_VARIANTS:
            legs = legs_of(one_bar(name, "BANKNIFTY", 57342.0, 100.0))
            self.assertTrue(legs, f"{name} produced no legs")
            for leg in legs:
                self.assertEqual(leg["side"], "BUY", f"{name} emitted a {leg['side']} leg")

    def test_a_buy_variant_holds_no_wings(self):
        """
        A long option's loss is already capped at the premium paid, so a
        protective wing costs money and protects nothing. The sell variant's
        wings sit far from spot; the buy variant must have none.
        """
        for sell_name, buy_name in zip(SELL_VARIANTS, BUY_VARIANTS):
            sell_strikes = strikes_of(one_bar(sell_name, "BANKNIFTY", 57342.0, 100.0))
            buy_strikes = strikes_of(one_bar(buy_name, "BANKNIFTY", 57342.0, 100.0))
            if not sell_strikes:
                continue  # "Fulcrum" trades only the platform's ATM contracts
            far = {k for k in sell_strikes if abs(k - 57342.0) / 57342.0 > 0.02}
            self.assertTrue(far, f"{sell_name} was expected to hold wings")
            self.assertFalse(
                buy_strikes & far,
                f"{buy_name} still holds the wings {buy_strikes & far}",
            )

    def test_the_straddle_strikes_themselves_are_unchanged(self):
        """Only the direction and the wings differ — the strikes traded are the same."""
        for sell_name, buy_name in zip(SELL_VARIANTS, BUY_VARIANTS):
            sell_strikes = strikes_of(one_bar(sell_name, "BANKNIFTY", 57342.0, 100.0))
            buy_strikes = strikes_of(one_bar(buy_name, "BANKNIFTY", 57342.0, 100.0))
            near = {k for k in sell_strikes if abs(k - 57342.0) / 57342.0 <= 0.02}
            self.assertEqual(buy_strikes, near, f"{buy_name} traded different strikes")

    def test_a_sell_variant_still_sells_and_still_hedges(self):
        """Guards against the direction plumbing flipping the originals."""
        for name in SELL_VARIANTS:
            legs = legs_of(one_bar(name, "BANKNIFTY", 57342.0, 100.0))
            self.assertTrue(any(l["side"] == "SELL" for l in legs), f"{name} stopped selling")
            if name != "Fulcrum":
                self.assertTrue(any(l["side"] == "BUY" for l in legs), f"{name} lost its wings")

    def test_a_hedged_long_is_available_for_anyone_who_wants_one(self):
        """Not the default, but a legitimate structure — the override must work."""
        from strategies.fulcrum.fulcrum_multi import FulcrumMultiStraddleStrategy

        hedged_long = FulcrumMultiStraddleStrategy({"direction": "BUY", "use_hedges": True})
        self.assertEqual(hedged_long.direction, "BUY")
        self.assertTrue(hedged_long.use_hedges)

    def test_buy_variants_scale_across_grids_too(self):
        for name in BUY_VARIANTS:
            for underlying, spot, step in (("NIFTY", 24387.0, 50.0), ("ADANIPOWER", 153.7, 2.5)):
                legs = legs_of(one_bar(name, underlying, spot, step))
                self.assertTrue(legs, f"{name} produced nothing on {underlying}")
                for leg in legs:
                    self.assertEqual(leg["side"], "BUY")


class CatalogueTextTests(unittest.TestCase):
    """
    What the console shows on the card someone is about to press Start on.
    legs_summary is a class attribute, so a class registered as both a seller
    and a buyer will show the seller's text on both unless it is rewritten.
    """

    def test_a_buy_variant_never_describes_itself_as_selling(self):
        for name in BUY_VARIANTS:
            summary = VARIANTS[name]().legs_summary
            self.assertNotIn("Sell", summary, f"{name} card reads: {summary}")
            self.assertIn("Buy", summary, f"{name} card reads: {summary}")

    def test_a_buy_variant_does_not_advertise_wings_it_will_not_trade(self):
        for name in BUY_VARIANTS:
            self.assertNotIn("wings", VARIANTS[name]().legs_summary.lower(), name)

    def test_a_sell_variant_still_reads_as_a_seller(self):
        for name in SELL_VARIANTS:
            summary = VARIANTS[name]().legs_summary
            self.assertIn("Sell", summary, f"{name} card reads: {summary}")

    def test_the_description_says_which_way_it_trades(self):
        self.assertIn("BUY variant", VARIANTS["FulcrumBuy"]().description)
        self.assertIn("SELL variant", VARIANTS["Fulcrum"]().description)

    def test_a_buy_variants_prose_does_not_open_by_saying_it_sells(self):
        """
        `description` is a class attribute too. Appending "this is the BUY
        variant" to a paragraph that opens "Sells short straddles…" contradicts
        itself inside two sentences, on the card someone reads before pressing
        Start.
        """
        for name in BUY_VARIANTS:
            text = VARIANTS[name]().description
            opening = text.split(" This is the BUY variant")[0]
            self.assertNotIn("Sells", opening, f"{name}: {opening[:90]}")
            self.assertNotIn("short straddle", opening, f"{name}: {opening[:90]}")

    def test_a_buy_variant_never_claims_to_profit_from_decay(self):
        """
        The seller's paragraph says it profits from premium decay. On a buy
        variant that is not merely imprecise — it names the thing that is
        costing the position money as the thing earning it, on the button
        someone is about to press.
        """
        for name in BUY_VARIANTS:
            text = VARIANTS[name]().description.lower()
            self.assertNotIn("profits from premium decay", text, name)
            self.assertNotIn("profits from decay", text, name)

    def test_a_buy_variant_does_not_advertise_the_sellers_roll(self):
        # Rolling on every ATM change takes a long straddle off at one strike
        # step — the defect the buy-side exit exists to correct.
        for name in BUY_VARIANTS:
            strategy = VARIANTS[name]()
            self.assertNotIn("rolled on every atm change", strategy.legs_summary.lower(), name)
            self.assertNotIn("whenever the atm strike moves", strategy.description.lower(), name)

    def test_a_buy_variant_does_not_describe_a_breakout_as_its_loss(self):
        """
        A breakout is what a long straddle is PAID for. Describing it as the
        cost — inherited from the seller's paragraph — inverts the one thing
        someone reads before deploying it.
        """
        for name in BUY_VARIANTS:
            text = VARIANTS[name]().description.lower()
            self.assertNotIn("collects more premium", text, name)
            self.assertNotIn("bigger loss on a breakout", text, name)

    def test_no_variants_prose_is_left_with_dangling_punctuation(self):
        # Wing clauses are removed from mid-sentence for the unhedged variants.
        for name, factory in VARIANTS.items():
            text = factory().description
            for broken in (" —.", " ,", "  ", "and ."):
                self.assertNotIn(broken, text, f"{name}: {text[:160]}")

    def test_a_sell_variant_still_says_it_profits_from_decay(self):
        # The correction must not have reached the seller, for whom it is true.
        self.assertIn("profits from premium decay", VARIANTS["Fulcrum"]().description.lower())

    def test_a_buy_variants_prose_does_not_promise_wings(self):
        for name in BUY_VARIANTS:
            opening = VARIANTS[name]().description.split(" This is the BUY variant")[0]
            self.assertNotIn("wings", opening.lower(), f"{name}: {opening[:120]}")


class NamingTests(unittest.TestCase):
    """
    The family was renamed. Every name the platform writes down — a catalogue
    entry, a signal's strategy_name, a group id — outlives the code that
    produced it: these go into the database and are read back for months.

    Asserted positively, as "everything is the current name", rather than by
    listing what the old one was. Naming it here would put the retired string
    straight back into the repository, which is the thing the rename existed to
    remove. A local pre-commit hook blocks it at the boundary instead.
    """

    def test_the_catalogue_advertises_only_current_names(self):
        for name in VARIANTS:
            self.assertTrue(name.startswith("Fulcrum"), f"unexpected catalogue entry: {name}")

    def test_signals_carry_the_current_name_and_group_prefix(self):
        for name in SELL_VARIANTS + BUY_VARIANTS:
            for sig in one_bar(name, "BANKNIFTY", 57342.0, 100.0):
                self.assertTrue(
                    sig.strategy_name.startswith("Fulcrum"),
                    f"{name} emitted strategy_name {sig.strategy_name}",
                )
                group_id = sig.metadata.get("group_id", "")
                if group_id:
                    self.assertTrue(group_id.startswith("FULCRUM"), group_id)

    def test_no_source_file_in_the_package_carries_a_retired_name(self):
        """
        The retired name is read from the local pre-commit hook rather than
        written here, so the repository never contains it. Skipped where the
        hook is not installed (a fresh clone), because there is then nothing to
        check against.
        """
        import re

        hook = pathlib.Path(__file__).resolve().parents[3] / ".git" / "hooks" / "pre-commit"
        if not hook.exists():
            self.skipTest("no local pre-commit hook to read retired names from")

        retired = re.findall(r"RETIRED_NAMES=\((.*?)\)", hook.read_text(), re.S)
        if not retired:
            self.skipTest("hook lists no retired names")
        names = [n.strip().strip('"\'') for n in retired[0].split() if n.strip()]

        package = pathlib.Path(__file__).resolve().parents[1]
        offenders = []
        for path in package.rglob("*.py"):
            if "__pycache__" in path.parts:
                continue
            body = path.read_text(errors="ignore").lower()
            offenders += [f"{path.name}: {n}" for n in names if n.lower() in body]

        self.assertEqual(offenders, [], f"retired name still present: {offenders}")


if __name__ == "__main__":
    unittest.main()
