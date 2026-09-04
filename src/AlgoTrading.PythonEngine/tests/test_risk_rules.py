"""Risk rules parsing (backtest/run_spec.py): `risk` object preferred, legacy keys as fallback."""

import json
import unittest

import _bootstrap  # noqa: F401

from backtest.run_spec import RiskRules, parse_risk_rules


class RiskRulesParsingTests(unittest.TestCase):
    def test_legacy_keys_become_the_overall_level(self) -> None:
        rules = parse_risk_rules({"stop_loss": 5000, "target": "8000"})
        self.assertEqual(rules.overall.stop_loss, 5000.0)
        self.assertEqual(rules.overall.target, 8000.0)
        self.assertEqual((rules.stop_loss, rules.target), (5000.0, 8000.0))
        self.assertFalse(rules.group.is_set)
        self.assertFalse(rules.leg.is_set)
        self.assertTrue(rules.is_set)

    def test_risk_object_is_preferred_over_legacy_keys(self) -> None:
        params = {
            "stop_loss": 999, "target": 999,
            "risk": {
                "overall": {"stopLoss": 5000, "target": None},
                "group": {"stopLoss": 1000, "target": 2500},
                "leg": {"stopLossPoints": 20, "targetPoints": 40, "stopLossPercent": 5, "targetPercent": None},
            },
        }
        rules = parse_risk_rules(params)
        self.assertEqual(rules.overall.stop_loss, 5000.0)
        self.assertIsNone(rules.overall.target)
        self.assertEqual((rules.group.stop_loss, rules.group.target), (1000.0, 2500.0))
        self.assertEqual(rules.leg.stop_loss_points, 20.0)
        self.assertEqual(rules.leg.target_points, 40.0)
        self.assertEqual(rules.leg.stop_loss_percent, 5.0)
        self.assertIsNone(rules.leg.target_percent)

    def test_risk_object_as_json_text_and_snake_case(self) -> None:
        raw = json.dumps({"overall": {"stop_loss": 100}, "leg": {"stop_loss_points": 7.5}})
        rules = parse_risk_rules({"risk": raw})
        self.assertEqual(rules.overall.stop_loss, 100.0)
        self.assertEqual(rules.leg.stop_loss_points, 7.5)

    def test_non_positive_and_garbage_values_are_unset(self) -> None:
        rules = parse_risk_rules({"risk": {"overall": {"stopLoss": 0, "target": -5},
                                          "group": {"stopLoss": "abc"}, "leg": "not an object"}})
        self.assertFalse(rules.is_set)
        self.assertEqual(rules.describe(), "none")

    def test_unusable_risk_falls_back_to_legacy(self) -> None:
        self.assertEqual(parse_risk_rules({"risk": None, "stop_loss": 10}).stop_loss, 10.0)
        self.assertEqual(parse_risk_rules({"risk": "not json", "target": 20}).target, 20.0)
        self.assertEqual(parse_risk_rules({"risk": [1, 2], "stop_loss": 30}).stop_loss, 30.0)
        # An explicit empty object means "nothing set", not "use the legacy keys".
        self.assertFalse(parse_risk_rules({"risk": {}, "stop_loss": 10}).is_set)
        self.assertFalse(parse_risk_rules(None).is_set)

    def test_to_dict_round_trips_in_camel_case(self) -> None:
        rules = parse_risk_rules({"risk": {"group": {"target": 1500}, "leg": {"targetPercent": 30}}})
        payload = rules.to_dict()
        self.assertEqual(set(payload), {"overall", "group", "leg"})
        self.assertEqual(payload["group"], {"stopLoss": None, "target": 1500.0,
                                            "trailStopLoss": None, "trailTrigger": None})
        self.assertEqual(payload["leg"]["targetPercent"], 30.0)
        self.assertEqual(set(payload["leg"]), {
            "stopLossPoints", "targetPoints", "stopLossPercent", "targetPercent",
            "trailStopLossPoints", "trailStopLossPercent", "trailTriggerPoints", "trailTriggerPercent",
        })
        self.assertEqual(RiskRules.from_object(payload), rules)

    def test_describe_lists_only_set_levels(self) -> None:
        rules = parse_risk_rules({"risk": {"overall": {"stopLoss": 5000}, "leg": {"stopLossPoints": 20, "stopLossPercent": 5}}})
        text = rules.describe()
        self.assertIn("overall SL ₹5,000", text)
        self.assertIn("leg SL 20 pts / 5%", text)
        self.assertNotIn("group", text)


if __name__ == "__main__":
    unittest.main()
