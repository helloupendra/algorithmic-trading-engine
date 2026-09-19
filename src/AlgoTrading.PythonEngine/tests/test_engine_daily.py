"""
Daily-resolution backtests. A daily candle is a whole session, so the engine
carries positions from day to day, closes a contract that expired at its last
close, sizes the warm-up from the strategy's bar count, and tells the strategy
which candles the run steps through. Before this, a 1D run of a strategy that
read 5-minute bars completed with no trades and no explanation.
"""

import unittest
from datetime import date, datetime, time, timezone
from typing import Any, Dict, List

import _bootstrap  # noqa: F401

from backtest.engine import WARMUP_DAYS, warmup_days_for
from backtest.timeutil import iso_utc, ist_day_end_utc, ist_day_start_utc
from strategies.base_strategy import BaseStrategy, StrategyInput
from strategies.ghost_tangent_crossings import GhostTangentCrossingsStrategy
from test_engine import (CE, EXPIRY, SPOT, UNDERLYING, EngineRunner, FakeApi, close_signals, contract_symbol,
                         open_ce, reason_of)

# Wed 19 Aug .. Wed 26 Aug 2026; the contract expires on Tue 25 Aug.
DAYS = [date(2026, 8, 19), date(2026, 8, 20), date(2026, 8, 21), date(2026, 8, 24), date(2026, 8, 25), date(2026, 8, 26)]
NEXT_CE = contract_symbol(57600, "CE", "2026-09-29")


def daily_stamp(day: date) -> str:
    """Daily candles are stored at midnight UTC of their date (05:30 IST)."""
    return iso_utc(datetime.combine(day, time(0, 0), tzinfo=timezone.utc))


def daily_rows(symbol: str, days: List[date], closes: List[float]) -> List[Dict[str, Any]]:
    return [{"symbol": symbol, "resolution": "D", "timestampUtc": daily_stamp(d), "open": c, "high": c + 5,
             "low": c - 5, "close": c, "volume": 0} for d, c in zip(days, closes)]


def daily_row(from_day: date, to_day: date, **params: Any) -> Dict[str, Any]:
    import json
    merged = {"lots": 1, "stop_loss": None, "target": None, "underlying": UNDERLYING, "resolution": "1D",
              "eod_square_off_ist": "15:15", "charges_per_lot": 0}
    merged.update(params)
    return {"id": 42, "mode": "OfflineReplay", "symbol": SPOT, "resolution": "D", "strategyName": "Scripted",
            "fromUtc": iso_utc(ist_day_start_utc(from_day)), "toUtc": iso_utc(ist_day_end_utc(to_day)),
            "parametersJson": json.dumps(merged), "initialCapital": 1_000_000, "userId": 1}


def daily_api() -> FakeApi:
    candles = {
        (SPOT, "D"): daily_rows(SPOT, DAYS, [57620.0] * len(DAYS)),
        (CE, "D"): daily_rows(CE, DAYS[:5], [100.0, 110.0, 120.0, 130.0, 140.0]),
        (NEXT_CE, "D"): daily_rows(NEXT_CE, DAYS, [300.0, 305.0, 310.0, 315.0, 320.0, 325.0]),
    }
    return FakeApi(candles)


class DailyRunTests(EngineRunner, unittest.TestCase):
    def test_the_strategy_is_told_it_steps_through_daily_candles(self):
        _, strategy = self.run_engine(daily_api(), {}, daily_row(DAYS[0], DAYS[2]))
        self.assertEqual({i.metadata.get("resolution") for i in strategy.inputs}, {"1D"})

    def test_what_a_daily_candle_triggers_happens_at_the_close(self):
        api = daily_api()
        self.run_engine(api, {0: open_ce(1)}, daily_row(DAYS[0], DAYS[1]))
        self.assertEqual({s["timestampUtc"][11:19] for s in api.signals}, {"10:00:00"})   # 15:30 IST

    def test_a_position_carries_across_days_instead_of_closing_at_its_entry_price(self):
        api = daily_api()
        outcome, _ = self.run_engine(api, {0: open_ce(1)}, daily_row(DAYS[0], DAYS[3]))
        self.assertEqual(outcome.summary["eodSquareOffs"], 0)
        self.assertEqual(close_signals(api)[-1]["legs"][0]["price"], 130.0)   # held to the end of the range

    def test_an_expired_contract_is_closed_at_its_last_close(self):
        api = daily_api()
        logs: List[str] = []
        outcome, _ = self.run_engine(api, {0: open_ce(1)}, daily_row(DAYS[0], DAYS[5]), log=logs.append)

        closes = close_signals(api)
        self.assertEqual(len(closes), 1)
        self.assertIn(f"expired on {EXPIRY}", reason_of(closes[0]))
        self.assertEqual(closes[0]["legs"][0]["price"], 140.0)                # the expiry day's close
        self.assertEqual(outcome.ledger.closed[0].realized, (140.0 - 100.0) * 30)
        self.assertTrue(any("[EXPIRY]" in line for line in logs))

    def test_an_entry_on_expiry_day_takes_the_next_expiry(self):
        # The daily bar is traded at 15:30, when the 25 Aug contract has already expired.
        api = daily_api()
        self.run_engine(api, {4: open_ce(1)}, daily_row(DAYS[0], DAYS[5]))
        opened = [s for s in api.signals if s["signalType"] == "OPEN_GROUP"]
        self.assertEqual(opened[0]["legs"][0]["symbol"], NEXT_CE)

    def test_the_log_says_the_end_of_day_square_off_does_not_apply(self):
        logs: List[str] = []
        self.run_engine(daily_api(), {}, daily_row(DAYS[0], DAYS[1]), log=logs.append)
        self.assertTrue(any("positions carry from one session to the next" in line for line in logs))


