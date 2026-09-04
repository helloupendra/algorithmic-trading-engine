import unittest
from unittest.mock import MagicMock, patch
from datetime import datetime, timezone
import json
from strategies.base_strategy import StrategyInput
from strategies.logic_engine import LogicEngine

class TestLogicEngine(unittest.TestCase):

    def setUp(self):
        # Mock Redis
        self.mock_redis_patch = patch('redis.Redis')
        self.mock_redis_class = self.mock_redis_patch.start()
        self.mock_redis_client = MagicMock()
        self.mock_redis_class.return_value = self.mock_redis_client

        # Mock Threading
        self.mock_thread_patch = patch('threading.Thread')
        self.mock_thread_class = self.mock_thread_patch.start()

        # Initialize Logic Engine
        self.engine = LogicEngine({"cooldown_seconds": 0, "ask_bid_ratio": 3.0})
        
        # Mock API Client
        self.engine.api = MagicMock()

    def tearDown(self):
        self.mock_redis_patch.stop()
        self.mock_thread_patch.stop()

    def test_initialize_state(self):
        state = self.engine.initialize_state()
        self.assertIn("last_alert_time", state)
        self.assertIn("last_highest_call_oi_strike", state)

    def test_vwap_fallback_to_ltp(self):
        self.engine.api.get_latest_quote.return_value = {"lastTradedPrice": 100.0}
        val = self.engine._get_vwap_or_ltp("TEST")
        self.assertEqual(val, 100.0)

        self.engine.api.get_latest_quote.return_value = {"lastTradedPrice": 100.0, "volumeWeightedAveragePrice": 105.0}
        val = self.engine._get_vwap_or_ltp("TEST")
        self.assertEqual(val, 105.0)

    def test_on_bar_rule_1_index_breakout(self):
        state = self.engine.initialize_state()
        
        inp = StrategyInput(
            underlying="BANKNIFTY",
            spot_price=45000.0,
            atm_strike=45000,
            timestamp_utc=datetime.now(timezone.utc),
            contracts={"atm_pe": MagicMock(symbol="PE_SYM")}
        )

        # Mock 15m high/low for index and heavyweights
        def fetch_15m_mock(symbol):
            if symbol == "NSE:NIFTYBANK-INDEX":
                return 44000.0, 43000.0
            return 1000.0, 900.0 # heavyweights
            
        self.engine._fetch_15m_high_low = MagicMock(side_effect=fetch_15m_mock)
        
        # Mock VWAP
        def get_vwap_mock(symbol):
            if symbol in self.engine.heavyweights:
                return 800.0 # Breaks below day low (900.0)
            return 1000.0
            
        self.engine._get_vwap_or_ltp = MagicMock(side_effect=get_vwap_mock)
        self.engine._get_contract_ltp = MagicMock(return_value=150.0)

        signals = self.engine.on_bar(state, inp)
        
        self.assertEqual(len(signals), 1)
        self.assertEqual(signals[0].signal_type, "ALERT")
        self.assertTrue(self.mock_redis_client.publish.called)

if __name__ == '__main__':
    unittest.main()
