import _bootstrap  # noqa: F401

import unittest
from unittest import mock

import requests

from sentinel.model import Finding, Severity
from sentinel import notify
from sentinel.notify import TelegramNotifier, format_opened, format_resolved, redact

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


if __name__ == "__main__":
    unittest.main()
