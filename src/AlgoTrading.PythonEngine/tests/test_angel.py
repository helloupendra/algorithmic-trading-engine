"""
Angel One SmartAPI module: the parts that must be right before a single live
call is made, and the ones that keep it out of the live desk's way.

Added 2026-09-16 alongside FYERS and Dhan, which were feeding production at
the time: nothing here registers a feed or writes to the database.
"""

import json
import os
import tempfile
import unittest
from datetime import date, datetime, timezone

from market_data.angel.client import (
    MAX_QUOTE_SYMBOLS,
    RATE_LIMIT_WAITS,
    AngelClient,
    AngelError,
    AngelRateLimited,
    interval_for,
    parse_candles,
    windows,
)
from market_data.angel.instruments import AngelInstruments, parse_expiry, parse_master
from market_data.angel.session import AngelAuthError, AngelCredentials, AngelSession, client_headers
from market_data.angel.totp import totp_now

MASTER = [
    {"token": "99926000", "symbol": "Nifty 50", "name": "NIFTY", "expiry": "", "strike": "0.000000",
     "lotsize": "1", "instrumenttype": "AMXIDX", "exch_seg": "NSE", "tick_size": "0.000000"},
    {"token": "99926009", "symbol": "Nifty Bank", "name": "BANKNIFTY", "expiry": "", "strike": "0.000000",
     "lotsize": "1", "instrumenttype": "AMXIDX", "exch_seg": "NSE", "tick_size": "0.000000"},
    {"token": "2885", "symbol": "RELIANCE-EQ", "name": "RELIANCE", "expiry": "", "strike": "-1.000000",
     "lotsize": "1", "instrumenttype": "", "exch_seg": "NSE", "tick_size": "5.000000"},
    {"token": "43521", "symbol": "NIFTY22SEP2624000CE", "name": "NIFTY", "expiry": "22SEP2026",
     "strike": "2400000.000000", "lotsize": "65", "instrumenttype": "OPTIDX", "exch_seg": "NFO",
     "tick_size": "5.000000"},
    {"token": "43522", "symbol": "NIFTY22SEP2624000PE", "name": "NIFTY", "expiry": "22SEP2026",
     "strike": "2400000.000000", "lotsize": "65", "instrumenttype": "OPTIDX", "exch_seg": "NFO",
     "tick_size": "5.000000"},
    {"token": "57920", "symbol": "NIFTY25SEP26FUT", "name": "NIFTY", "expiry": "25SEP2026", "strike": "0.000000",
     "lotsize": "65", "instrumenttype": "FUTIDX", "exch_seg": "NFO", "tick_size": "5.000000"},
    {"token": "", "symbol": "JUNK", "name": "JUNK", "expiry": "", "strike": "", "lotsize": "",
     "instrumenttype": "", "exch_seg": "NSE", "tick_size": ""},
]


class TotpTests(unittest.TestCase):
    def test_rfc_6238_vectors(self):
        secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"      # "12345678901234567890" in base32
        self.assertEqual("94287082", totp_now(secret, at=59, digits=8))
        self.assertEqual("07081804", totp_now(secret, at=1111111109, digits=8))

    def test_the_secret_is_taken_as_shown_spaces_case_and_padding_included(self):
        self.assertEqual(totp_now("GEZDGNBVGY3TQOJQ", at=59),
                         totp_now("gezd gnbv gy3t qojq", at=59))

    def test_an_empty_secret_says_which_variable_to_set(self):
        with self.assertRaisesRegex(ValueError, "ANGEL_TOTP_SECRET"):
            totp_now("")


