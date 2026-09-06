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

import unittest

import _bootstrap  # noqa: F401

from strategies.base_strategy import OptionContract, StrategyInput
from strategies.private_strategies import get_private_strategies

VARIANTS = get_private_strategies()

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


class LegacyNameTests(unittest.TestCase):
    """
    The family was renamed from its old name. Runs recorded before that still
    carry it, and the platform launches a run BY name — so an old name has to
    keep resolving or that history becomes unopenable.
    """

    def test_every_old_name_resolves_to_a_strategy_that_exists(self):
        """
        Checked against the FULL factory map, which is what the runner looks up
        in — the auto-discovered base classes live there too, not only in the
        parameterised registry.
        """
        from strategies.private_strategies import LEGACY_NAMES, canonical_strategy_name
        from strategies.registry import load_strategy_factories

        launchable = load_strategy_factories()
        for old in LEGACY_NAMES:
            current = canonical_strategy_name(old)
            self.assertIn(current, launchable, f"{old} resolves to {current}, which is missing")

    def test_a_current_name_passes_through_unchanged(self):
        from strategies.private_strategies import canonical_strategy_name

        for name in VARIANTS:
            self.assertEqual(canonical_strategy_name(name), name)

    def test_an_unknown_name_is_left_alone_rather_than_guessed_at(self):
        from strategies.private_strategies import canonical_strategy_name

        self.assertEqual(canonical_strategy_name("GhostTangentCrossings"), "GhostTangentCrossings")
        self.assertEqual(canonical_strategy_name(""), "")

    def test_the_catalogue_advertises_only_current_names(self):
        """
        A retired name must resolve, not reappear as a second card. Merged into
        the factory map, every strategy would show on screen twice under two
        names.

        Asserted as a positive — every advertised name belongs to this family —
        rather than by naming the retired one, which would only put that string
        back into the repository.
        """
        from strategies.private_strategies import LEGACY_NAMES

        for name in VARIANTS:
            self.assertTrue(name.startswith("Fulcrum"), f"unexpected catalogue entry: {name}")
            self.assertNotIn(name, LEGACY_NAMES, f"{name} is a retired name")

    def test_signals_carry_the_current_name_and_group_prefix(self):
        """
        Signals and group ids are written to the database and read back for
        months, so a stale name in them outlives the code that produced it.
        """
        from strategies.private_strategies import LEGACY_NAMES

        retired_prefixes = {n[:6].upper() for n in LEGACY_NAMES}

        for name in SELL_VARIANTS + BUY_VARIANTS:
            for sig in one_bar(name, "BANKNIFTY", 57342.0, 100.0):
                self.assertTrue(
                    sig.strategy_name.startswith("Fulcrum"),
                    f"{name} emitted strategy_name {sig.strategy_name}",
                )
                group_id = sig.metadata.get("group_id", "")
                if group_id:
                    self.assertTrue(group_id.startswith("FULCRUM"), group_id)
                    self.assertNotIn(group_id[:6].upper(), retired_prefixes)


if __name__ == "__main__":
    unittest.main()
