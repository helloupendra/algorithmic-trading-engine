"""
Engine smoke tests: a fake API and a scripted strategy over synthetic 5m bars.
Covers OPEN then CLOSE with the expected P&L, one equity point per bar, the
EOD square-off, SL/target trips on total P&L, skipped entries and the
zero-bars failure.
"""

import unittest
from datetime import date, datetime, time, timedelta
from typing import Any, Callable, Dict, List, Optional

import _bootstrap  # noqa: F401

from backtest.engine import run_backtest
from backtest.timeutil import IST, iso_utc, ist_day_end_utc, ist_day_start_utc, parse_utc
from strategies.base_strategy import BaseStrategy, DataRequirement, StrategyInput, StrategySignal

SPOT = "NSE:NIFTYBANK-INDEX"
UNDERLYING = "BANKNIFTY"
LOT_SIZE = 30
DAY1 = date(2026, 8, 19)
DAY2 = date(2026, 8, 20)
EXPIRY = "2026-08-25"


def bar_start(day: date, hh: int, mm: int) -> datetime:
    return datetime.combine(day, time(hh, mm), tzinfo=IST)


def session_bars(day: date, minutes: int = 5, last: time = time(15, 25)) -> List[datetime]:
    out = []
    cursor = bar_start(day, 9, 15)
    while cursor.time() <= last:
        out.append(cursor)
        cursor += timedelta(minutes=minutes)
    return out


def index_rows(days: List[date], base: float = 57620.0, last: time = time(15, 25)) -> List[Dict[str, Any]]:
    """Spot drifts 0.1/bar from 57620 so the ATM strike stays 57600 all session."""
    rows = []
    for day in days:
        for i, start in enumerate(session_bars(day, 5, last)):
            close = base + i * 0.1
            rows.append({"symbol": SPOT, "resolution": "5", "timestampUtc": iso_utc(start),
                         "open": close - 1, "high": close + 2, "low": close - 2, "close": close, "volume": 0})
    return rows


def option_rows(symbol: str, days: List[date], price_fn: Callable[[int], float], last: time = time(15, 25)) -> List[Dict[str, Any]]:
    rows = []
    for day in days:
        for i, start in enumerate(session_bars(day, 5, last)):
            close = price_fn(i)
            rows.append({"symbol": symbol, "resolution": "5", "timestampUtc": iso_utc(start),
                         "open": close, "high": close, "low": close, "close": close, "volume": 0})
    return rows


def contract_symbol(strike: int, option_type: str, expiry: str = EXPIRY) -> str:
    token = datetime.strptime(expiry, "%Y-%m-%d").strftime("%y%b").upper()
    return f"NSE:{UNDERLYING}{token}{strike}{option_type}"


CE = contract_symbol(57600, "CE")
PE = contract_symbol(57600, "PE")


