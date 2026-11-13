"""tools/run_backtest.py: the request it sends, and the risk rules in it."""

import argparse
import unittest

from tools import run_backtest as rb


def args(**overrides):
    base = dict(strategy="GhostTangentCrossings", underlying="nifty", resolution="5m", from_date="2026-08-13",
                to_date="2026-11-12", lots=1, sl=None, target=None, eod="15:15", charges=0.0, capital=None, params=None)
    base.update(overrides)
    return argparse.Namespace(**base)


class BuildRequestTest(unittest.TestCase):
    def test_risk_in_params_is_sent_as_the_requests_own_field(self):
        payload = rb.build_request(args(params='{"risk": {"leg": {"stopLossPoints": 10, "targetPoints": 20}}, '
                                              '"costs": {"slippage_percent": 0.5}}'), 7)
        self.assertEqual(payload["risk"], {"leg": {"stopLossPoints": 10, "targetPoints": 20}})
        self.assertEqual(payload["parameters"], {"costs": {"slippage_percent": 0.5}})

    def test_params_without_risk_send_no_risk_field(self):
        payload = rb.build_request(args(params='{"pivot_forward": 15}'), 7)
        self.assertNotIn("risk", payload)
        self.assertEqual(payload["parameters"], {"pivot_forward": 15})

    def test_overall_stop_and_target_flags_are_unchanged(self):
        payload = rb.build_request(args(sl=5000.0, target=8000.0), 7)
        self.assertEqual((payload["stopLoss"], payload["target"]), (5000.0, 8000.0))
        self.assertNotIn("risk", payload)

    def test_risk_that_is_not_an_object_is_refused(self):
        with self.assertRaises(SystemExit):
            rb.build_request(args(params='{"risk": 10}'), 7)


if __name__ == "__main__":
    unittest.main()
