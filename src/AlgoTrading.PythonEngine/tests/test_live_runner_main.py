"""
A live run, end to end through execution_runner.py's own main block.

The market replay (1 Oct) put recap branches all through the runner. Each
helper is pinned on its own in test_market_replay_runner.py; this drives the
whole script the way the API launches it, with the API, Redis and the tick
stream faked, and pins what a live (LivePaper, not recap) run does when the
desk starts it in the morning:

  * no request carries the replay's query (replay, recapDate, untilUtc,
    includeHistory), and the bulk quote read carries no query at all;
  * the expiry is the first listed one on or after today's UTC date, which
    on an expiry day is that day's own;
  * the spot, the resolved contracts and the signal's legs go on the live
    watchlist;
  * the stream is read from "$": never pinned, no listening key written;
  * replayed ticks (isReplay) never reach the strategy; live ones, with the
    flag false or without it, do;
  * every input, warm-up included, is LivePaper with no recap_date, and
    warm-up reads up to today;
  * one OPEN_GROUP is booked with its leg resolved and priced from the live
    quotes.

A recap twin runs the same script with a recap run's parameters, so the fakes
are shown to tell the two apart: a check that passed on both would prove
nothing about the live one.
"""

import contextlib
import io
import json
import os
import runpy
import signal
import threading
import unittest
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest import mock
from urllib.parse import urlparse

import _bootstrap  # noqa: F401

import requests

import core.api_client  # noqa: F401  (patched below)
import core.data_engine  # noqa: F401
import core.metrics  # noqa: F401
import core.process_priority  # noqa: F401
import core.safe_output  # noqa: F401
import core.warmup_source  # noqa: F401
import strategies.registry  # noqa: F401
import strategies.variants  # noqa: F401
from core.warmup_source import WarmupBars
from strategies.base_strategy import BaseStrategy, DataRequirement, StrategySignal

RUNNER = Path(__file__).resolve().parents[1] / "strategies" / "execution_runner.py"
SPOT = "NSE:NIFTY50-INDEX"
RUN_ID = 501
STREAM = "market:ticks"
PINNED_ID = "1727790005000-3"
REPLAY_QUERY = {"replay", "recapDate", "untilUtc", "includeHistory"}
IST = timezone(timedelta(hours=5, minutes=30))


class EndOfStream(BaseException):
    """Ends the runner's endless read the way a stop does: through its finally block."""


def contract_symbol(expiry: str, strike, option_type: str) -> str:
    return f"NSE:NIFTY-{expiry}-{int(float(strike))}{option_type}"


# ------------------------------------------------------------------ the API --

class FakeResponse:
    def __init__(self, body, status=200):
        self._body = body
        self.status_code = status
        self.content = json.dumps(body).encode() if body is not None else b""
        self.text = self.content.decode()

    def json(self):
        return self._body

    def raise_for_status(self):
        if self.status_code >= 400:
            raise requests.exceptions.HTTPError(f"{self.status_code}", response=self)


