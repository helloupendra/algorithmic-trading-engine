"""
The runner's side of Market Replay: a recap run trades a past day through the
same runner the live desk uses.

What a recap run must do differently, and what a live run must not: quotes
from the API's ReplayBook (replay=true), bars bounded by the tick's exchange
stamp (untilUtc), the expiry and contracts as of the replayed day (expired ones
included), a strike step even when that expiry has left the chain, only
replayed ticks (and a live run only live ones), a Redis key telling the player
it is listening, and a live watchlist it never touches. No API or Redis is
needed: each test fakes the few calls it makes.
"""

import contextlib
import io
import unittest
from datetime import date, datetime, timezone
from unittest import mock

import _bootstrap  # noqa: F401

import strategies.execution_runner as runner
from core.api_client import PlatformApiClient, utc_query_stamp
from core.recap_session import RecapSession
from messaging.redis_subscriber import RedisTickSubscriber
from strategies.base_strategy import StrategyInput, StrategySignal
from strategies.contract_selector import ExactContractCache, first_expiry_on_or_after

DAY = date(2026, 9, 15)                       # a Tuesday; NIFTY's weekly expired on the 16th
RECAP = RecapSession(DAY)
TICK = "2026-09-15T04:31:07.250000+00:00"     # 10:01:07 IST on the replayed day
EXPIRIES = [{"expiryDate": d} for d in ("2026-09-09", "2026-09-16", "2026-09-23", "2026-09-30", "2026-10-07")]


def quiet(fn, *args, **kwargs):
    with contextlib.redirect_stdout(io.StringIO()):
        return fn(*args, **kwargs)


class FakeResponse:
    def __init__(self, body=None, status=200):
        self._body = body if body is not None else {}
        self.status_code = status
        self.content = b"x"

    def raise_for_status(self):
        pass

    def json(self):
        return self._body


def client(replay=False):
    """A real PlatformApiClient whose HTTP session is a recorder."""
    api = PlatformApiClient("http://api.test", replay=replay)
    api.http = mock.MagicMock()
    api.http.get.return_value = FakeResponse([])
    return api


class FakeApi:
    """The runner's few API calls, recorded."""

    def __init__(self, expiries=None, chain=None, inventory=None, contracts=None, quotes=None, bars=None):
        self.expiries = EXPIRIES if expiries is None else expiries
        self.chain = chain or []
        self.inventory = inventory or []
        self.contracts = contracts or {}
        self.quotes = quotes or []
        self.bars = bars or []
        self.calls = []

    def get_expiries(self, underlying, include_history=False):
        self.calls.append(("expiries", underlying, include_history))
        return self.expiries

    def get_option_chain(self, underlying, expiry):
        self.calls.append(("chain", underlying, expiry))
        return self.chain

    def get_fno_underlyings(self):
        self.calls.append(("inventory",))
        return self.inventory

    def get_exact_contract(self, underlying, expiry, strike, option_type, **kwargs):
        self.calls.append(("contract", underlying, expiry, strike, option_type, kwargs))
        return self.contracts.get((expiry, strike, option_type))

    def get_recent_bars(self, symbol, resolution="1m", take=1, **kwargs):
        self.calls.append(("bars", symbol, resolution, take, kwargs))
        return self.bars

    def get_all_latest_quotes(self):
        self.calls.append(("quotes",))
        return self.quotes

    def upsert_watchlist(self, symbol, priority=50):
        self.calls.append(("watchlist", symbol))
        return {}

    def named(self, name):
        return [c for c in self.calls if c[0] == name]


# --------------------------------------------------------------- the client --

