import _bootstrap  # noqa: F401

import unittest
from datetime import datetime, timezone
from unittest import mock

import requests

from sentinel.model import Finding, Severity
from sentinel import notify
from sentinel.notify import (TelegramNotifier, format_again, format_flapping, format_opened, format_resolved,
                             format_seen_before, format_settled, redact)

# The shared redaction spec (sentinel/notify.py) as cases. The same table is
# in tests/AlgoTrading.UnitTests/IncidentsControllerTests.cs and
# web/src/lib/incidents.test.ts: a case added here is added there.
MASKED = [
    ("POSTGRES_PASSWORD=hunter2hunter2", "POSTGRES_PASSWORD=…"),
    ("TELEGRAM_BOT_TOKEN=abcdefghij", "TELEGRAM_BOT_TOKEN=…"),
    ("DHAN_PIN=1234", "DHAN_PIN=…"),
    ("FYERS_SECRET_KEY=ABCD1234XYZ", "FYERS_SECRET_KEY=…"),
    ("JWT_SECRET_KEY=supersecretjwtkey", "JWT_SECRET_KEY=…"),
    ("DHAN_API_SECRET=abcdef123", "DHAN_API_SECRET=…"),
    ("ANGEL_API_KEY=abcdef12", "ANGEL_API_KEY=…"),
    ('{"trading_pin": "4821"}', '{"trading_pin": "…"}'),
    ('"access_token": "abcDEF123456"', '"access_token": "…"'),
    ("refreshToken=Zm9vYmFyYmF6", "refreshToken=…"),
    ("X-Api-Key: abcd1234", "X-Api-Key: …"),
    ("Host=db;Password=pa55word;Database=algotrading", "Host=db;Password=…;Database=algotrading"),
    ("GET /login?client_secret=XYZ987654&state=1", "GET /login?client_secret=…&state=1"),
    ("TOTP=123456", "TOTP=…"),
    ('password="correct horse battery"', 'password="…"'),
    ("postgresql://postgres:S3cretPassw0rd@localhost:5432/algotrading",
     "postgresql://postgres:…@localhost:5432/algotrading"),
    ("redis://:S3cretPassw0rd@localhost:6379/0", "redis://:…@localhost:6379/0"),
    ("Authorization: Basic dXNlcjpTM2NyZXRQYXNzdzByZA==", "Authorization: Basic …"),
    ("curl -H 'Authorization: Bearer abcdefghijklmnop'", "curl -H 'Authorization: Bearer …'"),
    ("headers={'Authorization': 'Bearer abc.def.ghi-jkl'}", "headers={'Authorization': 'Bearer …'}"),
    ("Bearer abcdefghijklmnopqrstuvwxyz123", "Bearer …"),
    ("url: /bot8123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0/sendMessage", "url: /bot…/sendMessage"),  # pragma: allowlist secret
    ("chat 123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0 said", "chat … said"),  # pragma: allowlist secret
    ("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiIsImV4cCI6MX0.c2lnbmF0dXJlLXZhbHVlLWhlcmU expired",  # pragma: allowlist secret
     "token … expired"),
]

# The desk's own prose. A mask that garbles it is a failure too.
LEFT_ALONE = [
    "Generate a new Dhan token before tomorrow's 08:45 start",
    "SSH password guessing from 1.2.3.4 was banned",
    "Fyers rejected the desk's credential (token expired or invalid)",
    "FYERS token expired at 08:45; strategies are running deaf.",
    "Password reset for user coderforchange",
    "Sentinel's secret scan found a key in config/x.json",
    "Generate a new Dhan token\n\nEvidence:\n• feed silent",
    "Skipping: 3 runners already stopped",
    "input_tokens: 512",
    "spinning=3",
    "if token == expected:",
    "Dhan feed: 8 reconnect(s) carried no ticks — waiting 80s",
    "429 Client Error: Too Many Requests for url: http://localhost:5025/api/UserAuth/login",
    "https://example.com:8443/path",
    "NSE feed silent for 120 s; newest tick 11:27:35 IST on NSE:NIFTY50-INDEX",
]


class ChannelTests(unittest.TestCase):
    """Incidents go to the system channel when there is one (owner, 27 Sep)."""

    def test_the_system_chat_when_set(self):
        n = notify.notifier_from_env({"TELEGRAM_BOT_TOKEN": "123:abc", "TELEGRAM_CHAT_ID": "-100111",
                                      "TELEGRAM_SYSTEM_CHAT_ID": "-100222"}, dry_run=False)
        self.assertIsInstance(n, TelegramNotifier)
        self.assertEqual("-100222", n._chat_id)

    def test_the_one_chat_without_it(self):
        n = notify.notifier_from_env({"TELEGRAM_BOT_TOKEN": "123:abc", "TELEGRAM_CHAT_ID": "-100111"}, dry_run=False)
        self.assertEqual("-100111", n._chat_id)