class FakeApi:
    """
    The trading API as the runner reaches it: canned answers by path, every
    request recorded as (method, path, params as sent, json body). A path it
    does not know is recorded in `unexpected` and answered 404, not raised:
    the runner swallows most exceptions, and the test must still see it.
    """

    def __init__(self, run_params, expiries):
        self.run_params = run_params
        self.expiries = expiries
        self.requests = []
        self.unexpected = []
        self.contracts = set()
        self._lock = threading.Lock()

    # The session interface PlatformApiClient and UiSignalPublisher use.
    def get(self, url, params=None, **_kwargs):
        return self._answer("GET", url, params, None)

    def post(self, url, json=None, params=None, **_kwargs):
        return self._answer("POST", url, params, json)

    def _answer(self, method, url, params, body):
        # The path from its "/api/" on, whatever API_BASE_URL the machine's .env names.
        path = urlparse(url).path
        path = path[path.rfind("/api/"):]
        with self._lock:
            self.requests.append((method, path, None if params is None else dict(params), body))
        answer = self._route(method, path, dict(params or {}), body)
        if answer is None:
            self.unexpected.append((method, path))
            return FakeResponse({}, status=404)
        return FakeResponse(answer)

    def _route(self, method, path, params, body):
        now = datetime.now(timezone.utc).isoformat()
        if method == "GET" and path == f"/api/Simulator/runs/{RUN_ID}":
            return {"id": RUN_ID, "symbol": SPOT, "parametersJson": json.dumps(self.run_params)}
        if method == "GET" and path == "/api/Instruments/derivatives/expiries":
            return [{"expiryDate": d} for d in self.expiries]
        if method == "GET" and path == "/api/Instruments/derivatives/chain":
            return [{"strikePrice": k} for k in range(24700, 25150, 50)]
        if method == "GET" and path == "/api/Instruments/derivatives/underlyings":
            return [{"underlying": "NIFTY", "lotSize": 65, "strikeStep": 50}]
        if method == "GET" and path == "/api/Instruments/derivatives/contract":
            symbol = contract_symbol(params["expiry"], params["strike"], params["optionType"])
            self.contracts.add(symbol)
            return {"symbol": symbol, "underlying": "NIFTY", "expiryDate": params["expiry"],
                    "strikePrice": float(params["strike"]), "optionType": params["optionType"]}
        if method == "POST" and path == f"/api/Strategy/runs/{RUN_ID}/runner":
            return {}
        if method == "POST" and path == "/api/LiveData/watchlist":
            return {"symbol": body["symbol"]}
        if method == "GET" and path == "/api/LiveData/bars":
            start = datetime.now(timezone.utc).replace(second=0, microsecond=0)
            return [{"symbol": params["symbol"], "barStartUtc": (start - timedelta(minutes=5 * i)).isoformat(),
                     "open": 100.0, "high": 101.0, "low": 99.0, "close": 100.5, "volumeDelta": 10.0}
                    for i in range(3)]
        if method == "GET" and path == "/api/LiveData/latest/all":
            return [{"symbol": s, "lastTradedPrice": 101.5, "updatedUtc": now} for s in sorted(self.contracts)]
        if method == "GET" and path == "/api/MarketSession/check":
            return {"isMarketOpen": True}
        if method == "POST" and path == "/api/Simulator/signals":
            return {"id": 9001, "simulationRunId": RUN_ID, "signalType": body.get("signalType")}
        if method == "POST" and path in (f"/api/Strategy/runs/{RUN_ID}/signals", f"/api/Strategy/runs/{RUN_ID}/feed"):
            return {}
        return None

    def named(self, method, path):
        return [r for r in self.requests if r[0] == method and r[1] == path]


# ---------------------------------------------------------------- the Redis --

class FakeRedisServer:
    """One Redis for every client the runner opens: keys, and the tick stream read once."""

    def __init__(self, entries):
        self.kv = {}
        self.keys_set = []
        self.entries = entries
        self.xreads = []
        self.xinfo_calls = 0

    def client(self, *_args, **_kwargs):
        return FakeRedis(self)


class FakeRedis:
    def __init__(self, server):
        self.server = server

    def set(self, key, value, ex=None, px=None, nx=False):
        if nx and key in self.server.kv:
            return None
        self.server.kv[key] = value
        self.server.keys_set.append(key)
        return True

    def get(self, key):
        return self.server.kv.get(key)

    def delete(self, *keys):
        return sum(1 for key in keys if self.server.kv.pop(key, None) is not None)

    def pexpire(self, _key, _ms):
        return True

    def ping(self):
        return True

    def xinfo_stream(self, _name):
        self.server.xinfo_calls += 1
        return {"length": 9, "last-generated-id": PINNED_ID}

    def xread(self, streams, count=None, block=None):
        self.server.xreads.append(dict(streams))
        if self.server.entries is None:
            raise EndOfStream()
        entries, self.server.entries = self.server.entries, None
        return [[STREAM, entries]]


def stream_entry(index, tick):
    return (f"1727790006000-{index}", {"payload": json.dumps(tick), "symbol": tick["symbol"]})


# ------------------------------------------------------------- the strategy --

class ProbeStrategy(BaseStrategy):
    """Records every input it is handed and opens one short call on the first live one."""

    name = "LiveProbe"
    listed = False
    inputs = []

    @classmethod
    def get_data_requirements(cls):
        return [DataRequirement(symbol_type="index", resolution="5m"),
                DataRequirement(symbol_type="atm_ce", resolution="5m")]

    def __init__(self, params=None):
        self.params = params or {}

    def initialize_state(self):
        return {"opened": False}

    def on_bar(self, state, inp):
        ProbeStrategy.inputs.append(inp)
        if (inp.metadata or {}).get("source") == "warmup" or state.get("opened"):
            return []
        state["opened"] = True
        return [StrategySignal(
            strategy_name=self.name, signal_type="OPEN_GROUP", timestamp_utc=inp.timestamp_utc, reason="probe",
            # A Fulcrum-style logical leg: the runner resolves it against the master.
            legs=[{"symbol": f"NIFTY_CE_{int(inp.atm_strike)}", "side": "SELL", "quantity": 1}],
            metadata={"group_id": "probe-1"},
        )]