class ClientReplayModeTests(unittest.TestCase):
    def test_a_live_client_asks_exactly_as_before(self):
        api = client()
        api.get_latest_quote("NSE:NIFTY50-INDEX")
        api.get_all_latest_quotes()
        api.get_recent_bars("NSE:NIFTY50-INDEX", resolution="5m", take=500)
        (_, latest), (_, every), (_, bars) = [(c.args, c.kwargs) for c in api.http.get.call_args_list]
        self.assertEqual({"symbol": "NSE:NIFTY50-INDEX"}, latest["params"])
        self.assertIsNone(every["params"], "no query on the bulk read of a live run")
        self.assertEqual({"symbol": "NSE:NIFTY50-INDEX", "resolution": "5m", "take": 500}, bars["params"])

    def test_a_replay_client_reads_quotes_from_the_replay(self):
        api = client(replay=True)
        api.get_latest_quote("NSE:NIFTY2691624900CE")
        api.get_all_latest_quotes()
        latest, every = [c.kwargs["params"] for c in api.http.get.call_args_list]
        self.assertEqual({"symbol": "NSE:NIFTY2691624900CE", "replay": "true"}, latest)
        self.assertEqual({"replay": "true"}, every)
        self.assertTrue(api.http.get.call_args_list[1].args[0].endswith("/api/LiveData/latest/all"))

    def test_the_replay_flag_can_be_set_after_construction(self):
        # The runner reads the run's parameters with the client before it knows
        # it is a recap run, and switches it then.
        api = client()
        api.replay = True
        api.get_latest_quote("NSE:NIFTY50-INDEX")
        self.assertEqual("true", api.http.get.call_args.kwargs["params"]["replay"])

    def test_bars_are_bounded_by_the_moment_given(self):
        api = client(replay=True)
        api.get_recent_bars("NSE:NIFTY50-INDEX", resolution="1m", take=500, until_utc=TICK)
        params = api.http.get.call_args.kwargs["params"]
        self.assertEqual("2026-09-15T04:31:07.250000Z", params["untilUtc"])
        self.assertNotIn("replay", params, "bars carry the bound, not the quote flag")

    def test_bars_without_a_bound_are_the_newest(self):
        api = client(replay=True)
        api.get_recent_bars("NSE:NIFTY50-INDEX", take=5)
        self.assertNotIn("untilUtc", api.http.get.call_args.kwargs["params"])

    def test_query_stamps_are_utc_with_a_z(self):
        self.assertEqual("2026-09-15T04:31:07Z", utc_query_stamp("2026-09-15T10:01:07+05:30"))
        self.assertEqual("2026-09-15T04:31:07Z", utc_query_stamp("2026-09-15T04:31:07Z"))
        self.assertEqual("2026-09-15T04:31:07Z", utc_query_stamp("2026-09-15T04:31:07"))       # naive is UTC
        self.assertEqual("2026-09-15T04:31:07Z",
                         utc_query_stamp(datetime(2026, 9, 15, 4, 31, 7, tzinfo=timezone.utc)))
        self.assertEqual("not a time", utc_query_stamp("not a time"))


# ------------------------------------------------------------------- expiry --

class ExpiryAsOfTheReplayedDayTests(unittest.TestCase):
    def test_a_recap_trades_the_expiry_nearest_the_replayed_day_from_the_history(self):
        api = FakeApi()
        expiries, expiry = runner.choose_expiry(api, "NIFTY", RECAP, today="2026-10-01")
        self.assertEqual("2026-09-16", expiry, "the weekly of the 15th, though it has expired since")
        self.assertEqual([("expiries", "NIFTY", True)], api.calls)
        self.assertEqual(EXPIRIES, expiries)

    def test_on_its_expiry_day_a_recap_trades_that_expiry(self):
        _, expiry = runner.choose_expiry(FakeApi(), "NIFTY", RecapSession(date(2026, 9, 16)))
        self.assertEqual("2026-09-16", expiry)

    def test_a_live_run_still_takes_the_listed_expiries_from_today(self):
        api = FakeApi()
        _, expiry = runner.choose_expiry(api, "NIFTY", None, today="2026-09-24")
        self.assertEqual("2026-09-30", expiry)
        self.assertEqual([("expiries", "NIFTY", False)], api.calls, "no history asked for on a live run")

    def test_no_expiry_on_or_after_the_day_is_none_not_a_guess(self):
        api = FakeApi(expiries=[{"expiryDate": "2026-09-09"}])
        expiries, expiry = runner.choose_expiry(api, "NIFTY", RECAP)
        self.assertIsNone(expiry)
        self.assertEqual(1, len(expiries), "the caller still sees that expiries exist")

    def test_first_expiry_on_or_after(self):
        self.assertEqual("2026-09-23", first_expiry_on_or_after(EXPIRIES, "2026-09-17"))
        self.assertIsNone(first_expiry_on_or_after([], "2026-09-17"))


# -------------------------------------------------------------- strike step --

