"""
scripts/telegram_notifier.py — the alert sidecar.

Two things are worth pinning down here, because both are invisible until they
go wrong in front of a live market:

  * the transitions it must detect (run start/stop, position open/close, market
    data start/stop) and, just as important, the ticks on which it must stay
    silent;
  * that it only reads a run's legs when the cheap run list says something in
    that run's book actually moved. That read reaches
    PaperTradingService.GetPaperPositionsAsync, which takes the run's
    SimulationRunLocks gate — the same mutex the order/fill path uses — and
    writes marks back. Polling it every tick would put this process in
    contention with real fills, so "an unchanged tick touches no gate" is a
    safety property, not an optimisation.

Nothing here touches Redis, the API, or the ingestor.
"""

import os
import sys
import unittest

import _bootstrap  # noqa: F401

# The notifier lives in scripts/, which is not on the engine path.
SCRIPTS_DIR = os.path.abspath(
    os.path.join(os.path.dirname(__file__), "..", "..", "..", "scripts")
)
if SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, SCRIPTS_DIR)

import telegram_notifier as tn  # noqa: E402


def make_run(run_id, active, open_positions, trades, **extra):
    run = {
        "runId": run_id,
        "strategyName": "TestStrat",
        "underlying": "BANKNIFTY",
        "spotSymbol": "NSE:NIFTYBANK-INDEX",
        "lots": 1,
        "lotSize": 30,
        "userName": "admin",
        "startedUtc": "2026-09-07T07:00:00Z",
        "isActive": active,
        "openPositions": open_positions,
        "trades": trades,
        "netPnl": 100.0,
        "realizedPnl": 100.0,
        "unrealizedPnl": 0.0,
        "durationSeconds": 600,
        "capitalUsed": 1000.0,
        "risk": {},
    }
    run.update(extra)
    return run


def make_leg(leg_id, status="Open", pnl=0.0):
    return {
        "id": leg_id,
        "symbol": f"NSE:BANKNIFTY26SEP5710{leg_id}CE",
        "contract": {"label": f"BANKNIFTY 5710{leg_id} CE · 29 Sep"},
        "side": "BUY",
        "lots": 1,
        "lotSize": 30,
        "quantity": 30,
        "status": status,
        "entryPrice": 100.0,
        "ltp": 110.0,
        "pnl": pnl,
        "pnlPoints": 10.0,
        "pnlPercent": 10.0,
        "entryValue": 3000.0,
        "openedUtc": "2026-09-07T07:05:00Z",
        "closedUtc": None if status == "Open" else "2026-09-07T07:30:00Z",
    }


class RecordingPublisher:
    def __init__(self):
        self.events = []

    def publish(self, **kwargs):
        self.events.append(kwargs)


class FakeApi:
    """Serves whatever the test currently says the world looks like."""

    def __init__(self):
        self.runs = []
        self.live = {}
        self.ingestor = True

    def get(self, path):
        if path == "/api/Strategy/runs":
            return self.runs
        if path == "/api/Ingestor/status":
            return {"isRunning": self.ingestor}
        if "/live" in path:
            run_id = int(path.split("/runs/")[1].split("/")[0])
            return self.live.get(run_id)
        raise AssertionError(f"unexpected path {path}")


