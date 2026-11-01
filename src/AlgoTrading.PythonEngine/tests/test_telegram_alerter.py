"""
messaging/telegram_alerter.py: a send that fails prints its error without the
bot token. The token is part of the Bot API's URL and a requests error quotes
that URL, so printing the error as it was put the token in the runner's log
(2 Nov review).
"""
import contextlib
import io
import unittest
from unittest import mock

import _bootstrap  # noqa: F401
import requests

from messaging import telegram_alerter as ta

# Made up, and built so that no line here is secret-shaped (test_repo_secrets.py).
FAKE_TOKEN = "8123456789:" + "AAHdqTcvCH1vGWJx" * 2 + "fSeo"


class AlerterTokenTests(unittest.TestCase):
    def test_a_failed_send_prints_the_error_without_the_token(self):
        alerter = ta.TelegramAlerter(bot_token=FAKE_TOKEN, chat_id="-100111")

        def unreachable(url, **_):
            raise requests.ConnectionError(f"Max retries exceeded with url: {url}")

        out = io.StringIO()
        with mock.patch.object(ta.requests, "post", side_effect=unreachable), contextlib.redirect_stdout(out):
            alerter._send_sync("hello")  # noqa: SLF001 - the worker's own send, without the thread
        alerter.send_alert_async(None)  # stops the worker
        self.assertIn("Failed to send Telegram alert: Max retries exceeded with url: https://api.telegram.org/bot…/sendMessage",
                      out.getvalue())
        self.assertNotIn(FAKE_TOKEN, out.getvalue())

    def test_the_helper_hides_any_token_shape(self):
        self.assertEqual(ta.redact_bot_token(f"url: /bot{FAKE_TOKEN}/getMe"), "url: /bot…/getMe")
        self.assertEqual(ta.redact_bot_token("no token at 09:15"), "no token at 09:15")


if __name__ == "__main__":
    unittest.main()