class StrikeStepTests(unittest.TestCase):
    CHAIN = [{"strikePrice": k} for k in (24800, 24850, 24900, 24950)]

    def test_the_chain_still_decides_when_it_lists_the_expiry(self):
        api = FakeApi(chain=self.CHAIN)
        self.assertEqual(50.0, quiet(runner.resolve_strike_step, api, "NIFTY", "2026-09-16", listed_fallback=True))
        self.assertEqual([], api.named("inventory"))

    def test_an_expired_expiry_takes_the_step_of_the_contracts_listed_now(self):
        api = FakeApi(inventory=[{"underlying": "MIDCPNIFTY", "strikeStep": 25}, {"underlying": "RELIANCE", "strikeStep": 10}])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            step = runner.resolve_strike_step(api, "RELIANCE", "2026-09-30", listed_fallback=True)
        self.assertEqual(10.0, step, "a stock's grid, not the 50-point default")
        self.assertIn("expired since the replayed day", out.getvalue())

    def test_then_the_known_steps(self):
        for underlying, step in (("NIFTY", 50.0), ("BANKNIFTY", 100.0), ("SENSEX", 100.0),
                                 ("FINNIFTY", 50.0), ("MIDCPNIFTY", 25.0)):
            api = FakeApi()      # an empty chain and an empty inventory
            self.assertEqual(step, quiet(runner.resolve_strike_step, api, underlying, "2026-09-16",
                                         listed_fallback=True), underlying)

    def test_a_broken_inventory_falls_back_to_the_known_step(self):
        api = FakeApi()
        api.get_fno_underlyings = mock.Mock(side_effect=RuntimeError("API down"))
        self.assertEqual(100.0, quiet(runner.resolve_strike_step, api, "BANKNIFTY", "2026-09-16", listed_fallback=True))

    def test_a_live_run_never_asks_the_inventory(self):
        api = FakeApi(inventory=[{"underlying": "NIFTY", "strikeStep": 100}])
        self.assertEqual(50.0, quiet(runner.resolve_strike_step, api, "NIFTY", "2026-10-07"))
        self.assertEqual([], api.named("inventory"))


# ---------------------------------------------------------------- contracts --

class ContractLookupTests(unittest.TestCase):
    CE = {"symbol": "NSE:NIFTY2691624900CE", "strikePrice": 24900, "optionType": "CE", "expiryDate": "2026-09-16"}

    def test_a_recap_run_finds_contracts_that_have_expired_since(self):
        api = FakeApi(contracts={("2026-09-16", 24900, "CE"): self.CE})
        cache = ExactContractCache(api, "NIFTY", log=lambda _line: None, include_history=True)
        self.assertEqual(self.CE, cache.get("2026-09-16", 24900, "CE"))
        self.assertEqual({"include_history": True}, api.named("contract")[0][5])

    def test_a_live_lookup_is_the_call_it_always_was(self):
        api = FakeApi(contracts={("2026-09-16", 24900, "CE"): self.CE})
        ExactContractCache(api, "NIFTY", log=lambda _line: None).get("2026-09-16", 24900, "CE")
        self.assertEqual({}, api.named("contract")[0][5])


# -------------------------------------------------------------------- ticks --

class TickSessionFilterTests(unittest.TestCase):
    LIVE = {"symbol": "NSE:NIFTY50-INDEX", "lastTradedPrice": 24900.0}
    REPLAYED = {**LIVE, "isReplay": True, "sourceKey": "desk-replay"}

    def test_a_live_run_passes_over_replayed_ticks(self):
        self.assertTrue(runner.tick_is_for_this_run(self.LIVE, recap_run=False))
        self.assertFalse(runner.tick_is_for_this_run(self.REPLAYED, recap_run=False))

    def test_a_recap_run_takes_only_replayed_ticks(self):
        self.assertTrue(runner.tick_is_for_this_run(self.REPLAYED, recap_run=True))
        self.assertFalse(runner.tick_is_for_this_run(self.LIVE, recap_run=True))

    def test_what_counts_as_replayed(self):
        for flag, expected in ((True, True), ("true", True), ("True", True), (1, True),
                               (False, False), ("false", False), (None, False), (0, False)):
            self.assertEqual(expected, runner.is_replay_tick({**self.LIVE, "isReplay": flag}), repr(flag))
        self.assertFalse(runner.is_replay_tick(self.LIVE), "no flag is a live tick")


# ---------------------------------------------------------------- listening --