class InstrumentTests(unittest.TestCase):
    def setUp(self):
        self.master = AngelInstruments(parse_master(MASTER))

    def test_rows_without_a_token_are_dropped(self):
        self.assertEqual(6, len(self.master))

    def test_expiry_parsing(self):
        self.assertEqual(date(2026, 9, 22), parse_expiry("22SEP2026"))
        self.assertIsNone(parse_expiry(""))
        self.assertIsNone(parse_expiry("next week"))

    def test_equity_and_index_lookups_use_the_platforms_own_symbols(self):
        self.assertEqual("2885", self.master.equity("NSE:RELIANCE-EQ").token)
        self.assertEqual("99926000", self.master.index("NSE:NIFTY50-INDEX").token)
        self.assertEqual("99926009", self.master.index("NSE:NIFTYBANK-INDEX").token)
        self.assertIsNone(self.master.equity("NSE:NOSUCH-EQ"))

    def test_an_option_is_matched_on_every_part_not_on_a_spelled_out_symbol(self):
        # Angel writes strikes multiplied by 100; a near miss would price the
        # wrong contract, so each part is checked.
        call = self.master.derivative(exchange="NSE", name="NIFTY", expiry=date(2026, 9, 22),
                                      strike=24000, option_type="CE")
        put = self.master.derivative(exchange="NSE", name="NIFTY", expiry=date(2026, 9, 22),
                                     strike=24000, option_type="PE")
        self.assertEqual(("43521", "43522"), (call.token, put.token))
        self.assertEqual(65, call.lot_size)
        self.assertIsNone(self.master.derivative(exchange="NSE", name="NIFTY", expiry=date(2026, 9, 22),
                                                 strike=24050, option_type="CE"))

    def test_a_future_is_not_answered_with_an_option(self):
        future = self.master.derivative(exchange="NSE", name="NIFTY", expiry=date(2026, 9, 25))
        self.assertEqual("57920", future.token)

    def test_search_finds_what_angel_calls_something(self):
        hits = self.master.search("nifty", exchange="NSE")
        self.assertIn("Nifty 50", [h.symbol for h in hits])

    def test_load_reads_a_saved_master(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "scrip-master.json")
            with open(path, "w") as fh:
                json.dump(MASTER, fh)
            self.assertEqual(6, len(AngelInstruments.load(path)))


class SessionTests(unittest.TestCase):
    def creds(self, **kw):
        base = {"ANGEL_API_KEY": "key", "ANGEL_CLIENT_CODE": "A1234", "ANGEL_PIN": "1234",
                "ANGEL_TOTP_SECRET": "GEZDGNBVGY3TQOJQ"}
        base.update(kw)
        return AngelCredentials.from_env(base)

    def test_missing_credentials_are_named_not_guessed(self):
        creds = self.creds(ANGEL_PIN="", ANGEL_TOTP_SECRET="")
        self.assertEqual(["ANGEL_PIN", "ANGEL_TOTP_SECRET"], creds.missing)
        self.assertFalse(creds.can_login)
        with self.assertRaisesRegex(AngelAuthError, "ANGEL_PIN, ANGEL_TOTP_SECRET"):
            AngelSession(creds, cache_path=None).login()

    def test_a_successful_login_keeps_the_tokens_and_signs_later_calls(self):
        sent = {}

        def post(url, json, headers):
            sent["url"], sent["json"], sent["headers"] = url, json, headers
            return {"status": True, "data": {"jwtToken": "jwt-1", "refreshToken": "r", "feedToken": "f"}}

        session = AngelSession(self.creds(), post=post, cache_path=None)
        session.login()
        self.assertTrue(sent["url"].endswith("/rest/auth/angelbroking/user/v1/loginByPassword"))
        self.assertEqual({"clientcode", "password", "totp"}, set(sent["json"]))
        self.assertEqual("key", sent["headers"]["X-PrivateKey"])
        self.assertEqual("Bearer jwt-1", session.headers()["Authorization"])
        self.assertEqual("f", session.feed_token)
        self.assertTrue(session.is_live)

    def test_a_refusal_carries_the_vendors_words_and_a_cause_worth_naming(self):
        def refuse(message, code):
            def post(url, json, headers):
                return {"status": False, "message": message, "errorcode": code}
            with self.assertRaises(AngelAuthError) as caught:
                AngelSession(self.creds(), post=post, cache_path=None).login()
            return str(caught.exception)

        totp = refuse("Invalid totp", "AB1050")
        self.assertIn("Invalid totp", totp)
        self.assertIn("clock", totp)                      # the usual cause

        blocked = refuse("Client is blocked for trading", "AB1004")
        self.assertIn("static IP", blocked)               # the other usual cause

    def test_a_live_session_is_not_logged_in_again(self):
        calls = []

        def post(url, json, headers):
            calls.append(url)
            return {"status": True, "data": {"jwtToken": "jwt"}}

        session = AngelSession(self.creds(), post=post, cache_path=None)
        session.login()
        session.login()
        self.assertEqual(1, len(calls))

    def test_headers_carry_the_identifiers_smartapi_demands(self):
        headers = client_headers("key")
        for name in ("X-UserType", "X-SourceID", "X-ClientLocalIP", "X-ClientPublicIP", "X-MACAddress"):
            self.assertTrue(headers.get(name), name)


