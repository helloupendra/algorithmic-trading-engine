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


def rendered(event):
    """What the forwarder sends to Telegram for a published event."""
    return tn.render_for_telegram({
        "Title": event["title"],
        "Message": event["message"],
        "Source": event["source"],
        "Severity": event["severity"],
    })


class FakeApi:
    """Serves whatever the test currently says the world looks like."""

    def __init__(self):
        self.runs = []
        self.live = {}
        # connector key -> running; None serves an API too old for /api/Feeds.
        self.feeds = {"fyers": False, "dhan": True}
        self.ingestor = True

    def get(self, path):
        if path == "/api/Strategy/runs":
            return self.runs
        if path == "/api/Feeds":
            if self.feeds is None:
                return "<!doctype html>"  # the SPA fallback an old API answers with
            names = {"fyers": "FYERS", "dhan": "Dhan", "truedata": "TrueData"}
            return [{"key": k, "displayName": names.get(k, k), "isRunning": v} for k, v in self.feeds.items()]
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
        """Titles seen so far. Does not clear: several tests assert on the
        full event objects afterwards."""
        return [e["title"] for e in self.publisher.events]

    def clear(self):
        self.publisher.events.clear()

    def test_baseline_alerts_on_nothing(self):
        # A run already live when the notifier starts must not raise a start
        # alert; otherwise restarting it mid-session spams the group.
        self.assertEqual(self.titles(), [])

    def test_unchanged_tick_is_silent_and_touches_no_gate(self):
        before = self.watcher.detail_calls
        self.watcher.tick()
        self.assertEqual(self.titles(), [])
        self.assertEqual(self.watcher.detail_calls, before)

    def test_opening_two_legs_sends_one_message(self):
        """
        The point of the consolidated alert: a strategy entering a straddle is
        one decision and must read as one message, not one per leg.
        """
        before = self.watcher.detail_calls
        self.api.runs = [make_run(1, True, 3, 0)]
        self.api.live = {1: {"positions": [make_leg(1), make_leg(2), make_leg(3)]}}
        self.watcher.tick()

        events = self.publisher.events
        self.assertEqual(len(events), 1, [e["title"] for e in events])
        self.assertIn("2 legs opened", events[0]["title"])
        # Both new legs are named in the body.
        self.assertIn("57102", events[0]["message"])
        self.assertIn("57103", events[0]["message"])
        # A counter move costs exactly one gated read, not one per tick.
        self.assertEqual(self.watcher.detail_calls, before + 1)

    def test_position_closed_carries_its_pnl(self):
        self.api.runs = [make_run(1, True, 0, 1)]
        self.api.live = {1: {"positions": [make_leg(1, status="Closed", pnl=250.0)]}}
        self.watcher.tick()

        events = self.publisher.events
        self.assertEqual(len(events), 1)
        self.assertIn("1 leg closed", events[0]["title"])
        self.assertIn("250", events[0]["message"])

    def test_a_leg_carried_forward_is_not_reported_as_a_close(self):
        """
        27 Sep: a leg ticked "carry forward" moves to the owner's manual book
        when the close stops its run. It was not sold; "closed · ₹0" would say
        it was.
        """
        self.api.runs = [make_run(1, True, 0, 1)]
        self.api.live = {
            1: {
                "positions": [
                    make_leg(1, status="Carried", pnl=0.0),
                    make_leg(2, status="Closed", pnl=250.0),
                ]
            }
        }
        self.watcher.tick()

        events = self.publisher.events
        self.assertEqual(len(events), 1, [e["title"] for e in events])
        self.assertIn("1 leg closed, 1 leg carried forward", events[0]["title"])
        self.assertIn("Carried forward</b> to the manual book", events[0]["message"])
        self.assertIn("held overnight", events[0]["message"])
        # Only the leg that was actually sold counts toward the realized line.
        self.assertIn("250", events[0]["message"])

    def test_a_close_that_only_carries_says_so(self):
        self.api.runs = [make_run(1, True, 0, 0)]
        self.api.live = {1: {"positions": [make_leg(1, status="Carried", pnl=0.0)]}}
        self.watcher.tick()

        events = self.publisher.events
        self.assertEqual(len(events), 1)
        self.assertIn("1 leg carried forward to the manual book", events[0]["title"])
        self.assertNotIn("closed", events[0]["title"])
        self.assertEqual(events[0]["severity"], "info")

    def test_a_roll_is_one_message_not_two(self):
        """
        Closing two legs and opening two others in the same tick is an
        adjustment. Reported separately it looks like four unrelated events.
        """
        self.api.runs = [make_run(1, True, 2, 1)]
        self.api.live = {
            1: {
                "positions": [
                    make_leg(1, status="Closed", pnl=120.0),
                    make_leg(7),
                    make_leg(8),
                ]
            }
        }
        self.watcher.tick()

        events = self.publisher.events
        self.assertEqual(len(events), 1, [e["title"] for e in events])
        self.assertIn("rolled", events[0]["title"])
        self.assertIn("Closed", events[0]["message"])
        self.assertIn("Opened", events[0]["message"])

    def test_a_leg_that_vanishes_is_still_reported(self):
        self.api.runs = [make_run(1, True, 0, 1)]
        self.api.live = {1: {"positions": []}}
        self.watcher.tick()
        events = self.publisher.events
        self.assertEqual(len(events), 1)
        self.assertIn("closed", events[0]["title"])

    def test_run_started_and_stopped(self):
        self.api.runs = [make_run(1, True, 1, 0), make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        # The account comes first, so two accounts' identical runs are told apart.
        self.assertTrue(any(t.startswith("[admin] Strategy started") for t in self.titles()))

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
        stopped = [t for t in self.titles() if t.startswith("[admin] Strategy stopped")]
        self.assertEqual(len(stopped), 1, stopped)

    def test_a_runner_that_died_is_a_warning_not_a_flat_result(self):
        # 24 Sep: sixteen runners died seconds after starting, and read as
        # "Strategy stopped · ₹0.00" at severity info.
        self.api.runs = [make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        self.api.runs = [make_run(2, False, 0, 0, stoppedUtc="2026-09-24T03:47:04Z", durationSeconds=3,
                                  stopReason="Runner exited (code 1)", stoppedBy="runner")]
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if "Strategy stopped" in e["title"]]
        self.assertEqual(1, len(stopped))
        self.assertEqual("warning", stopped[0]["severity"])
        self.assertIn("missing, not finished", stopped[0]["message"])

    def test_an_ordinary_flat_stop_stays_info(self):
        self.api.runs = [make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        self.api.runs = [make_run(2, False, 0, 0, stoppedUtc="2026-09-24T10:00:00Z", durationSeconds=21_600,
                                  stopReason="Market closed (15:30 IST)", stoppedBy="market-hours",
                                  netPnl=0.0, realizedPnl=0.0)]
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if "Strategy stopped" in e["title"]]
        self.assertEqual("info", stopped[0]["severity"])

    def test_a_person_stopping_a_run_after_20_seconds_is_not_a_missing_run(self):
        # Short is not dead: the warning blamed "ended by itself" on any run
        # under a minute, even one its owner stopped on purpose.
        self.api.runs = [make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        self.api.runs = [make_run(2, False, 0, 0, stoppedUtc="2026-09-28T03:50:20Z", durationSeconds=20,
                                  stopReason="Stopped by admin", stoppedBy="admin")]
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if "Strategy stopped" in e["title"]]
        self.assertEqual(1, len(stopped))
        self.assertEqual("success", stopped[0]["severity"])  # netPnl +100
        self.assertNotIn("missing", stopped[0]["message"])

    def test_a_risk_rule_that_closes_a_run_at_once_is_not_a_missing_run(self):
        self.api.runs = [make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        self.api.runs = [make_run(2, False, 0, 1, stoppedUtc="2026-09-28T03:50:30Z", durationSeconds=30,
                                  stopReason="Stop loss hit: P&L −₹5,120 ≤ −₹5,000", stoppedBy="risk-guard",
                                  netPnl=-5120.0)]
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if "Strategy stopped" in e["title"]]
        self.assertEqual("warning", stopped[0]["severity"])  # a loss, said as one
        self.assertNotIn("missing", stopped[0]["message"])

    def test_a_short_run_nobody_stopped_is_still_missing(self):
        # No RUN_STOPPED signal: nothing in stoppedBy, and a reason that names
        # no person and no rule. That is the 24 Sep shape without the runner's
        # exit record.
        self.api.runs = [make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        self.api.runs = [make_run(2, False, 0, 0, stoppedUtc="2026-09-28T03:47:04Z", durationSeconds=4,
                                  stopReason="Runner failed to start.")]
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if "Strategy stopped" in e["title"]]
        self.assertEqual("warning", stopped[0]["severity"])
        self.assertIn("missing, not finished", stopped[0]["message"])

    def test_a_person_named_only_in_the_reason_still_explains_a_short_run(self):
        self.api.runs = [make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        self.api.runs = [make_run(2, False, 0, 0, stoppedUtc="2026-09-28T03:50:20Z", durationSeconds=20,
                                  stopReason="Stopped by coderforchange")]
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if "Strategy stopped" in e["title"]]
        self.assertNotIn("missing", stopped[0]["message"])

    # -- runs that start and end between two polls --------------------------- #
    def test_a_run_that_started_and_died_between_two_polls_is_still_reported(self):
        # The API keeps its own "X started on Y" off Telegram while the
        # notifier runs, so a run the poller never saw live must not vanish.
        died = make_run(5, False, 0, 0, stoppedUtc="2026-09-24T03:47:04Z", durationSeconds=3,
                        stopReason="Runner exited (code 1)", stoppedBy="runner")
        self.api.runs = [make_run(1, True, 1, 0), died]
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if "Strategy stopped" in e["title"]]
        self.assertEqual(1, len(stopped), self.titles())
        self.assertEqual("warning", stopped[0]["severity"])
        self.assertIn("missing, not finished", stopped[0]["message"])

        self.watcher.tick()
        self.assertEqual(1, len([t for t in self.titles() if "Strategy stopped" in t]))

    def test_a_row_still_being_set_up_waits_for_its_stop(self):
        # Start inserts the row before the runner is registered: inactive, no
        # stop time. That is not a stop — until the runner dies unseen.
        self.api.runs = [make_run(1, True, 1, 0), make_run(5, False, 0, 0)]
        self.watcher.tick()
        self.assertEqual([], self.titles())

        self.api.runs = [make_run(1, True, 1, 0),
                         make_run(5, False, 0, 0, stoppedUtc="2026-09-24T03:47:04Z", durationSeconds=2,
                                  stopReason="Runner exited (code 1)", stoppedBy="runner")]
        self.watcher.tick()
        self.assertEqual(1, len([t for t in self.titles() if "Strategy stopped" in t]))

    def test_a_stop_seen_live_is_reported_once_even_when_its_time_arrives_late(self):
        self.api.runs = [make_run(1, True, 1, 0), make_run(2, True, 0, 0)]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()
        self.api.runs = [make_run(1, True, 1, 0), make_run(2, False, 0, 0)]  # no stoppedUtc yet
        self.watcher.tick()
        self.api.runs = [make_run(1, True, 1, 0), make_run(2, False, 0, 0, stoppedUtc="2026-09-28T04:00:00Z")]
        self.watcher.tick()
        self.assertEqual(1, len([t for t in self.titles() if "Strategy stopped" in t]))

    def test_runs_already_over_and_alerts_only_runs_are_never_announced(self):
        api = FakeApi()
        api.runs = [make_run(3, False, 0, 2, stoppedUtc="2026-09-25T10:00:00Z")]
        watcher = tn.Watcher(api, RecordingPublisher(), reconcile_seconds=10_000)
        watcher.baseline()
        api.runs = [make_run(3, False, 0, 2, stoppedUtc="2026-09-25T10:00:00Z"),
                    make_run(4, False, 0, 0, stoppedUtc="2026-09-28T04:00:00Z", role="alerts")]
        watcher.tick()
        self.assertEqual([], watcher._publisher.events)

    def test_a_failed_first_read_does_not_announce_every_old_run(self):
        api = FakeApi()
        api.runs = None  # the API did not answer the baseline
        watcher = tn.Watcher(api, RecordingPublisher(), reconcile_seconds=10_000)
        watcher.baseline()
        api.runs = [make_run(n, False, 0, 1, stoppedUtc="2026-09-25T10:00:00Z") for n in (3, 4, 5)]
        watcher.tick()
        self.assertEqual([], watcher._publisher.events)

    # -- what reaches Telegram ------------------------------------------------ #
    def test_every_run_message_names_the_account_on_its_first_line(self):
        """
        The forwarder sends the body and drops the title, so the account has
        to be in the body: two accounts running one plan otherwise read as one
        run reported twice.
        """
        self.api.runs = [make_run(1, True, 1, 0), make_run(2, True, 0, 0, userName="coderforchange")]
        self.api.live[2] = {"positions": []}
        self.watcher.tick()                                        # started
        self.api.runs = [make_run(1, True, 2, 1), make_run(2, True, 0, 0, userName="coderforchange")]
        self.api.live[1] = {"positions": [make_leg(1, status="Closed", pnl=50.0), make_leg(7)]}
        self.watcher.tick()                                        # rolled
        self.api.runs = [make_run(1, True, 3, 1), make_run(2, True, 0, 0, userName="coderforchange")]
        self.api.live[1] = {"positions": [make_leg(1, status="Closed", pnl=50.0), make_leg(7), make_leg(8)]}
        self.watcher.tick()                                        # opened
        self.api.runs = [make_run(1, True, 2, 2), make_run(2, True, 0, 0, userName="coderforchange")]
        self.api.live[1] = {"positions": [make_leg(1, status="Closed", pnl=50.0), make_leg(7, status="Closed", pnl=-20.0), make_leg(8)]}
        self.watcher.tick()                                        # closed
        self.api.runs = [make_run(1, True, 2, 2),
                         make_run(2, False, 0, 0, userName="coderforchange", stoppedUtc="2026-09-28T04:00:00Z",
                                  stopReason="Stopped by admin", stoppedBy="admin")]
        self.watcher.tick()                                        # stopped

        kinds = [e["title"] for e in self.publisher.events]
        self.assertTrue(any("Strategy started" in k for k in kinds), kinds)
        self.assertTrue(any("rolled" in k for k in kinds), kinds)
        self.assertTrue(any("opened" in k for k in kinds), kinds)
        self.assertTrue(any("closed" in k for k in kinds), kinds)
        self.assertTrue(any("Strategy stopped" in k for k in kinds), kinds)
        for event in self.publisher.events:
            text = rendered(event)
            account = "[coderforchange]" if "coderforchange" in event["title"] else "[admin]"
            self.assertIn(account, text.splitlines()[0], text)
            # Passed through as the HTML it is, never escaped as plain text.
            self.assertEqual(event["message"], text)
            self.assertNotIn("&lt;b&gt;", text)

    def test_two_accounts_stopping_the_same_plan_are_told_apart(self):
        self.api.runs = [make_run(1, True, 1, 0), make_run(2, True, 0, 0), make_run(3, True, 0, 0, userName="coderforchange")]
        self.api.live.update({2: {"positions": []}, 3: {"positions": []}})
        self.watcher.tick()
        self.clear()
        self.api.runs = [make_run(1, True, 1, 0),
                         make_run(2, False, 0, 0, stoppedUtc="2026-09-28T10:00:00Z", stoppedBy="market-hours",
                                  stopReason="Market closed (15:30 IST)"),
                         make_run(3, False, 0, 0, userName="coderforchange", stoppedUtc="2026-09-28T10:00:00Z",
                                  stoppedBy="market-hours", stopReason="Market closed (15:30 IST)")]
        self.watcher.tick()
        first_lines = sorted(rendered(e).splitlines()[0] for e in self.publisher.events)
        self.assertEqual(2, len(first_lines))
        self.assertNotEqual(first_lines[0], first_lines[1])
        self.assertIn("[admin]", first_lines[0])
        self.assertIn("[coderforchange]", first_lines[1])

    def test_every_connectors_feed_is_announced_by_name(self):
        # 2026-09-15: stopping and starting Dhan's feed sent nothing, because
        # only the FYERS ingestor was watched.
        self.api.feeds["dhan"] = False
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if e["title"] == "Dhan feed stopped"]
        self.assertEqual(len(stopped), 1)
        self.assertIn("No feed is running now", stopped[0]["message"])

        self.api.feeds["dhan"] = True
        self.watcher.tick()
        started = [e for e in self.publisher.events if e["title"] == "Dhan feed started"]
        self.assertEqual(len(started), 1)
        self.assertEqual("success", started[0]["severity"])

    def test_a_second_feed_starting_warns_and_a_stop_names_what_still_feeds(self):
        self.api.feeds["fyers"] = True
        self.watcher.tick()
        started = [e for e in self.publisher.events if e["title"] == "FYERS feed started"][0]
        self.assertEqual("warning", started["severity"])
        self.assertIn("2 feeds are running", started["message"])

        self.api.feeds["fyers"] = False
        self.watcher.tick()
        stopped = [e for e in self.publisher.events if e["title"] == "FYERS feed stopped"][0]
        self.assertIn("Still feeding: Dhan", stopped["message"])

    def test_unchanged_feeds_and_a_new_connector_are_silent(self):
        self.api.feeds["truedata"] = False
        self.watcher.tick()
        self.assertEqual([t for t in self.titles() if "feed" in t], [])

    def test_first_feed_reading_is_a_baseline_not_a_transition(self):
        watcher = tn.Watcher(FakeApi(), RecordingPublisher())
        watcher._feeds = None
        watcher._api.feeds["dhan"] = False
        watcher._diff_feeds()
        self.assertEqual(watcher._publisher.events, [])

    def test_an_api_without_feeds_is_read_through_the_ingestor(self):
        api = FakeApi()
        api.feeds = None
        watcher = tn.Watcher(api, RecordingPublisher())
        watcher.baseline()
        api.ingestor = False
        watcher._diff_feeds()
        self.assertEqual(["FYERS feed stopped"], [e["title"] for e in watcher._publisher.events])


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
            ({"Title": "Dhan feed stopped", "Source": "process"}),
            ({"Title": "FYERS feed started", "Source": "process"}),
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

    def test_record_only_events_are_not_forwarded(self):
        """The API keeps them off Telegram; a hand-started forwarder must too."""
        self.assertTrue(tn.is_record_only(
            {"Title": "Feed stalled — FulcrumBuy on NIFTY", "Source": "strategyrun", "RecordOnly": True}))
        self.assertFalse(tn.is_record_only({"Title": "Feed stalled — FulcrumBuy on NIFTY", "Source": "strategyrun"}))
        self.assertFalse(tn.is_record_only({"Title": "x", "RecordOnly": False}))


class RenderTests(unittest.TestCase):
    def test_the_startup_summary_passes_through_as_html(self):
        summary = tn.startup_summary({"active": 0, "open_positions": 0, "active_runs": [], "feeds": {}})
        text = tn.render_for_telegram({"Title": "Notifier online", "Message": summary, "Source": "process"})
        self.assertEqual(summary, text)
        self.assertTrue(text.startswith("🔔 <b>Notifier online</b>"))

    def test_backend_plain_text_is_still_escaped(self):
        text = tn.render_for_telegram({"Title": "Kill switch engaged", "Message": "P&L <floor>", "Source": "risk"})
        self.assertIn("P&amp;L &lt;floor&gt;", text)


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