def warmup_bar(at: datetime):
    return SimpleNamespace(symbol=SPOT, resolution="5", timestamp_start=at, open=24800.0, high=24820.0,
                           low=24790.0, close=24810.0, volume=0.0)


# ------------------------------------------------------------------ harness --

class RunnerHarness:
    """execution_runner.py run as __main__ against the fakes; the outcome is kept on the instance."""

    def __init__(self, run_params, expiries, ticks, warmup_bars):
        self.api = FakeApi(run_params, expiries)
        self.redis = FakeRedisServer([stream_entry(i, t) for i, t in enumerate(ticks)])
        self.warmup_bars = warmup_bars
        self.warmup_calls = []
        self.output = ""
        self.inputs = []

    def load_warmup(self, **kwargs):
        self.warmup_calls.append(kwargs)
        return WarmupBars(list(self.warmup_bars), "local store", f"{len(self.warmup_bars)} bars")

    def run(self):
        ProbeStrategy.inputs = []
        argv = ["execution_runner.py", "--strategy", "LiveProbe", "--strategy-id", "7", "--user-id", "1",
                "--run-id", str(RUN_ID), "--underlying", "NIFTY", "--spot-symbol", SPOT]
        env = {"RUNNER_STREAM_FILTER": "1", "RUNNER_STREAM_BATCH_MS": "0", "REDIS_STREAM_NAME": STREAM}
        handlers = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
        out = io.StringIO()
        patches = [
            mock.patch("sys.argv", argv),
            mock.patch.dict(os.environ, env),
            # The test process keeps its own priority and its own stdout.
            mock.patch("core.process_priority.lower_priority_from_env", lambda *a, **k: None),
            mock.patch("core.safe_output.install_safe_stdio", lambda *a, **k: ""),
            mock.patch("core.safe_output.install_exit_line", lambda *a, **k: None),
            mock.patch("core.api_client.build_session", lambda *a, **k: self.api),
            mock.patch("strategies.registry.discover_strategies", lambda *a, **k: {"LiveProbe": ProbeStrategy}),
            mock.patch("strategies.variants.get_parameterised_strategies", lambda: {}),
            mock.patch("core.metrics.start_metrics_server_auto", lambda *a, **k: 0),
            mock.patch("core.warmup_source.load_warmup_bars", self.load_warmup),
            mock.patch("core.data_engine.DataEngine", mock.MagicMock()),
            mock.patch("redis.Redis", self.redis.client),
        ]
        try:
            with contextlib.ExitStack() as stack:
                for p in patches:
                    stack.enter_context(p)
                stack.enter_context(contextlib.redirect_stdout(out))
                stack.enter_context(contextlib.redirect_stderr(out))
                try:
                    runpy.run_path(str(RUNNER), run_name="__main__")
                except EndOfStream:
                    pass
        finally:
            for sig, handler in handlers.items():
                signal.signal(sig, handler)
            self.output = out.getvalue()
            self.inputs = list(ProbeStrategy.inputs)
        return self

    # --- views of what happened ---------------------------------------------

    def params_sent(self):
        return [(method, path, params) for method, path, params, _ in self.api.requests]

    def watchlist(self):
        return [(body["symbol"], body["priority"]) for _, _, _, body in self.api.named("POST", "/api/LiveData/watchlist")]

    def live_inputs(self):
        return [i for i in self.inputs if (i.metadata or {}).get("source") != "warmup"]

    def warmup_inputs(self):
        return [i for i in self.inputs if (i.metadata or {}).get("source") == "warmup"]


def utc_day(offset_days=0):
    return (datetime.now(timezone.utc).date() + timedelta(days=offset_days)).isoformat()