class RedactionSpecTests(unittest.TestCase):
    def test_every_credential_shape_is_masked_and_its_label_kept(self):
        for text, expected in MASKED:
            with self.subTest(text=text):
                self.assertEqual(expected, redact(text))

    def test_the_desk_s_own_prose_is_left_alone(self):
        for text in LEFT_ALONE:
            with self.subTest(text=text):
                self.assertEqual(text, redact(text))

    def test_masking_twice_is_masking_once(self):
        for text, expected in MASKED:
            with self.subTest(text=text):
                self.assertEqual(expected, redact(redact(text)))

    def test_a_long_line_is_cheap(self):
        # No pattern may backtrack its way through a long identifier: a round must not stall on one log line.
        import time
        started = time.perf_counter()
        redact("token_" * 20_000 + " " + "a" * 100_000)
        self.assertLess(time.perf_counter() - started, 0.5)

    def test_a_message_keeps_its_evidence_header(self):
        finding = Finding(agent="health", rule="fyers-token", severity=Severity.HIGH,
                          title="FYERS token expired", summary="Fyers rejected the desk's credential (token",
                          fingerprint="health:fyers-token", evidence=["expired or invalid"],
                          suggestion="Generate a new Dhan token before tomorrow's 08:45 start.")
        text = format_opened(finding, 12)
        self.assertIn("(token\n\nEvidence:\n• expired or invalid", text)
        self.assertIn("Generate a new Dhan token before tomorrow's 08:45 start.", text)

    def test_an_incident_the_database_did_not_take_says_so_instead_of_a_number(self):
        finding = Finding(agent="health", rule="api-down", severity=Severity.HIGH, title="API down",
                          summary="s", fingerprint="health:api-down")
        self.assertIn("not stored", format_opened(finding, 0).splitlines()[1])
        self.assertIn("#7 · health/api-down", format_opened(finding, 7))
        self.assertTrue(format_resolved("API down", 0, Severity.HIGH).startswith("✅ RESOLVED (never stored)"))


class Reply:
    def __init__(self, status, body=None, text_body=None):
        self.status_code = status
        self.ok = 200 <= status < 300
        self._body = body
        self._text = text_body

    def json(self):
        if self._body is None:
            raise ValueError("not JSON")
        return self._body


class TelegramNotifierTests(unittest.TestCase):
    def setUp(self):
        self.notifier = TelegramNotifier("123:abc", "42")
        self.slept = []
        patcher = mock.patch.object(notify.time, "sleep", side_effect=self.slept.append)
        patcher.start()
        self.addCleanup(patcher.stop)

    def post(self, *replies):
        return mock.patch.object(notify.requests, "post", side_effect=list(replies))

    def test_a_429_is_not_slept_through_its_wait_goes_back_to_the_engine(self):
        with self.post(Reply(429, {"ok": False, "parameters": {"retry_after": 37}})) as post:
            self.assertFalse(self.notifier.send("hello"))
        self.assertEqual(1, post.call_count, "no second try inside the round")
        self.assertEqual(37.0, self.notifier.retry_after)
        self.assertTrue(all(s <= 1.1 for s in self.slept), self.slept)

    def test_a_refused_message_is_marked_so_the_engine_does_not_try_it_forever(self):
        with self.post(Reply(400, {"ok": False, "description": "Bad Request: message is too long"})):
            self.assertFalse(self.notifier.send("x"))
        self.assertTrue(self.notifier.refused)
        with self.post(Reply(200, {"ok": True})):
            self.assertTrue(self.notifier.send("x"))
        self.assertFalse(self.notifier.refused)
        self.assertIsNone(self.notifier.retry_after)

    def test_a_reply_that_is_not_json_is_a_failed_send_not_a_crash(self):
        with self.post(Reply(429), Reply(502)):
            self.assertFalse(self.notifier.send("x"))
            self.assertEqual(5.0, self.notifier.retry_after)
            self.assertFalse(self.notifier.send("x"))

    def test_no_network_is_a_failed_send(self):
        with self.post(requests.ConnectionError("no route")):
            self.assertFalse(self.notifier.send("x"))

    def test_text_that_is_not_utf8_is_repaired_not_refused(self):
        with self.post(Reply(200, {"ok": True})) as post:
            self.assertTrue(self.notifier.send("feed log: caf\udce9"))
        self.assertEqual("feed log: caf?", post.call_args.kwargs["json"]["text"])


