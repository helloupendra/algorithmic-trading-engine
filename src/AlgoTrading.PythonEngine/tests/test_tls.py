"""Certificate validation through the actual authenticated API transport."""
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest import mock

import _bootstrap  # noqa: F401
import requests
from core.api_client import AuthenticatedSession, PlatformApiClient
from core.tls import tls_verify


class TlsPolicyTests(unittest.TestCase):
    def setUp(self):
        self.env = mock.patch.dict(os.environ, {
            "VERIFY_SSL": "True", "API_CA_BUNDLE": "", "REQUESTS_CA_BUNDLE": "", "CURL_CA_BUNDLE": ""})
        self.env.start()
        self.addCleanup(self.env.stop)

    def test_default_verifies_certificates(self):
        self.assertIs(tls_verify(), True)
        self.assertIs(PlatformApiClient("https://api.example.invalid").verify_ssl, True)

    def test_insecure_and_malformed_settings_fail_closed(self):
        for setting in ("False", "0", "no", "typo", ""):
            with self.subTest(setting=setting), mock.patch.dict(os.environ, VERIFY_SSL=setting):
                with self.assertRaisesRegex(ValueError, "cannot be disabled"):
                    tls_verify()

    def test_explicit_bypass_fails_before_login_or_transport(self):
        with self.assertRaises(ValueError):
            PlatformApiClient("https://api.example.invalid", verify_ssl=False)
        with AuthenticatedSession() as session, mock.patch("requests.Session.request") as send, \
                mock.patch.object(session._provider, "get") as login:
            for path in ("/api/UserAuth/login", "/api/LiveData/latest"):
                with self.subTest(path=path), self.assertRaises(ValueError):
                    session.get("https://api.example.invalid" + path, verify=False)
            send.assert_not_called()
            login.assert_not_called()

    def test_bundle_precedence_and_missing_file(self):
        with tempfile.TemporaryDirectory() as directory:
            one, two = Path(directory) / "one.pem", Path(directory) / "two.pem"
            one.write_text("fixture")
            two.write_text("fixture")
            with mock.patch.dict(os.environ, REQUESTS_CA_BUNDLE=str(one), API_CA_BUNDLE=str(two)):
                self.assertEqual(tls_verify(), str(two.resolve()))
                self.assertEqual(tls_verify(str(one)), str(one.resolve()))
            with mock.patch.dict(os.environ, REQUESTS_CA_BUNDLE=str(one)):
                self.assertEqual(tls_verify(), str(one.resolve()))
            with mock.patch.dict(os.environ, CURL_CA_BUNDLE=str(two)):
                self.assertEqual(tls_verify(), str(two.resolve()))
            with self.assertRaisesRegex(ValueError, "not found"):
                tls_verify(str(Path(directory) / "missing.pem"))

    def test_relative_bundle_is_resolved_from_the_repository_not_cwd(self):
        root = Path(__file__).resolve().parents[3]
        with tempfile.NamedTemporaryFile(dir=root, suffix=".pem") as bundle:
            with mock.patch.dict(os.environ, API_CA_BUNDLE=Path(bundle.name).name):
                self.assertEqual(tls_verify(), str(Path(bundle.name).resolve()))

    def test_adapters_refuse_an_explicit_bypass_with_injected_transports_too(self):
        from analysis.api import ForecastApi
        from core.live.feed_runner import FeedRunner
        from market_data.live.vendors.fyers import FyersFeed
        from market_data.live.vendors.dhan import DhanFeed
        factories = [
            lambda: ForecastApi(http=mock.Mock(), verify_ssl=False),
            lambda: FeedRunner(mock.Mock(), http=mock.Mock(), publisher=mock.Mock(), verify_ssl=False),
            lambda: FyersFeed(http=mock.Mock(), verify_ssl=False),
            lambda: DhanFeed(http=mock.Mock(), verify_ssl=False),
        ]
        for factory in factories:
            with self.subTest(factory=factory), self.assertRaises(ValueError):
                factory()


class HttpsTransportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        executable = shutil.which("openssl")
        if not executable:
            raise unittest.SkipTest("OpenSSL is required to generate the disposable HTTPS certificate")
        cls.temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temp.cleanup)
        cls.cert = Path(cls.temp.name) / "cert.pem"
        key = Path(cls.temp.name) / "key.pem"
        subprocess.run([executable, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-keyout", str(key), "-out", str(cls.cert), "-subj", "/CN=localhost",
                        "-addext", "subjectAltName=DNS:localhost"],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_): pass
            def answer(self, body, status=200):
                data = json.dumps(body).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            def do_POST(self):
                self.rfile.read(int(self.headers.get("Content-Length", 0)))
                self.server.logins += 1
                self.answer({"accessToken": "synthetic-access"})
            def do_GET(self):
                self.server.reads += 1
                if self.server.reject_once:
                    self.server.reject_once = False
                    self.answer({}, 401)
                else:
                    self.answer({"symbol": "TEST", "lastTradedPrice": 123})
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(str(cls.cert), str(key))
        cls.server.socket = context.wrap_socket(cls.server.socket, server_side=True)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.addClassCleanup(cls.server.server_close)
        cls.addClassCleanup(cls.server.shutdown)
        cls.url = f"https://localhost:{cls.server.server_port}"

    def setUp(self):
        self.server.logins = self.server.reads = 0
        self.server.reject_once = False
        env = mock.patch.dict(os.environ, {"VERIFY_SSL": "True", "API_CA_BUNDLE": "",
            "REQUESTS_CA_BUNDLE": "", "CURL_CA_BUNDLE": "", "NO_PROXY": "localhost,127.0.0.1"})
        env.start()
        self.addCleanup(env.stop)

    def client(self, url=None, verify=None):
        client = PlatformApiClient(url or self.url, verify_ssl=verify,
                                   username="fixture-user", password="fixture-password")
        self.addCleanup(client.http.close)
        return client

    def test_untrusted_certificate_refuses_login_before_any_credentials_are_received(self):
        with self.assertRaises(requests.exceptions.SSLError):
            self.client().get_latest_quote("TEST")
        self.assertEqual(self.server.logins, 0)
        self.assertEqual(self.server.reads, 0)

    def test_private_ca_works_for_login_requests_and_401_reauthentication(self):
        with mock.patch.dict(os.environ, API_CA_BUNDLE=str(self.cert)):
            self.server.reject_once = True
            self.assertEqual(self.client().get_latest_quote("TEST")["lastTradedPrice"], 123)
        self.assertEqual(self.server.logins, 2)
        self.assertEqual(self.server.reads, 2)

    def test_explicit_ca_bundle_applies_to_login_and_data_requests(self):
        self.assertEqual(self.client(verify=str(self.cert)).get_latest_quote("TEST")["symbol"], "TEST")
        self.assertEqual(self.server.logins, 1)

    def test_trusting_the_ca_does_not_disable_hostname_verification(self):
        with self.assertRaises(requests.exceptions.SSLError):
            self.client(url=self.url.replace("localhost", "127.0.0.1"), verify=str(self.cert)).get_latest_quote("TEST")
        self.assertEqual(self.server.logins, 0)