class LiveRunEndToEndTests(unittest.TestCase):
    """A live run on an expiry day, with a desk replay's ticks on the same stream."""

    @classmethod
    def setUpClass(cls):
        now = datetime.now(timezone.utc).replace(microsecond=0)
        cls.today = utc_day()
        cls.expiries = [utc_day(-7), cls.today, utc_day(7)]
        ticks = [
            # A desk replay of another day playing on the same stream.
            {"symbol": SPOT, "lastTradedPrice": 25010.0, "exchangeTimestampUtc": now.isoformat(),
             "isReplay": True, "sourceKey": "desk-replay"},
            # Another symbol: passed over on its stream field.
            {"symbol": "NSE:RELIANCE-EQ", "lastTradedPrice": 1400.0, "exchangeTimestampUtc": now.isoformat(),
             "isReplay": False},
            # The live feed, as the feed runner publishes it (isReplay false) ...
            {"symbol": SPOT, "lastTradedPrice": 24910.0, "exchangeTimestampUtc": now.isoformat(),
             "isReplay": False, "sourceKey": "dhan"},
            # ... and as a publisher that never sets the flag does.
            {"symbol": SPOT, "lastTradedPrice": 24920.0, "exchangeTimestampUtc": now.isoformat()},
        ]
        warmup = [warmup_bar(now - timedelta(days=1, minutes=5 * i)) for i in range(3)]
        cls.live = RunnerHarness({"lots": 1, "underlying": "NIFTY"}, cls.expiries, ticks, warmup).run()

    def test_the_runner_used_only_the_endpoints_a_live_run_uses(self):
        self.assertEqual([], self.live.api.unexpected, self.live.output)

    def test_no_request_carries_the_replays_query(self):
        for method, path, params in self.live.params_sent():
            self.assertFalse(REPLAY_QUERY & set(params or {}), f"{method} {path} {params}")

    def test_the_bulk_quote_read_carries_no_query_at_all(self):
        reads = self.live.api.named("GET", "/api/LiveData/latest/all")
        self.assertTrue(reads, "the signal's legs were priced from the bulk read")
        self.assertEqual([None] * len(reads), [r[2] for r in reads])

    def test_the_expiry_is_the_first_on_or_after_todays_utc_date_and_on_expiry_day_that_is_today(self):
        expiry_reads = self.live.api.named("GET", "/api/Instruments/derivatives/expiries")
        self.assertEqual([{"underlying": "NIFTY"}], [r[2] for r in expiry_reads])
        self.assertIn(f"Using expiry: {self.today}\n", self.live.output)
        lookups = self.live.api.named("GET", "/api/Instruments/derivatives/contract")
        self.assertTrue(lookups)
        self.assertEqual({self.today}, {r[2]["expiry"] for r in lookups})

    def test_the_strike_step_comes_from_the_chain_of_that_expiry(self):
        self.assertEqual([{"underlying": "NIFTY", "expiry": self.today}],
                         [r[2] for r in self.live.api.named("GET", "/api/Instruments/derivatives/chain")])
        self.assertEqual([], self.live.api.named("GET", "/api/Instruments/derivatives/underlyings")[1:],
                         "the inventory is read once, for the lot size")

    def test_the_spot_the_contracts_and_the_legs_go_on_the_live_watchlist(self):
        watched = self.live.watchlist()
        self.assertEqual((SPOT, 100), watched[0])
        ce, pe = contract_symbol(self.today, 24900, "CE"), contract_symbol(self.today, 24900, "PE")
        self.assertIn((ce, 80), watched)
        self.assertIn((pe, 80), watched)
        self.assertIn((ce, 50), watched, "the signal's leg is subscribed before its price is waited for")

    def test_the_stream_is_read_from_now_and_never_pinned(self):
        self.assertEqual(0, self.live.redis.xinfo_calls)
        self.assertEqual({STREAM: "$"}, self.live.redis.xreads[0])
        self.assertFalse([k for k in self.live.redis.keys_set if k.startswith("recap:")])

    def test_replayed_ticks_never_reach_a_live_strategy(self):
        self.assertEqual([24910.0, 24920.0], [i.spot_price for i in self.live.live_inputs()])
        self.assertEqual(1, self.live.output.count("Passing over replayed ticks"))

    def test_every_input_is_live_paper_with_no_recap_date(self):
        self.assertEqual(3, len(self.live.warmup_inputs()))
        for inp in self.live.inputs:
            self.assertEqual("LivePaper", inp.mode)
            self.assertIsNone(inp.recap_date)
        for inp in self.live.live_inputs():
            self.assertEqual("live-api", inp.metadata["source"])
            self.assertEqual(self.today, inp.metadata["expiry_date"])
            self.assertEqual(24900, inp.atm_strike)
            self.assertEqual(50.0, inp.strike_step)
            self.assertEqual(65, inp.lot_size)
            self.assertEqual(contract_symbol(self.today, 24900, "CE"), inp.contracts["atm_ce"].symbol)
            self.assertEqual({"index", "atm_ce"}, set(inp.bars["5m"]))

    def test_bars_are_the_newest_without_a_bound(self):
        reads = self.live.api.named("GET", "/api/LiveData/bars")
        self.assertTrue(reads)
        for _, _, params, _ in reads:
            self.assertEqual({"symbol", "resolution", "take"}, set(params))
            self.assertEqual(500, params["take"])

    def test_warm_up_reads_fifteen_days_up_to_today(self):
        (call,) = self.live.warmup_calls
        today = datetime.now()
        self.assertEqual(today.strftime("%Y-%m-%d"), call["end_date"])
        self.assertEqual((today - timedelta(days=15)).strftime("%Y-%m-%d"), call["start_date"])
        self.assertEqual(SPOT, call["symbol"])

    def test_one_open_group_is_booked_with_its_leg_resolved_and_priced(self):
        (post,) = self.live.api.named("POST", "/api/Simulator/signals")
        body = post[3]
        self.assertEqual("OPEN_GROUP", body["signalType"])
        self.assertEqual(RUN_ID, body["simulationRunId"])
        self.assertEqual([{"symbol": contract_symbol(self.today, 24900, "CE"), "side": "SELL", "quantity": 1,
                           "price": 101.5}], body["legs"])
        self.assertTrue(body.get("clientSignalId"))

    def test_the_runner_clears_its_state_when_it_ends(self):
        self.assertNotIn(f"strategy:lock:{RUN_ID}", self.live.redis.kv)
        self.assertNotIn(f"strategy:state:{RUN_ID}", self.live.redis.kv)