class FakeApi:
    """Just enough of PlatformApiClient for the engine, recording every write."""

    def __init__(self, candles: Dict[tuple, List[Dict[str, Any]]], broker_linked: bool = False,
                 sync_rows: Optional[Dict[str, List[Dict[str, Any]]]] = None):
        self.candles = candles
        self.broker_linked = broker_linked
        self.sync_rows = sync_rows or {}
        self.signals: List[Dict[str, Any]] = []
        self.marks: List[Dict[str, Any]] = []
        self.snapshots: List[Dict[str, Any]] = []
        self.progress: List[Dict[str, Any]] = []
        self.completed: List[Dict[str, Any]] = []
        self.synced: List[tuple] = []
        self.fail_contract_lookups = 0      # raise on this many get_exact_contract calls first
        self.contract_calls = 0

    def get_local_history(self, symbol, resolution, from_date, to_date):
        rows = self.candles.get((symbol, resolution), [])
        return [r for r in rows if from_date <= r["timestampUtc"][:10] <= to_date]

    def sync_history(self, symbol, resolution, from_date, to_date):
        if not self.broker_linked:
            raise RuntimeError("400: broker session missing")
        self.synced.append((symbol, resolution, from_date, to_date))
        rows = self.sync_rows.get(symbol, [])
        if rows:
            self.candles.setdefault((symbol, resolution), []).extend(rows)
        return rows

    def get_broker_session(self):
        return {"isAuthenticated": self.broker_linked}

    def get_fno_underlyings(self):
        return [{"underlying": UNDERLYING, "lotSize": LOT_SIZE, "lotSizeSource": "master", "strikeStep": 100}]

    def get_expiries(self, underlying):
        return [{"underlying": underlying, "expiryDate": EXPIRY}, {"underlying": underlying, "expiryDate": "2026-09-29"}]

    def get_option_chain(self, underlying, expiry, from_strike=None, to_strike=None):
        return [{"strikePrice": 57000 + i * 100} for i in range(10)]

    def get_exact_contract(self, underlying, expiry, strike, option_type):
        self.contract_calls += 1
        if self.fail_contract_lookups > 0:
            self.fail_contract_lookups -= 1
            raise RuntimeError("502 Bad Gateway")
        return {"symbol": contract_symbol(int(strike), option_type, expiry), "underlying": underlying,
                "expiryDate": expiry, "strikePrice": strike, "optionType": option_type}

    def create_simulation_signal(self, payload):
        self.signals.append(payload)
        return {"id": len(self.signals)}

    def post_marks(self, run_id, at_utc, marks):
        self.marks.append({"atUtc": at_utc, "marks": marks})

    def post_equity_snapshots(self, run_id, items):
        assert len(items) <= 5000
        self.snapshots.extend(items)

    def post_progress(self, run_id, progress):
        self.progress.append(progress)

    def complete_run(self, run_id, status, summary, error=None):
        self.completed.append({"runId": run_id, "status": status, "summary": summary, "error": error})


class ScriptedStrategy(BaseStrategy):
    """Emits whatever the script says at the given in-range bar index."""
    name = "Scripted"
    default_lots = 1

    def __init__(self, params=None, script=None, requirements=None):
        self.params = params or {}
        self.lots = self.lots_from(self.params, self.default_lots)
        self.script = script or {}
        self.requirements = requirements or []
        self.inputs: List[StrategyInput] = []
        self.index_bar_counts: List[int] = []     # bars["5m"]["index"] is a shared, growing list: record its size now

    def get_data_requirements(self):
        return self.requirements

    def initialize_state(self):
        return {"bar": -1}

    def on_bar(self, state, inp):
        if inp.metadata.get("source") == "warmup":
            return []
        state["bar"] += 1
        self.inputs.append(inp)
        self.index_bar_counts.append(len(inp.bars.get("5m", {}).get("index", [])))
        action = self.script.get(state["bar"])
        return action(inp, self) if action else []


def open_ce(lots: int = 1, group: str = "g1"):
    def action(inp, strategy):
        ce = inp.contracts["atm_ce"]
        return [StrategySignal(strategy_name="Scripted", signal_type="OPEN_GROUP", timestamp_utc=inp.timestamp_utc,
                               reason="scripted entry", legs=[{"symbol": ce.symbol, "side": "BUY", "quantity": lots}],
                               metadata={"group_id": group})]
    return action


def close_ce(lots: int = 1, group: str = "g1"):
    def action(inp, strategy):
        ce = inp.contracts["atm_ce"]
        return [StrategySignal(strategy_name="Scripted", signal_type="CLOSE_GROUP", timestamp_utc=inp.timestamp_utc,
                               reason="scripted exit", legs=[{"symbol": ce.symbol, "side": "SELL", "quantity": lots}],
                               metadata={"group_id": group})]
    return action