class WarmupWindowTests(unittest.TestCase):
    class NeedsBars(BaseStrategy):
        def __init__(self, bars):
            self.warmup_bars = bars

    def test_a_strategy_without_a_bar_count_keeps_the_default_window(self):
        self.assertEqual(warmup_days_for(BaseStrategy(), "D"), WARMUP_DAYS)

    def test_daily_bars_need_a_window_of_sessions_not_days(self):
        # 100 sessions: 140 calendar days of weekends, +10% for holidays, +5.
        self.assertEqual(warmup_days_for(self.NeedsBars(100), "D"), 159)

    def test_intraday_bars_fit_in_the_default_window(self):
        self.assertEqual(warmup_days_for(self.NeedsBars(100), "5"), WARMUP_DAYS)
        self.assertGreater(warmup_days_for(self.NeedsBars(2_000), "5"), WARMUP_DAYS)


class GhostChartTests(unittest.TestCase):
    def input_for(self, resolution: str) -> StrategyInput:
        return StrategyInput(mode="OfflineReplay", timestamp_utc="2026-08-19T00:00:00Z", underlying=UNDERLYING,
                             spot_price=57620.0, bars={}, metadata={"resolution": resolution})

    def test_it_draws_on_the_runs_candles_when_they_are_longer_than_five_minutes(self):
        ghost = GhostTangentCrossingsStrategy()
        self.assertEqual(ghost.chart(self.input_for("1D")), "1D")
        self.assertEqual(ghost.chart(self.input_for("15m")), "15m")

    def test_it_keeps_the_five_minute_chart_for_shorter_or_unknown_runs(self):
        ghost = GhostTangentCrossingsStrategy()
        self.assertEqual(ghost.chart(self.input_for("1m")), "5m")
        self.assertEqual(ghost.chart(self.input_for("5m")), "5m")
        live = StrategyInput(mode="LivePaper", timestamp_utc="2026-08-19T04:00:00Z", underlying=UNDERLYING,
                             spot_price=57620.0, bars={}, metadata={"source": "live-api"})
        self.assertEqual(ghost.chart(live), "5m")

    def test_a_chart_can_be_forced(self):
        self.assertEqual(GhostTangentCrossingsStrategy({"timeframe": "5m"}).chart(self.input_for("1D")), "5m")

    def test_it_asks_for_four_pivot_windows_of_warm_up(self):
        self.assertEqual(GhostTangentCrossingsStrategy().warmup_bars, 100)
        self.assertEqual(GhostTangentCrossingsStrategy({"pivot_forward": 10}).warmup_bars, 40)

    def test_it_reads_daily_candles_on_a_daily_run(self):
        ghost = GhostTangentCrossingsStrategy({"pivot_forward": 2})
        state = ghost.initialize_state()
        daily = [type("Bar", (), {"timestamp_utc": f"2026-08-{d:02d}T00:00:00Z", "high": h, "low": h - 10,
                                  "open": h - 5, "close": h - 5})()
                 for d, h in zip(range(3, 13), [100, 110, 120, 110, 100, 90, 100, 110, 120, 130])]
        for i in range(1, len(daily) + 1):
            inp = StrategyInput(mode="OfflineReplay", timestamp_utc=daily[i - 1].timestamp_utc, underlying=UNDERLYING,
                                spot_price=daily[i - 1].close, bars={"1D": {"index": daily[:i]}, "5m": {"index": []}},
                                metadata={"resolution": "1D"})
            ghost.on_bar(state, inp)
        # One step per daily candle, and a pivot high found on the daily chart.
        self.assertEqual(state["bar_index"], len(daily))
        self.assertIsNotNone(state["ph_current"])


if __name__ == "__main__":
    unittest.main()