class RecapTwinTests(unittest.TestCase):
    """The same script and fakes with a recap run's parameters: everything above comes out the other way."""

    DAY = date(2026, 9, 15)

    @classmethod
    def setUpClass(cls):
        in_session = datetime.combine(cls.DAY, time(10, 1, 7), IST).astimezone(timezone.utc)
        now = datetime.now(timezone.utc).replace(microsecond=0)
        ticks = [
            {"symbol": SPOT, "lastTradedPrice": 25500.0, "exchangeTimestampUtc": now.isoformat(), "isReplay": False},
            {"symbol": SPOT, "lastTradedPrice": 24910.0, "exchangeTimestampUtc": in_session.isoformat(),
             "isReplay": True, "sourceKey": "desk-replay"},
        ]
        before = datetime.combine(cls.DAY - timedelta(days=1), time(15, 25), IST).astimezone(timezone.utc)
        warmup = [warmup_bar(before), warmup_bar(in_session)]       # the second is the replayed day's own
        params = {"lots": 1, "underlying": "NIFTY", "session": "recap", "recap_date": cls.DAY.isoformat()}
        expiries = ["2026-09-09", "2026-09-16", "2026-09-23", utc_day(7)]
        cls.recap = RunnerHarness(params, expiries, ticks, warmup).run()

    def test_the_fakes_tell_a_recap_run_from_a_live_one(self):
        out = self.recap
        self.assertEqual([], out.api.unexpected, out.output)
        quotes = out.api.named("GET", "/api/LiveData/latest/all")
        self.assertTrue(quotes)
        self.assertEqual({"replay": "true", "recapDate": "2026-09-15"}, quotes[0][2])
        self.assertTrue(all("untilUtc" in r[2] for r in out.api.named("GET", "/api/LiveData/bars")))
        self.assertEqual([{"underlying": "NIFTY", "includeHistory": "true"}],
                         [r[2] for r in out.api.named("GET", "/api/Instruments/derivatives/expiries")])
        self.assertEqual([], out.watchlist())
        self.assertEqual(1, out.redis.xinfo_calls)
        self.assertEqual({STREAM: PINNED_ID}, out.redis.xreads[0])
        self.assertIn(f"recap:listening:{RUN_ID}", out.redis.keys_set)
        self.assertNotIn(f"recap:listening:{RUN_ID}", out.redis.kv, "taken back when the runner ends")
        self.assertEqual([24910.0], [i.spot_price for i in out.live_inputs()])
        self.assertEqual({"2026-09-15"}, {i.recap_date for i in out.inputs})
        self.assertEqual(1, len(out.warmup_inputs()), "warm-up keeps only the days before the replayed one")
        self.assertEqual("2026-09-14", out.warmup_calls[0]["end_date"])
        (post,) = out.api.named("POST", "/api/Simulator/signals")
        self.assertEqual(contract_symbol("2026-09-16", 24900, "CE"), post[3]["legs"][0]["symbol"])


if __name__ == "__main__":
    unittest.main()