def run_row(from_day: date, to_day: date, **params) -> Dict[str, Any]:
    merged = {"lots": 1, "stop_loss": None, "target": None, "underlying": UNDERLYING, "resolution": "5m",
              "eod_square_off_ist": "15:15", "charges_per_lot": 0}
    merged.update(params)
    import json
    return {"id": 42, "mode": "OfflineReplay", "symbol": SPOT, "resolution": "5", "strategyName": "Scripted",
            "fromUtc": iso_utc(ist_day_start_utc(from_day)), "toUtc": iso_utc(ist_day_end_utc(to_day)),
            "parametersJson": json.dumps(merged), "initialCapital": 1_000_000, "userId": 1}


def quiet(_: str) -> None:
    pass


def make_api(ce_price: Callable[[int], float] = lambda i: 100.0 + i, days: Optional[List[date]] = None,
             last: time = time(15, 25), with_ce: bool = True, **kwargs) -> FakeApi:
    days = days or [DAY1]
    candles = {(SPOT, "5"): index_rows(days, last=last)}
    if with_ce:
        candles[(CE, "5")] = option_rows(CE, days, ce_price, last=last)
        candles[(PE, "5")] = option_rows(PE, days, lambda i: 80.0 - i * 0.5, last=last)
    return FakeApi(candles, **kwargs)