class SessionCacheTests(unittest.TestCase):
    """
    A session is kept on disk between processes: every CLI command would
    otherwise log in again, and Angel answers 403 to logins repeated within
    seconds (2026-09-16). The tests own their cache file — an early version of
    this suite wrote fake tokens over the real one.
    """

    def creds(self, code="A1234"):
        return AngelCredentials(api_key="key", client_code=code, pin="1234", totp_secret="GEZDGNBVGY3TQOJQ")

    def post_ok(self, calls):
        def post(url, json, headers):
            calls.append(url)
            return {"status": True, "data": {"jwtToken": "jwt-1", "feedToken": "feed-1"}}
        return post

    def test_a_second_process_reuses_the_session_instead_of_logging_in_again(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "session.json")
            calls = []
            AngelSession(self.creds(), post=self.post_ok(calls), cache_path=path).login()
            second = AngelSession(self.creds(), post=self.post_ok(calls), cache_path=path)
            self.assertTrue(second.is_live)
            second.login()
            self.assertEqual(1, len(calls))
            self.assertEqual("Bearer jwt-1", second.headers()["Authorization"])

    def test_the_cache_is_readable_only_by_this_user(self):
        import stat as stat_module
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "session.json")
            AngelSession(self.creds(), post=self.post_ok([]), cache_path=path).login()
            mode = stat_module.S_IMODE(os.stat(path).st_mode)
            self.assertEqual(0o600, mode)

    def test_another_accounts_session_is_ignored(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "session.json")
            AngelSession(self.creds("A1234"), post=self.post_ok([]), cache_path=path).login()
            other = AngelSession(self.creds("B9999"), post=self.post_ok([]), cache_path=path)
            self.assertFalse(other.is_live)

    def test_a_broken_cache_is_ignored_not_fatal(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "session.json")
            with open(path, "w") as fh:
                fh.write("{not json")
            self.assertFalse(AngelSession(self.creds(), cache_path=path).is_live)