class ListeningKeyTests(unittest.TestCase):
    def test_a_recap_runner_says_it_is_listening_for_six_hours(self):
        redis_client = mock.MagicMock()
        at = datetime(2026, 10, 1, 14, 0, 5, tzinfo=timezone.utc)
        self.assertTrue(runner.announce_recap_listening(redis_client, 42, now=at))
        redis_client.set.assert_called_once_with("recap:listening:42", "2026-10-01T14:00:05Z", ex=6 * 60 * 60)

    def test_redis_trouble_is_not_fatal(self):
        redis_client = mock.MagicMock()
        redis_client.set.side_effect = ConnectionError("redis is down")
        self.assertFalse(quiet(runner.announce_recap_listening, redis_client, 42))
        redis_client.delete.side_effect = ConnectionError("redis is down")
        runner.forget_recap_listening(redis_client, 42)       # nothing raised

    def test_a_stopped_runner_takes_its_key_back(self):
        redis_client = mock.MagicMock()
        runner.forget_recap_listening(redis_client, 42)
        redis_client.delete.assert_called_once_with("recap:listening:42")

    def test_the_stream_position_is_pinned_before_the_player_is_told(self):
        subscriber = RedisTickSubscriber()
        subscriber.client = mock.MagicMock()
        subscriber.client.xinfo_stream.return_value = {"length": 9, "last-generated-id": "1727790005000-3"}
        self.assertTrue(subscriber.start_from_now())
        self.assertEqual("1727790005000-3", subscriber.last_id)

    def test_a_stream_that_cannot_be_asked_stays_at_dollar(self):
        subscriber = RedisTickSubscriber()
        subscriber.client = mock.MagicMock()
        subscriber.client.xinfo_stream.side_effect = RuntimeError("no such key")
        self.assertFalse(subscriber.start_from_now())
        self.assertEqual("$", subscriber.last_id)


# --------------------------------------------------------------------- bars --

class RecentBarsTests(unittest.TestCase):
    ROWS = [  # newest first, as the API returns them
        {"barStartUtc": "2026-09-15T04:32:00Z", "close": 3.0},     # after the tick: an API that ignored the bound
        {"barStartUtc": "2026-09-15T04:31:00Z", "close": 2.0},     # the minute in progress
        {"barStartUtc": "2026-09-15T04:30:00Z", "close": 1.0},
    ]

    def test_a_recap_asks_for_bars_up_to_its_tick_and_still_drops_any_later_one(self):
        api = FakeApi(bars=self.ROWS)
        rows = runner.recent_bars(api, "NSE:NIFTY50-INDEX", "1m", RECAP, TICK)
        self.assertEqual([2.0, 1.0], [r["close"] for r in rows])
        self.assertEqual([("bars", "NSE:NIFTY50-INDEX", "1m", 500, {"until_utc": TICK})], api.calls)

    def test_a_live_run_reads_the_newest_bars_as_before(self):
        api = FakeApi(bars=self.ROWS)
        rows = runner.recent_bars(api, "NSE:NIFTY50-INDEX", "5m", None, TICK)
        self.assertEqual(self.ROWS, rows)
        self.assertEqual([("bars", "NSE:NIFTY50-INDEX", "5m", 500, {})], api.calls)


# -------------------------------------------------------------- signal legs --

class SignalLegTests(unittest.TestCase):
    CONTRACT = {"symbol": "NSE:NIFTY2691624900PE", "strikePrice": 24900, "optionType": "PE"}

    def signal(self):
        # A close waits for no price, so nothing here sleeps.
        return StrategySignal(strategy_name="Fulcrum", signal_type="CLOSE_GROUP", timestamp_utc=TICK,
                              reason="test", legs=[{"symbol": "NIFTY_PE_24900", "side": "BUY", "quantity": 1}])

    def api(self):
        return FakeApi(contracts={("2026-09-16", 24900, "PE"): self.CONTRACT},
                       quotes=[{"symbol": "NSE:NIFTY2691624900PE", "lastTradedPrice": 88.5,
                                "updatedUtc": datetime.now(timezone.utc).isoformat()}])

    def test_a_recap_run_leaves_the_live_watchlist_alone_and_resolves_expired_contracts(self):
        api = self.api()
        sig = quiet(runner.enrich_signal_leg_prices, api, self.signal(), "2026-09-16", recap_run=True)
        self.assertEqual([], api.named("watchlist"))
        self.assertEqual({"include_history": True}, api.named("contract")[0][5])
        self.assertEqual("NSE:NIFTY2691624900PE", sig.legs[0]["symbol"])
        self.assertEqual(88.5, sig.legs[0]["price"])

    def test_a_live_run_still_subscribes_its_legs(self):
        api = self.api()
        quiet(runner.enrich_signal_leg_prices, api, self.signal(), "2026-09-16")
        self.assertEqual([("watchlist", "NSE:NIFTY2691624900PE")], api.named("watchlist"))
        self.assertEqual({}, api.named("contract")[0][5])


class StrategyInputTests(unittest.TestCase):
    def test_live_and_backtest_inputs_carry_no_recap_date(self):
        inp = StrategyInput(mode="LivePaper", timestamp_utc=TICK, underlying="NIFTY", spot_price=24900.0)
        self.assertIsNone(inp.recap_date)


if __name__ == "__main__":
    unittest.main()