class EngineSmokeTests(unittest.TestCase):
    def run_engine(self, api: FakeApi, script: Dict[int, Callable], row: Dict[str, Any], requirements=None, **kwargs):
        holder: Dict[str, ScriptedStrategy] = {}

        def factory(params=None):
            holder["s"] = ScriptedStrategy(params, script, requirements)
            return holder["s"]

        outcome = run_backtest(api, row, factory, on_progress=lambda p: api.post_progress(42, p),
                               log=quiet, progress_interval=0.0, **kwargs)
        return outcome, holder["s"]

    def test_open_then_close_with_expected_pnl(self):
        api = make_api(lambda i: 100.0 + i)
        outcome, strategy = self.run_engine(api, {2: open_ce(2), 5: close_ce(2)}, run_row(DAY1, DAY1, lots=2))

        self.assertEqual(outcome.status, "Completed")
        self.assertEqual(api.completed[-1]["status"], "Completed")
        self.assertEqual(outcome.summary["totalBars"], 75)
        self.assertEqual(outcome.summary["sessions"], 1)
        self.assertEqual(outcome.summary["trades"], 1)
        self.assertEqual(outcome.summary["skippedEntries"], [])

        # OPEN at bar 2 (close 102) then CLOSE at bar 5 (close 105): (105 - 102) x 2 lots x 30
        self.assertEqual([s["signalType"] for s in api.signals], ["OPEN_GROUP", "CLOSE_GROUP"])
        open_sig, close_sig = api.signals
        self.assertEqual(open_sig["legs"], [{"symbol": CE, "side": "BUY", "quantity": 2, "price": 102.0}])
        self.assertEqual(close_sig["legs"], [{"symbol": CE, "side": "SELL", "quantity": 2, "price": 105.0}])
        self.assertEqual(open_sig["timestampUtc"], iso_utc(bar_start(DAY1, 9, 25)))
        self.assertEqual(close_sig["timestampUtc"], iso_utc(bar_start(DAY1, 9, 40)))
        self.assertEqual(open_sig["simulationRunId"], 42)
        self.assertEqual(open_sig["groupId"], "g1")
        self.assertIn('"reason": "scripted entry"', open_sig["metadataJson"])
        self.assertAlmostEqual(outcome.ledger.realized_pnl(), 3.0 * 2 * LOT_SIZE)
        self.assertEqual(outcome.ledger.closed[0].exit_reason, "scripted exit")

        # One equity point per bar, all posted with the historical timestamps.
        self.assertEqual(len(outcome.equity_points), 75)
        self.assertEqual(len(api.snapshots), 75)
        self.assertEqual(api.snapshots[0]["snapshotUtc"], iso_utc(bar_start(DAY1, 9, 15)))
        self.assertEqual(api.snapshots[-1]["snapshotUtc"], iso_utc(bar_start(DAY1, 15, 25)))
        self.assertEqual(api.snapshots[3]["openPositions"], 1)
        self.assertAlmostEqual(api.snapshots[3]["unrealizedPnl"], 1.0 * 2 * LOT_SIZE)
        self.assertEqual(api.snapshots[5]["closedPositions"], 1)
        self.assertAlmostEqual(api.snapshots[5]["realizedPnl"], 180.0)

        # Marks were posted only while something was open and the mark changed.
        self.assertEqual(len(api.marks), 3)          # bars 2, 3, 4: open book with a changed mark
        self.assertEqual(api.marks[0]["marks"], [{"symbol": CE, "price": 102.0}])
        self.assertEqual(api.marks[-1], {"atUtc": iso_utc(bar_start(DAY1, 9, 35)), "marks": [{"symbol": CE, "price": 104.0}]})

        # Strategy inputs carry the OfflineReplay contract.
        first = strategy.inputs[0]
        self.assertEqual(first.mode, "OfflineReplay")
        self.assertEqual(first.underlying, UNDERLYING)
        self.assertEqual(first.atm_strike, 57600)
        self.assertEqual(first.contracts["atm_ce"].symbol, CE)
        self.assertEqual(first.timestamp_utc, iso_utc(bar_start(DAY1, 9, 15)))
        self.assertEqual(strategy.index_bar_counts[10], 11)
        self.assertEqual(strategy.inputs[10].bars["5m"]["index"][10].timestamp_utc, iso_utc(bar_start(DAY1, 10, 5)))

        # Progress reached 100% and the summary carries the data notes.
        self.assertEqual(api.progress[-1]["percent"], 100.0)
        self.assertEqual(api.progress[-1]["barsProcessed"], 75)
        # The lot-size note itself is phrased by the API's run view; the summary
        # carries the facts it is built from.
        self.assertEqual(outcome.summary["lotSize"], 30)
        self.assertEqual(outcome.summary["lotSizeSource"], "master")

    def test_eod_square_off_at_1515(self):
        api = make_api(lambda i: 100.0 + i)
        outcome, _ = self.run_engine(api, {0: open_ce(1), 73: open_ce(1, "g2")}, run_row(DAY1, DAY1))

        self.assertEqual(outcome.status, "Completed")
        self.assertEqual(outcome.summary["eodSquareOffs"], 1)
        close_signals = [s for s in api.signals if s["signalType"] == "CLOSE_GROUP"]
        self.assertEqual(len(close_signals), 1)
        self.assertEqual(close_signals[0]["timestampUtc"], iso_utc(bar_start(DAY1, 15, 15)))
        self.assertIn('"reason": "End-of-day square-off 15:15 IST"', close_signals[0]["metadataJson"])
        self.assertEqual(close_signals[0]["legs"][0]["price"], 100.0 + 72)
        self.assertEqual(outcome.ledger.closed[0].exit_reason, "End-of-day square-off 15:15 IST")
        # The entry signalled at 15:20 is not taken and is listed, not dropped.
        self.assertEqual(len(outcome.summary["skippedEntries"]), 1)
        self.assertIn("after the EOD square-off", outcome.summary["skippedEntries"][0]["reason"])
        self.assertFalse(outcome.ledger.has_open())

    def test_no_eod_square_off_when_disabled(self):
        api = make_api(lambda i: 100.0 + i)
        outcome, _ = self.run_engine(api, {0: open_ce(1)}, run_row(DAY1, DAY1, eod_square_off_ist=""))
        self.assertEqual(outcome.summary["eodSquareOffs"], 0)
        close_signals = [s for s in api.signals if s["signalType"] == "CLOSE_GROUP"]
        self.assertEqual(len(close_signals), 1)
        self.assertIn("End of backtest", close_signals[0]["metadataJson"])
        self.assertEqual(close_signals[0]["timestampUtc"], iso_utc(bar_start(DAY1, 15, 25)))

    def test_stop_loss_trips_on_total_pnl(self):
        api = make_api(lambda i: 200.0 - 5.0 * i)      # -150 per bar per lot
        outcome, _ = self.run_engine(api, {0: open_ce(1)}, run_row(DAY1, DAY1, stop_loss=500))

        self.assertEqual(outcome.status, "Completed")
        self.assertTrue(outcome.stop_reason.startswith("Stop loss hit"), outcome.stop_reason)
        self.assertEqual(outcome.summary["stopReason"], outcome.stop_reason)
        self.assertEqual(outcome.summary["barsProcessed"], 5)       # bar 4: P&L -600 <= -500
        self.assertEqual(len(api.snapshots), 5)
        self.assertAlmostEqual(outcome.ledger.realized_pnl(), -600.0)
        self.assertFalse(outcome.ledger.has_open())
        self.assertEqual(api.signals[-1]["signalType"], "CLOSE_GROUP")
        self.assertEqual(api.signals[-1]["timestampUtc"], iso_utc(bar_start(DAY1, 9, 35)))

    def test_target_trips_on_total_pnl(self):
        api = make_api(lambda i: 100.0 + 10.0 * i)     # +300 per bar per lot
        outcome, _ = self.run_engine(api, {0: open_ce(1)}, run_row(DAY1, DAY1, target=1000))
        self.assertTrue(outcome.stop_reason.startswith("Target hit"), outcome.stop_reason)
        self.assertEqual(outcome.summary["barsProcessed"], 5)       # bar 4: +1200 >= 1000
        self.assertAlmostEqual(outcome.ledger.realized_pnl(), 1200.0)

    def test_charges_count_against_total_pnl(self):
        api = make_api(lambda i: 100.0)                # flat premium: P&L is charges only
        outcome, _ = self.run_engine(api, {0: open_ce(1)}, run_row(DAY1, DAY1, charges_per_lot=300))
        self.assertIsNone(outcome.stop_reason)
        self.assertAlmostEqual(outcome.ledger.charges, 600.0)      # entry + EOD exit, 300 per lot each
        self.assertAlmostEqual(outcome.ledger.total_pnl(), -600.0)
        self.assertEqual(outcome.summary["charges"], 600.0)

        # With a 500 stop the exit charges push total P&L through the stop right after the square-off.
        api = make_api(lambda i: 100.0)
        outcome, _ = self.run_engine(api, {0: open_ce(1)}, run_row(DAY1, DAY1, charges_per_lot=300, stop_loss=500))
        self.assertTrue(outcome.stop_reason.startswith("Stop loss hit"))
        self.assertEqual(outcome.summary["barsProcessed"], 73)     # stopped on the 15:15 bar

    def test_missing_premium_history_is_skipped_and_listed(self):
        api = make_api(with_ce=False)
        outcome, _ = self.run_engine(api, {3: open_ce(1)}, run_row(DAY1, DAY1))

        self.assertEqual(outcome.status, "Completed")
        self.assertEqual(api.signals, [])
        self.assertEqual(len(outcome.summary["skippedEntries"]), 1)
        entry = outcome.summary["skippedEntries"][0]
        self.assertEqual(entry["symbol"], CE)
        self.assertIn("no premium history", entry["reason"])
        self.assertEqual(entry["atUtc"], iso_utc(bar_start(DAY1, 9, 30)))
        # Skipped entries are grouped into notes by the API's run view; the
        # runner only reports the broker state.
        self.assertFalse(any("Skipped" in n for n in outcome.summary["dataNotes"]))
        self.assertTrue(any("Broker not linked" in n for n in outcome.summary["dataNotes"]))

    def test_sync_is_tried_once_when_broker_linked(self):
        rows = option_rows(CE, [DAY1], lambda i: 50.0 + i)
        api = make_api(with_ce=False, broker_linked=True, sync_rows={CE: rows})
        outcome, _ = self.run_engine(api, {1: open_ce(1), 2: close_ce(1)}, run_row(DAY1, DAY1))
        self.assertEqual(len(api.synced), 1)
        self.assertEqual(api.synced[0][:2], (CE, "5"))
        self.assertEqual(len(api.signals), 2)
        self.assertAlmostEqual(outcome.ledger.realized_pnl(), 1.0 * LOT_SIZE)

    def test_open_position_carried_to_next_day_is_squared_off_at_last_close(self):
        # Day 1 data ends at 14:00 IST (before 15:15) so the square-off happens at the day-change.
        candles = {(SPOT, "5"): index_rows([DAY1], last=time(14, 0)) + index_rows([DAY2])}
        candles[(CE, "5")] = option_rows(CE, [DAY1], lambda i: 100.0 + i, last=time(14, 0)) + option_rows(CE, [DAY2], lambda i: 300.0)
        candles[(PE, "5")] = option_rows(PE, [DAY1, DAY2], lambda i: 80.0)
        api = FakeApi(candles)
        outcome, _ = self.run_engine(api, {0: open_ce(1)}, run_row(DAY1, DAY2))

        self.assertEqual(outcome.summary["sessions"], 2)
        self.assertEqual(outcome.summary["eodSquareOffs"], 1)
        close_sig = [s for s in api.signals if s["signalType"] == "CLOSE_GROUP"][0]
        self.assertEqual(close_sig["timestampUtc"], iso_utc(bar_start(DAY1, 14, 0)))
        self.assertEqual(close_sig["legs"][0]["price"], 100.0 + 57)      # 09:15 + 57 x 5m = 14:00
        self.assertEqual(outcome.ledger.closed[0].closed_utc, iso_utc(bar_start(DAY1, 14, 0)))

    def test_buy_sell_signal_becomes_one_leg_open_group(self):
        def ghost_buy(inp, strategy):
            return [StrategySignal(strategy_name="Scripted", signal_type="BUY", timestamp_utc=inp.timestamp_utc,
                                   reason="Ghost Tangent Break", symbol=inp.underlying, price=inp.spot_price)]

        api = make_api(lambda i: 100.0 + i)
        outcome, _ = self.run_engine(api, {1: ghost_buy}, run_row(DAY1, DAY1, lots=3),
                                     requirements=[DataRequirement(symbol_type="index", resolution="5m")])
        open_sig = api.signals[0]
        self.assertEqual(open_sig["signalType"], "OPEN_GROUP")
        self.assertEqual(open_sig["legs"], [{"symbol": CE, "side": "BUY", "quantity": 3, "price": 101.0}])
        self.assertTrue(open_sig["groupId"].startswith("GTC_"))
        self.assertIn('"direction": "BUY"', open_sig["metadataJson"])
        self.assertEqual(outcome.summary["trades"], 1)      # closed by the EOD square-off

    def test_logical_symbols_are_resolved(self):
        def titli_open(inp, strategy):
            return [StrategySignal(strategy_name="Scripted", signal_type="OPEN_GROUP", timestamp_utc=inp.timestamp_utc,
                                   reason="Titli", legs=[{"symbol": f"{UNDERLYING}_CE_57600", "side": "SELL", "quantity": 1, "price": None},
                                                         {"symbol": f"{UNDERLYING}_PE_57600", "side": "SELL", "quantity": 1, "price": None}],
                                   metadata={"group_id": "T1"})]

        api = make_api(lambda i: 100.0)
        outcome, _ = self.run_engine(api, {0: titli_open}, run_row(DAY1, DAY1))
        legs = api.signals[0]["legs"]
        self.assertEqual({l["symbol"] for l in legs}, {CE, PE})
        self.assertEqual(outcome.summary["trades"], 2)

    def test_zero_bars_fails_with_backfill_hint(self):
        api = make_api()
        outcome, _ = self.run_engine(api, {}, run_row(date(2026, 8, 3), date(2026, 8, 4)))
        self.assertEqual(outcome.status, "Failed")
        self.assertIn("backfill first", outcome.error)
        self.assertEqual(api.completed[-1]["status"], "Failed")
        self.assertEqual(api.completed[-1]["error"], outcome.error)

    def test_strategy_exception_marks_run_failed(self):
        def boom(inp, strategy):
            raise RuntimeError("strategy blew up")

        api = make_api()
        outcome, _ = self.run_engine(api, {2: boom}, run_row(DAY1, DAY1))
        self.assertEqual(outcome.status, "Failed")
        self.assertIn("strategy blew up", outcome.error)
        self.assertEqual(api.completed[-1]["status"], "Failed")
        self.assertEqual(len(api.snapshots), 2)      # points before the failure were still flushed

    def test_frozen_lot_size_from_run_parameters_wins(self):
        api = make_api(lambda i: 100.0 + i)
        outcome, _ = self.run_engine(api, {2: open_ce(1), 5: close_ce(1)}, run_row(DAY1, DAY1, lot_size=65, lot_size_source="master"))
        self.assertAlmostEqual(outcome.ledger.realized_pnl(), 3.0 * 65)     # not the underlyings endpoint's 30
        self.assertEqual(outcome.summary["lotSize"], 65)
        self.assertEqual(outcome.summary["lotSizeSource"], "master")

    def test_equity_points_carry_cumulative_charges(self):
        api = make_api(lambda i: 100.0)
        self.run_engine(api, {0: open_ce(1)}, run_row(DAY1, DAY1, charges_per_lot=300))
        self.assertEqual(api.snapshots[0]["charges"], 300.0)
        self.assertEqual(api.snapshots[-1]["charges"], 600.0)     # entry + EOD exit

    def test_missing_required_index_resolution_fails_the_run(self):
        api = make_api(lambda i: 100.0 + i)
        outcome, strategy = self.run_engine(api, {2: open_ce(1)}, run_row(DAY1, DAY1),
                                            requirements=[DataRequirement(symbol_type="index", resolution="15m")])
        self.assertEqual(outcome.status, "Failed")
        self.assertIn("15m", outcome.error)
        self.assertIn("backfill", outcome.error)
        self.assertEqual(api.completed[-1]["status"], "Failed")
        self.assertTrue(any("15m" in n for n in outcome.summary["dataNotes"]))
        self.assertEqual(strategy.inputs, [])          # the strategy never ran on an empty feed
        self.assertEqual(api.signals, [])

    def test_contract_lookup_failure_is_retried_and_named(self):
        def ghost_buy(inp, strategy):
            return [StrategySignal(strategy_name="Scripted", signal_type="BUY", timestamp_utc=inp.timestamp_utc,
                                   reason="Ghost", symbol=inp.underlying, price=inp.spot_price)]

        api = make_api(lambda i: 100.0 + i)
        api.fail_contract_lookups = 2                   # the CE and PE lookups of the first bar both fail
        outcome, _ = self.run_engine(api, {0: ghost_buy, 2: ghost_buy}, run_row(DAY1, DAY1),
                                     requirements=[DataRequirement(symbol_type="index", resolution="5m")])

        skipped = outcome.summary["skippedEntries"]
        self.assertEqual(len(skipped), 1)
        self.assertIn("lookup failed", skipped[0]["reason"])
        self.assertNotIn("instrument master", skipped[0]["reason"])
        # Bar 2 found the contract because the failure was not cached as "missing".
        self.assertEqual([s["signalType"] for s in api.signals][0], "OPEN_GROUP")
        self.assertEqual(api.signals[0]["timestampUtc"], iso_utc(bar_start(DAY1, 9, 25)))
        self.assertEqual(api.contract_calls, 4)         # 2 failed + 2 cached answers
        self.assertTrue(any("lookup" in n for n in outcome.summary["dataNotes"]))

    def test_close_leg_matches_the_contract_it_was_opened_with_across_expiry(self):
        expiry_day = date(2026, 8, 25)                  # EXPIRY itself; the next bar day rolls to 26SEP
        next_day = date(2026, 8, 26)
        aug_ce = contract_symbol(57600, "CE", "2026-08-25")
        sep_ce = contract_symbol(57600, "CE", "2026-09-29")
        candles = {(SPOT, "5"): index_rows([expiry_day, next_day])}
        candles[(aug_ce, "5")] = option_rows(aug_ce, [expiry_day], lambda i: 100.0) + option_rows(aug_ce, [next_day], lambda i: 90.0)
        candles[(sep_ce, "5")] = option_rows(sep_ce, [expiry_day, next_day], lambda i: 250.0)
        api = FakeApi(candles)

        def titli_open(inp, strategy):
            return [StrategySignal(strategy_name="Scripted", signal_type="OPEN_GROUP", timestamp_utc=inp.timestamp_utc,
                                   reason="Titli", legs=[{"symbol": f"{UNDERLYING}_CE_57600", "side": "SELL", "quantity": 1}],
                                   metadata={"group_id": "T1"})]

        def titli_close(inp, strategy):
            return [StrategySignal(strategy_name="Scripted", signal_type="CLOSE_GROUP", timestamp_utc=inp.timestamp_utc,
                                   reason="Titli exit", legs=[{"symbol": f"{UNDERLYING}_CE_57600", "side": "BUY", "quantity": 1}],
                                   metadata={"group_id": "T1"})]

        # Bar 74 is the last bar of the expiry day; bar 75 is the first of the next day.
        outcome, _ = self.run_engine(api, {74: titli_open, 76: titli_close},
                                     run_row(expiry_day, next_day, eod_square_off_ist=""))

        self.assertEqual(outcome.status, "Completed")
        open_sig, close_sig = api.signals
        self.assertEqual(open_sig["legs"][0]["symbol"], aug_ce)
        self.assertEqual(close_sig["signalType"], "CLOSE_GROUP")
        self.assertEqual(close_sig["legs"][0]["symbol"], aug_ce)         # not the 26SEP contract
        self.assertEqual(close_sig["legs"][0]["price"], 90.0)
        self.assertEqual(close_sig["timestampUtc"], iso_utc(bar_start(next_day, 9, 20)))
        self.assertEqual(outcome.ledger.ignored_legs, 0)
        self.assertEqual(outcome.ledger.closed[0].exit_reason, "Titli exit")
        self.assertAlmostEqual(outcome.ledger.realized_pnl(), 10.0 * LOT_SIZE)  # short 100 -> 90
        self.assertFalse(outcome.ledger.has_open())

    def test_stop_loss_is_checked_right_after_the_rollover_square_off(self):
        # Day 1 data ends at 14:00 (58 bars). Long CE from 100 drifts to 85 at the last bar:
        # unrealized -450, entry charge 30 -> total -480, above the 500 stop. The rollover
        # square-off books -450 realized plus a 30 exit charge -> -510 <= -500, so day 2 must
        # begin with the stop, not with the entry the strategy would signal on its first bar.
        candles = {(SPOT, "5"): index_rows([DAY1], last=time(14, 0)) + index_rows([DAY2])}
        candles[(CE, "5")] = option_rows(CE, [DAY1], lambda i: 100.0 - 15.0 * min(i, 57) / 57, last=time(14, 0)) \
            + option_rows(CE, [DAY2], lambda i: 85.0)
        candles[(PE, "5")] = option_rows(PE, [DAY1, DAY2], lambda i: 80.0)
        api = FakeApi(candles)
        outcome, strategy = self.run_engine(api, {0: open_ce(1), 58: open_ce(1, "g2")},
                                            run_row(DAY1, DAY2, charges_per_lot=30, stop_loss=500))

        self.assertTrue(outcome.stop_reason.startswith("Stop loss hit"), outcome.stop_reason)
        self.assertEqual([s["signalType"] for s in api.signals], ["OPEN_GROUP", "CLOSE_GROUP"])
        self.assertEqual(outcome.summary["barsProcessed"], 59)
        self.assertEqual(len(strategy.inputs), 58)     # on_bar never ran on the stop bar
        self.assertAlmostEqual(outcome.ledger.total_pnl(), -510.0)
        self.assertEqual(outcome.summary["trades"], 1)


if __name__ == "__main__":
    unittest.main()