class SeenBeforeFormatTests(unittest.TestCase):
    """The lines that say a problem has happened before: in the evidence with a prefix, in the message without."""

    WHEN = datetime(2026, 9, 24, 5, 57, tzinfo=timezone.utc)   # 11:27 IST

    def test_the_first_time_says_nothing(self):
        self.assertEqual([], format_seen_before(0, self.WHEN, "Restarted the feed"))

    def test_how_often_when_last_in_ist_and_what_was_done(self):
        self.assertEqual(["history: Seen before: once, last on 24 Sep 2026, 11:27 IST",
                          "history: Last time: Restarted the feed"],
                         format_seen_before(1, self.WHEN, "Restarted the feed"))
        self.assertEqual(["history: Seen before: 4 times, last on 24 Sep 2026, 11:27 IST"],
                         format_seen_before(4, self.WHEN, ""))

    def test_a_timestamp_without_a_zone_is_utc_and_none_is_left_out(self):
        self.assertEqual(["history: Seen before: once, last on 24 Sep 2026, 11:27 IST"],
                         format_seen_before(1, self.WHEN.replace(tzinfo=None)))
        self.assertEqual(["history: Seen before: 2 times"], format_seen_before(2, None))

    def test_a_long_resolution_over_several_lines_is_one_short_line(self):
        lines = format_seen_before(1, self.WHEN, "Closed the sixth\nDhan socket.  " + "x" * 400)
        last_time = lines[1].removeprefix("history: Last time: ")
        self.assertTrue(last_time.startswith("Closed the sixth Dhan socket. x"))
        self.assertNotIn("\n", last_time)
        self.assertEqual(notify.LAST_TIME_CHARS, len(last_time))
        self.assertTrue(last_time.endswith("…"))

    def test_the_message_carries_them_under_the_summary_and_redacts_them(self):
        f = Finding(agent="health", rule="feed-silent", severity=Severity.HIGH, title="NSE feed silent",
                    summary="No NSE tick for 120 s.", fingerprint="health:feed-silent:NSE",
                    evidence=["newest tick 11:27:35 IST"])
        history = format_seen_before(2, self.WHEN, "Set DHAN_PIN=1234 in .env again")
        text = format_opened(f, 9, history=history)
        self.assertIn("No NSE tick for 120 s.\n\nSeen before: 2 times, last on 24 Sep 2026, 11:27 IST\n"
                      "Last time: Set DHAN_PIN=… in .env again\n\nEvidence:\n• newest tick 11:27:35 IST", text)
        self.assertNotIn("1234", text)
        self.assertNotIn("Seen before", format_opened(f, 9))


class FlappingMessageTests(unittest.TestCase):
    """What a flapping incident says (28 Sep 13:06-13:28): which return it is, and what comes next."""

    SINCE = datetime(2026, 9, 28, 7, 37, 30, tzinfo=timezone.utc)   # 13:07:30 IST
    NOW = datetime(2026, 9, 28, 7, 40, 30, tzinfo=timezone.utc)

    def test_a_return_says_which_time_it_is_under_the_header(self):
        f = Finding(agent="health", rule="feed-silent", severity=Severity.CRITICAL,
                    title="NSE/BSE ticks have stopped while the market is open", summary="No live NSE/BSE tick.",
                    fingerprint="health:feed-silent:NSE")
        text = format_opened(f, 135, again=format_again(2, self.SINCE, self.NOW))
        head, number, blank, again, blank2, summary = text.splitlines()[:6]
        self.assertEqual("🔴 AGAIN [CRITICAL] NSE/BSE ticks have stopped while the market is open", head)
        self.assertTrue(again.startswith("Back again: the 2nd time since 13:07 IST."), again)
        self.assertIn("at most one message about it every 30 min", again)
        self.assertEqual("No live NSE/BSE tick.", summary)
        self.assertTrue(format_opened(f, 135, escalated=True, again="x").startswith("🔴 ESCALATED"))

    def test_ordinals(self):
        said = [format_again(n, self.SINCE, self.NOW).split(" time ")[0].removeprefix("Back again: the ")
                for n in (2, 3, 4, 5, 11, 12, 13, 21, 22, 23, 101, 111)]
        self.assertEqual(["2nd", "3rd", "4th", "5th", "11th", "12th", "13th", "21st", "22nd", "23rd", "101st",
                          "111th"], said)

    def test_another_day_is_named(self):
        self.assertIn("since 27 Sep 13:07 IST", format_again(3, self.SINCE.replace(day=27), self.NOW))

    def test_the_held_back_resolved_says_how_often_and_since_when(self):
        note = format_settled(8, self.SINCE, datetime(2026, 9, 28, 8, 0, tzinfo=timezone.utc), self.NOW)
        text = format_resolved("MCX ticks have stopped while the market is open", 136, Severity.CRITICAL, note=note)
        self.assertEqual("✅ RESOLVED #136 [CRITICAL] MCX ticks have stopped while the market is open\n"
                         "It happened 8 times since 13:07 IST; clear since 13:30 IST (30 min without coming back).",
                         text)

    def test_the_console_line(self):
        self.assertEqual("flapping: 5th episode since 13:07 IST: it cleared and came back within 30 min each time, "
                         "so it stays this one incident", format_flapping(5, self.SINCE, self.NOW))


if __name__ == "__main__":
    unittest.main()