class ClientTests(unittest.TestCase):
    def build(self, answer):
        calls = []

        def post(url, json, headers):
            calls.append((url, json))
            return answer(url, json) if callable(answer) else answer

        session = AngelSession(AngelCredentials(api_key="k", client_code="c", pin="p",
                                                totp_secret="GEZDGNBVGY3TQOJQ"),
                               post=lambda *a, **k: {"status": True, "data": {"jwtToken": "jwt"}},
                               cache_path=None)
        return AngelClient(session=session, post=post, min_interval=0, sleep=lambda _: None), calls

    def test_intervals_map_from_the_platforms_resolutions(self):
        self.assertEqual("FIVE_MINUTE", interval_for("5m"))
        self.assertEqual("ONE_DAY", interval_for("D"))
        self.assertEqual("ONE_MINUTE", interval_for("ONE_MINUTE"))
        with self.assertRaisesRegex(ValueError, "none of them"):
            interval_for("2m")

    def test_a_long_range_is_cut_to_what_one_request_may_carry(self):
        ranges = windows(date(2026, 1, 1), date(2026, 6, 30), "ONE_MINUTE")
        self.assertEqual(date(2026, 1, 1), ranges[0][0])
        self.assertEqual(date(2026, 6, 30), ranges[-1][1])
        self.assertTrue(all((hi - lo).days + 1 <= 30 for lo, hi in ranges))
        self.assertEqual(1, len(windows(date(2026, 1, 1), date(2026, 6, 30), "ONE_DAY")))

    def test_candles_are_typed_and_a_broken_row_is_dropped(self):
        bars = parse_candles([["2026-09-16T09:15:00+05:30", 1, 2, 0.5, 1.5, 100], ["bad"], []])
        self.assertEqual(1, len(bars))
        self.assertEqual(9, bars[0].timestamp.hour)
        self.assertEqual(100.0, bars[0].volume)

    def test_a_refusal_is_raised_with_the_vendors_message_even_on_http_200(self):
        client, _ = self.build({"status": False, "message": "Invalid Token", "errorcode": "AB1010"})
        with self.assertRaisesRegex(AngelError, "Invalid Token"):
            client.ltp("NSE", "RELIANCE-EQ", "2885")

    def test_quotes_refuse_more_instruments_than_smartapi_takes(self):
        client, _ = self.build({"status": True, "data": {}})
        too_many = {"NSE": [str(i) for i in range(MAX_QUOTE_SYMBOLS + 1)]}
        with self.assertRaisesRegex(ValueError, "at most 50"):
            client.quotes(too_many)

    def test_history_asks_once_per_window_and_returns_every_bar(self):
        def answer(url, body):
            start = body["fromdate"][:10]
            return {"status": True, "data": [[f"{start}T09:15:00+05:30", 1, 2, 0.5, 1.5, 10]]}

        client, calls = self.build(answer)
        bars = client.history("NSE", "99926000", "1m", date(2026, 1, 1), date(2026, 3, 31))
        self.assertEqual(3, len(calls))         # 90 days at 30 days a request
        self.assertEqual(3, len(bars))
        self.assertTrue(all(c[0].endswith("/historical/v1/getCandleData") for c in calls))

    def test_a_rate_limit_is_waited_out_then_the_call_succeeds(self):
        # 2026-09-16: the historical endpoint answered 403 "Access denied
        # because of exceeding access rate" in plain text, and the module
        # reported "no JSON" — useless. Now it waits and retries.
        answers = [AngelRateLimited("Access denied because of exceeding access rate (HTTP 403)"),
                   {"status": True, "data": []}]
        waits = []

        def answer(url, body):
            got = answers.pop(0)
            if isinstance(got, Exception):
                raise got
            return got

        client, calls = self.build(answer)
        client._sleep = waits.append
        client.option_greeks("NIFTY", date(2026, 9, 22))
        self.assertEqual(2, len(calls))
        self.assertEqual([RATE_LIMIT_WAITS[0]], waits)

    def test_a_persistent_rate_limit_says_so_plainly(self):
        def answer(url, body):
            raise AngelRateLimited("Access denied because of exceeding access rate (HTTP 403)")

        client, calls = self.build(answer)
        client._sleep = lambda _: None
        with self.assertRaisesRegex(AngelRateLimited, "still rate-limited"):
            client.option_greeks("NIFTY", date(2026, 9, 22))
        self.assertEqual(1 + len(RATE_LIMIT_WAITS), len(calls))

    def test_greeks_use_angels_expiry_spelling(self):
        client, calls = self.build({"status": True, "data": []})
        client.option_greeks("nifty", date(2026, 9, 22))
        self.assertEqual({"name": "NIFTY", "expirydate": "22SEP2026"}, calls[0][1])


if __name__ == "__main__":
    unittest.main()
