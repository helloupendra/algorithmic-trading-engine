"""
LogicEngine rule tests.

Every rule reads live market data through the platform API, so each test fakes
just the calls that rule makes. The point of most of these is the *negative*
case: the feed does not carry a session VWAP, top-of-book depth on index
symbols, or open interest on every contract, and a rule with no data must stay
silent instead of firing on a fabricated number.
"""

import unittest
from datetime import datetime, timedelta, timezone
from unittest.mock import MagicMock, patch

import _bootstrap  # noqa: F401

from strategies.base_strategy import StrategyInput
from strategies.logic_engine import IST, LogicEngine

SPOT = "NSE:NIFTYBANK-INDEX"
CE = "NSE:BANKNIFTY26SEP57500CE"
PE = "NSE:BANKNIFTY26SEP57500PE"


def bar(day: str, hh: int, mm: int, close: float, volume: float) -> dict:
    """One stored 1m bar, timestamped in UTC the way the API returns it."""
    start = datetime.fromisoformat(f"{day}T{hh:02d}:{mm:02d}:00+05:30").astimezone(timezone.utc)
    return {
        "symbol": "NSE:HDFCBANK-EQ",
        "resolution": "1m",
        "barStartUtc": start.isoformat().replace("+00:00", "Z"),
        "open": close,
        "high": close,
        "low": close,
        "close": close,
        "volumeDelta": volume,
        "tickCount": 1,
    }


def chain_row(strike: float, option_type: str) -> dict:
    return {
        "symbol": f"NSE:BANKNIFTY26SEP{int(strike)}{option_type}",
        "underlying": "BANKNIFTY",
        "expiryDate": "2026-09-29",
        "strikePrice": strike,
        "optionType": option_type,
    }


