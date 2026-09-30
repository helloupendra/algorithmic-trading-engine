"""
The feed process's own switches (market_data/live/run_feed.py): the GIL
switch interval the socket thread needs, and the strategy stream's bounded
Redis client. Nothing is started: the runner, the vendor and Redis are fakes.
"""

import os
import sys
import unittest
from unittest import mock

import _bootstrap  # noqa: F401

import market_data.live.run_feed as run_feed


class FakeRunner:
    def __init__(self, feed, publisher=None, fixed_symbols=None, **_):
        self.source_name = feed.source_name

    def run(self):
        pass

    def stop(self):
        pass


class RunFeedSwitchTests(unittest.TestCase):
    def _main(self, **env):
        patcher = mock.patch.dict(os.environ, {}, clear=False)
        patcher.start()
        self.addCleanup(patcher.stop)
        for key in ("FEED_GIL_SWITCH_MS", "FEED_REDIS_TIMEOUT_SECONDS", "FEED_GC_FREEZE"):
            os.environ.pop(key, None)
        os.environ.update(env)
        feed = mock.MagicMock(key="fake", source_name="python-fake-feed", is_replay=False)
        printed = []
        with mock.patch("messaging.redis_publisher.build_publisher_from_env") as build, \
             mock.patch("market_data.live.vendors.build_feed", return_value=feed), \
             mock.patch("core.live.symbol_list.symbols_for", return_value=([], {})), \
             mock.patch("core.live.feed_runner.FeedRunner", FakeRunner), \
             mock.patch("core.safe_output.install_safe_stdio"), \
             mock.patch("signal.signal"), \
             mock.patch.object(sys, "setswitchinterval") as switch, \
             mock.patch("gc.freeze") as freeze, mock.patch("gc.collect"), \
             mock.patch("builtins.print", side_effect=lambda *a, **_: printed.append(" ".join(map(str, a)))):
            run_feed.main(["--vendor", "fake"])
        self.freeze = freeze
        return build, switch, printed

    def test_the_startup_heap_is_frozen_after_the_greeks_libraries_load(self):
        _, _, printed = self._main()
        self.freeze.assert_called_once_with()
        self.assertIn("core.greeks_calculator", sys.modules, "loaded before the freeze, not on the first tick")
        self.assertTrue(any("frozen out of the cyclic GC" in line for line in printed), printed)

    def test_feed_gc_freeze_0_leaves_the_collector_alone(self):
        _, _, printed = self._main(FEED_GC_FREEZE="0")
        self.freeze.assert_not_called()
        self.assertTrue(any("FEED_GC_FREEZE=0" in line for line in printed), printed)

    def test_the_defaults_are_the_fix(self):
        build, switch, printed = self._main()
        switch.assert_called_once_with(0.001)
        self.assertEqual({"stream_timeout": 2.0}, build.call_args.kwargs)
        self.assertTrue(any("GIL switch interval 1 ms" in line for line in printed), printed)

    def test_zero_restores_the_old_behaviour(self):
        build, switch, printed = self._main(FEED_GIL_SWITCH_MS="0", FEED_REDIS_TIMEOUT_SECONDS="0")
        switch.assert_not_called()
        self.assertEqual({"stream_timeout": None}, build.call_args.kwargs, "the shared client, as before")
        self.assertTrue(any("left at Python's" in line for line in printed), printed)

    def test_other_values_and_nonsense(self):
        build, switch, _ = self._main(FEED_GIL_SWITCH_MS="2.5", FEED_REDIS_TIMEOUT_SECONDS="0.5")
        switch.assert_called_once_with(0.0025)
        self.assertEqual({"stream_timeout": 0.5}, build.call_args.kwargs)

        build, switch, printed = self._main(FEED_GIL_SWITCH_MS="fast", FEED_REDIS_TIMEOUT_SECONDS="-1")
        switch.assert_called_once_with(0.001)
        self.assertEqual({"stream_timeout": 2.0}, build.call_args.kwargs)
        self.assertEqual(2, sum("is not a number of 0 or more" in line for line in printed))


if __name__ == "__main__":
    unittest.main()