class WatcherTransitionTests(unittest.TestCase):
    def setUp(self):
        self.api = FakeApi()
        self.publisher = RecordingPublisher()
        # A reconcile far in the future, so these tests only exercise the
        # counter-driven path.
        self.watcher = tn.Watcher(self.api, self.publisher, reconcile_seconds=10_000)

        self.api.runs = [make_run(1, True, 1, 0)]
        self.api.live = {1: {"positions": [make_leg(1)]}}
        self.watcher.baseline()

    def titles(self):
        found = [e["title"] for e in self.publisher.events]
        self.publisher.events.clear()
        return found

    def test_baseline_alerts_on_nothing(self):
        # A run already live when the notifier starts must not raise a start
        # alert; otherwise restarting it mid-session spams the group.
        self.assertEqual(self.titles(), [])

    def test_unchanged_tick_is_silent_and_touches_no_gate(self):
        before = self.watcher.detail_calls
        self.watcher.tick()
        self.assertEqual(self.titles(), [])
        self.assertEqual(self.watcher.detail_calls, before)

    def test_position_opened(self):
        before = self.watcher.detail_calls
        self.api.runs = [make_run(1, True, 2, 0)]
        self.api.live = {1: {"positions": [make_leg(1), make_leg(2)]}}
        self.watcher.tick()

        titles = self.titles()
        self.assertEqual(len(titles), 1)
        self.assertTrue(titles[0].startswith("Position opened"), titles[0])
        # A counter move costs exactly one gated read, not one per tick.
        self.assertEqual(self.watcher.detail_calls, before + 1)

    def test_position_closed_carries_its_pnl(self):
        self.api.runs = [make_run(1, True, 0, 1)]
        self.api.live = {1: {"positions": [make_leg(1, status="Closed", pnl=250.0)]}}
        self.watcher.tick()

        titles = self.titles()
        self.assertEqual(len(titles), 1)
        self.assertTrue(titles[0].startswith("Position closed"), titles[0])
        self.assertIn("250", titles[0])

    def test_a_leg_that_vanishes_is_still_reported(self):
        self.api.runs = [make_run(1, True, 0, 1)]
        self.api.live = {1: {"positions": []}}
        self.watcher.tick()
        self.assertTrue(any(t.startswith("Position closed") for t in self.titles()))

    def test_run_started_and_stopped(self):
        self.api.runs = [make_run(1, True, 1, 0), make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        self.assertTrue(any(t.startswith("Strategy started") for t in self.titles()))

        self.api.runs = [
            make_run(1, True, 1, 0),
            make_run(
                2, False, 0, 3,
                stoppedUtc="2026-09-07T08:00:00Z",
                stopReason="Target hit",
                stoppedBy="admin",
            ),
        ]
        self.watcher.tick()
        stopped = [t for t in self.titles() if t.startswith("Strategy stopped")]
        self.assertEqual(len(stopped), 1, stopped)

    def test_market_data_stop_and_start(self):
        self.api.ingestor = False
        self.watcher.tick()
        self.assertIn("Market data stopped", self.titles())

        self.api.ingestor = True
        self.watcher.tick()
        self.assertIn("Market data started", self.titles())

    def test_first_ingestor_reading_is_a_baseline_not_a_transition(self):
        watcher = tn.Watcher(FakeApi(), RecordingPublisher())
        watcher._ingestor_running = None
        watcher._api.ingestor = False
        watcher._diff_ingestor()
        self.assertEqual(watcher._publisher.events, [])


class ForwarderSuppressionTests(unittest.TestCase):
    """
    The API notifies on stop and on deploy with one terse line. The poller here
    reports the same moments in full, so the terse one is dropped — but only
    that one. Feed and risk alerts have no counterpart and must survive.
    """

    def test_backend_start_and_stop_are_superseded(self):
        for title in (
            "GhostTangentCrossings stopped on SENSEX",
            "FulcrumBuy started on NIFTY",
        ):
            self.assertTrue(
                tn.is_superseded({"Title": title, "Source": "strategyrun"}), title
            )

    def test_feed_and_risk_alerts_are_kept(self):
        keep = [
            ({"Title": "Feed stalled — FulcrumBuy on BANKNIFTY", "Source": "strategyrun"}),
            ({"Title": "Feed recovered — FulcrumBuy on BANKNIFTY", "Source": "strategyrun"}),
            ({"Title": "Kill switch engaged", "Source": "risk"}),
            ({"Title": "Trading resumed", "Source": "risk"}),
            ({"Title": "Market data stopped", "Source": "process"}),
        ]
        for payload in keep:
            self.assertFalse(tn.is_superseded(payload), payload["Title"])

    def test_our_own_run_alerts_are_never_suppressed(self):
        for title in (
            "Strategy started · FulcrumBuy · NIFTY",
            "Strategy stopped · FulcrumBuy · NIFTY · -₹344.50",
        ):
            self.assertFalse(
                tn.is_superseded({"Title": title, "Source": "strategyrun"}), title
            )


class PayloadShapeTests(unittest.TestCase):
    def test_published_keys_are_pascal_case(self):
        """
        AlertSubscriberService calls JsonSerializer.Deserialize<AlertEventPayload>
        with no options, so binding is case-sensitive. Lowercase keys bind to
        nothing and the row lands as "Alert"/"system"/"UNKNOWN" with the content
        lost — which is exactly what logic_engine.py does today.
        """
        sent = {}

        class FakeRedis:
            def publish(self, channel, message):
                sent["channel"] = channel
                sent["message"] = message

        import json

        tn.AlertPublisher(FakeRedis()).publish(
            title="t", message="m", source="strategyrun", severity="info"
        )
        payload = json.loads(sent["message"])
        self.assertEqual(sent["channel"], "alerts:new")
        for key in ("Title", "Message", "Source", "Severity", "Underlying", "Symbol"):
            self.assertIn(key, payload)

    def test_html_is_escaped(self):
        self.assertEqual(tn.esc("a & b <c>"), "a &amp; b &lt;c&gt;")

    def test_money_is_signed(self):
        self.assertEqual(tn.money(1234.5), "+₹1,234.50")
        self.assertEqual(tn.money(-20), "-₹20.00")
        self.assertEqual(tn.money(0), "₹0.00")

    def test_utc_is_rendered_as_ist(self):
        # 07:33:55Z is 13:03:55 IST.
        self.assertIn("13:03:55 IST", tn.ist("2026-09-07T07:33:55.410588Z"))


if __name__ == "__main__":
    unittest.main()