class LogicEngineTests(unittest.TestCase):
    def setUp(self):
        self.redis_patch = patch('redis.Redis')
        self.redis_class = self.redis_patch.start()
        self.redis_client = MagicMock()
        self.redis_class.return_value = self.redis_client

        self.thread_patch = patch('threading.Thread')
        self.thread_patch.start()

        self.engine = LogicEngine({"cooldown_seconds": 0, "ask_bid_ratio": 3.0})
        self.engine.api = MagicMock()

    def tearDown(self):
        self.redis_patch.stop()
        self.thread_patch.stop()

    def make_input(self, spot=57550.0, atm=57500, contracts=None):
        return StrategyInput(
            mode="LivePaper",
            underlying="BANKNIFTY",
            spot_price=spot,
            atm_strike=atm,
            timestamp_utc=datetime.now(timezone.utc),
            contracts=contracts if contracts is not None else {
                "atm_ce": MagicMock(symbol=CE),
                "atm_pe": MagicMock(symbol=PE),
            },
        )

    # --- state ------------------------------------------------------------

    def test_initialize_state(self):
        state = self.engine.initialize_state()
        self.assertIn("last_alert_time", state)
        self.assertIn("last_highest_call_oi_strike", state)
        self.assertIn("last_highest_put_oi_strike", state)

    # --- session VWAP -----------------------------------------------------

    def test_session_vwap_is_volume_weighted_over_the_newest_session(self):
        today = datetime.now(IST).date().isoformat()
        yesterday = (datetime.now(IST) - timedelta(days=1)).date().isoformat()
        self.engine.api.get_recent_bars.return_value = [
            bar(today, 9, 15, 100.0, 100),      # today: 100 @ 100 lots
            bar(today, 9, 16, 110.0, 300),      # today: 110 @ 300 lots -> vwap 107.5
            bar(yesterday, 15, 20, 999.0, 500),  # previous session must not count
        ]

        value, source = self.engine._session_vwap("NSE:HDFCBANK-EQ")

        self.assertEqual(source, "vwap")
        self.assertAlmostEqual(value, 107.5, places=6)

    def test_session_vwap_falls_back_to_ltp_without_volume(self):
        today = datetime.now(IST).date().isoformat()
        self.engine.api.get_recent_bars.return_value = [bar(today, 9, 15, 57000.0, 0)]
        self.engine._get_contract_ltp = MagicMock(return_value=57480.0)

        value, source = self.engine._session_vwap(SPOT)

        self.assertEqual(source, "ltp")
        self.assertEqual(value, 57480.0)

    def test_equity_breakout_stays_silent_when_vwap_is_only_the_ltp(self):
        """Spot above its own last traded price is not a breakout."""
        self.engine._session_vwap = MagicMock(return_value=(700.0, "ltp"))
        self.engine._fetch_15m_high_low = MagicMock(return_value=(0.0, 0.0))
        self.engine._highest_oi_strikes = MagicMock(return_value=(None, None, 100.0))
        self.engine._top_of_book = MagicMock(return_value=None)

        state = self.engine.initialize_state()
        inp = self.make_input(spot=750.0)
        inp.underlying = "HDFCBANK"          # takes the equity branch

        self.assertEqual(self.engine.on_bar(state, inp), [])
        self.assertFalse(self.redis_client.publish.called)

    # --- rule 1 (index bear trap) ----------------------------------------

    def test_index_bear_trap_fires_when_a_heavyweight_breaks_its_low(self):
        state = self.engine.initialize_state()
        inp = self.make_input()

        def fifteen_minute(symbol):
            return (44000.0, 43000.0) if symbol == SPOT else (1000.0, 900.0)

        self.engine._fetch_15m_high_low = MagicMock(side_effect=fifteen_minute)
        # Heavyweight trading below its 15m low, index above its 15m high.
        self.engine._get_contract_ltp = MagicMock(return_value=800.0)

        signals = self.engine.on_bar(state, inp)

        self.assertEqual(len(signals), 1)
        self.assertEqual(signals[0].signal_type, "ALERT")
        self.assertTrue(self.redis_client.publish.called)

    # --- rule 2 (open interest) ------------------------------------------

    def test_oi_rule_is_skipped_when_the_feed_has_no_open_interest(self):
        self.engine.api.get_expiries.return_value = [{"expiryDate": "2026-09-29"}]
        self.engine.api.get_option_chain.return_value = [chain_row(57500, "CE"), chain_row(57600, "CE")]
        self.engine.api.get_all_latest_quotes.return_value = [
            {"symbol": "NSE:BANKNIFTY26SEP57500CE", "openInterest": None},
            {"symbol": "NSE:BANKNIFTY26SEP57600CE", "openInterest": None},
        ]

        ce, pe, _step = self.engine._highest_oi_strikes("BANKNIFTY", 57500)

        self.assertIsNone(ce)
        self.assertIsNone(pe)

    def test_highest_oi_strike_comes_from_the_chain_within_the_window(self):
        self.engine.strike_window = 2      # +/- 200 points on a 100-point grid
        self.engine.api.get_expiries.return_value = [{"expiryDate": "2026-09-29"}]
        self.engine.api.get_option_chain.return_value = [
            chain_row(57400, "CE"), chain_row(57500, "CE"), chain_row(57600, "CE"),
            chain_row(58200, "CE"),                       # outside the window
            chain_row(57300, "PE"), chain_row(57400, "PE"),
        ]
        self.engine.api.get_all_latest_quotes.return_value = [
            {"symbol": "NSE:BANKNIFTY26SEP57400CE", "openInterest": 10},
            {"symbol": "NSE:BANKNIFTY26SEP57500CE", "openInterest": 90},
            {"symbol": "NSE:BANKNIFTY26SEP57600CE", "openInterest": 40},
            {"symbol": "NSE:BANKNIFTY26SEP58200CE", "openInterest": 9999},   # ignored: out of window
            {"symbol": "NSE:BANKNIFTY26SEP57300PE", "openInterest": 70},
            {"symbol": "NSE:BANKNIFTY26SEP57400PE", "openInterest": 20},
        ]

        ce, pe, step = self.engine._highest_oi_strikes("BANKNIFTY", 57500)

        self.assertEqual(ce, 57500.0)
        self.assertEqual(pe, 57300.0)
        self.assertEqual(step, 100.0)

    def test_bearish_oi_shift_alerts_only_after_a_real_move(self):
        state = self.engine.initialize_state()
        state["last_highest_call_oi_strike"] = 57700.0
        inp = self.make_input()

        self.engine._fetch_15m_high_low = MagicMock(return_value=(99999.0, 0.0))  # rule 1 silent
        self.engine._top_of_book = MagicMock(return_value=None)                   # rule 3 silent
        self.engine._get_contract_ltp = MagicMock(return_value=120.0)
        # Peak call OI moved 57700 -> 57500, i.e. two strikes closer to the money.
        self.engine._highest_oi_strikes = MagicMock(return_value=(57500.0, None, 100.0))

        signals = self.engine.on_bar(state, inp)

        self.assertEqual(len(signals), 1)
        self.assertIn("OI shift", signals[0].reason)
        self.assertEqual(state["last_highest_call_oi_strike"], 57500.0)

    def test_no_oi_alert_when_the_peak_has_not_moved_enough(self):
        state = self.engine.initialize_state()
        state["last_highest_call_oi_strike"] = 57500.0
        inp = self.make_input()

        self.engine._fetch_15m_high_low = MagicMock(return_value=(99999.0, 0.0))
        self.engine._top_of_book = MagicMock(return_value=None)
        self.engine._highest_oi_strikes = MagicMock(return_value=(57500.0, None, 100.0))

        self.assertEqual(self.engine.on_bar(state, inp), [])
        self.assertFalse(self.redis_client.publish.called)

    # --- rule 3 (order-book imbalance) -----------------------------------

    def test_top_of_book_returns_none_when_the_feed_has_no_depth(self):
        self.engine.api.get_recent_ticks.return_value = [
            {"symbol": SPOT, "bidSize": None, "askSize": None}
        ]
        self.assertIsNone(self.engine._top_of_book(SPOT))

    def test_top_of_book_reads_the_option_tick(self):
        self.engine.api.get_recent_ticks.return_value = [{"symbol": CE, "bidSize": 30, "askSize": 90}]
        self.assertEqual(self.engine._top_of_book(CE), (30.0, 90.0))

    def test_imbalance_rule_fires_on_a_lopsided_option_book_near_resistance(self):
        state = self.engine.initialize_state()
        inp = self.make_input(spot=57595.0, atm=57500)   # resistance 57600, within 20 points

        self.engine._fetch_15m_high_low = MagicMock(return_value=(99999.0, 0.0))
        self.engine._highest_oi_strikes = MagicMock(return_value=(None, None, 100.0))
        self.engine._top_of_book = MagicMock(return_value=(30.0, 120.0))   # ask = 4x bid
        self.engine._get_contract_ltp = MagicMock(return_value=210.0)

        signals = self.engine.on_bar(state, inp)

        self.assertEqual(len(signals), 1)
        self.assertIn("Selling Pressure", signals[0].reason)

    def test_imbalance_rule_is_silent_without_depth(self):
        state = self.engine.initialize_state()
        inp = self.make_input(spot=57595.0, atm=57500)

        self.engine._fetch_15m_high_low = MagicMock(return_value=(99999.0, 0.0))
        self.engine._highest_oi_strikes = MagicMock(return_value=(None, None, 100.0))
        self.engine._top_of_book = MagicMock(return_value=None)

        self.assertEqual(self.engine.on_bar(state, inp), [])
        self.assertFalse(self.redis_client.publish.called)

    # --- cooldown ---------------------------------------------------------

    def test_cooldown_suppresses_a_second_alert(self):
        engine = self.engine
        engine.cooldown_seconds = 300
        state = engine.initialize_state()
        inp = self.make_input()

        def fifteen_minute(symbol):
            return (44000.0, 43000.0) if symbol == SPOT else (1000.0, 900.0)

        engine._fetch_15m_high_low = MagicMock(side_effect=fifteen_minute)
        engine._get_contract_ltp = MagicMock(return_value=800.0)
        engine._highest_oi_strikes = MagicMock(return_value=(None, None, 100.0))
        engine._top_of_book = MagicMock(return_value=None)

        first = engine.on_bar(state, inp)
        second = engine.on_bar(state, inp)

        self.assertEqual(len(first), 1)
        self.assertEqual(second, [], "a second alert inside the cooldown window must be suppressed")


if __name__ == '__main__':
    unittest.main()
