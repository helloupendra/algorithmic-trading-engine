"""
market_data/angel/session.py

Credentials and the login SmartAPI asks for.

Angel One's session is client code + PIN + a TOTP code, exchanged for a JWT
that every later call carries. The app is also bound to the static IP it was
registered with, so a call from anywhere else is refused however good the
token is — worth knowing before blaming the credentials.

Nothing is printed: the API key, the PIN, the TOTP secret and the JWT never
reach a log line, only whether they are set.
"""

from __future__ import annotations

import json
import os
import socket
import stat
import uuid
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Dict, Optional

from market_data.angel.totp import totp_now

DEFAULT_ROOT = "https://apiconnect.angelone.in"
LOGIN_PATH = "/rest/auth/angelbroking/user/v1/loginByPassword"
PROFILE_PATH = "/rest/secure/angelbroking/user/v1/getProfile"

#: A JWT lasts the trading day; it is refreshed a little early rather than on
#: the first refusal, so a long-running job never trades on a dead token.
SESSION_TTL_HOURS = 8.0

#: Where a session is kept between processes. Every CLI command would otherwise
#: log in again, and Angel answers 403 to logins repeated within a few seconds —
#: which looks exactly like bad credentials (2026-09-16).
SESSION_CACHE = os.path.join(os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data")),
                             "angel", "session.json")


class AngelAuthError(RuntimeError):
    """The vendor refused the credentials, or they are not configured."""


@dataclass(frozen=True)
class AngelCredentials:
    """What the SmartAPI app needs. Read from the environment; never logged."""
    api_key: str
    client_code: str = ""
    pin: str = ""
    totp_secret: str = ""
    root_url: str = DEFAULT_ROOT

    @classmethod
    def from_env(cls, env: Optional[Dict[str, str]] = None) -> "AngelCredentials":
        env = env or os.environ
        return cls(
            api_key=(env.get("ANGEL_API_KEY") or "").strip(),
            client_code=(env.get("ANGEL_CLIENT_CODE") or "").strip(),
            pin=(env.get("ANGEL_PIN") or "").strip(),
            totp_secret=(env.get("ANGEL_TOTP_SECRET") or "").strip(),
            root_url=(env.get("ANGEL_ROOT_URL") or DEFAULT_ROOT).strip().rstrip("/"),
        )

    @property
    def missing(self) -> list[str]:
        """Which environment variables still have to be filled in, by name."""
        names = {"ANGEL_API_KEY": self.api_key, "ANGEL_CLIENT_CODE": self.client_code,
                 "ANGEL_PIN": self.pin, "ANGEL_TOTP_SECRET": self.totp_secret}
        return [name for name, value in names.items() if not value]

    @property
    def can_login(self) -> bool:
        return not self.missing


def client_headers(api_key: str, local_ip: str = "", public_ip: str = "", mac: str = "") -> Dict[str, str]:
    """
    The headers SmartAPI wants on every request.

    The three client identifiers are mandatory and are not checked against
    anything: they exist for the vendor's own audit trail. Real values are
    used where they can be read without a network call.
    """
    return {
        "Content-Type": "application/json",
        "Accept": "application/json",
        "X-UserType": "USER",
        "X-SourceID": "WEB",
        "X-ClientLocalIP": local_ip or _local_ip(),
        "X-ClientPublicIP": public_ip or _local_ip(),
        "X-MACAddress": mac or _mac(),
        "X-PrivateKey": api_key,
    }


def _local_ip() -> str:
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.settimeout(0.2)
            probe.connect(("8.8.8.8", 80))     # no packet is sent; it just picks the route
            return probe.getsockname()[0]
    except Exception:
        return "127.0.0.1"


def _mac() -> str:
    node = uuid.getnode()
    return ":".join(f"{(node >> shift) & 0xFF:02x}" for shift in range(40, -8, -8))


class AngelSession:
    """Holds one logged-in SmartAPI session and hands out its headers."""

    def __init__(self, credentials: Optional[AngelCredentials] = None, post: Optional[Callable] = None,
                 clock: Callable[[], datetime] = lambda: datetime.now(timezone.utc),
                 now: Optional[Callable[[], float]] = None,
                 cache_path: Optional[str] = SESSION_CACHE) -> None:
        self.credentials = credentials or AngelCredentials.from_env()
        self._post = post
        self._clock = clock
        self._totp_clock = now
        self._cache_path = cache_path
        self.jwt: Optional[str] = None
        self.refresh_token: Optional[str] = None
        self.feed_token: Optional[str] = None
        self.logged_in_at: Optional[datetime] = None
        self._load_cache()

    # ------------------------------------------------------- session cache

    def _load_cache(self) -> None:
        """Reuse a session an earlier process left behind, while it is still young."""
        if not self._cache_path or not os.path.exists(self._cache_path):
            return
        try:
            with open(self._cache_path) as fh:
                saved = json.load(fh)
            if saved.get("clientCode") != self.credentials.client_code:
                return          # someone else's session
            at = datetime.fromisoformat(saved["at"])
            if self._clock() - at >= timedelta(hours=SESSION_TTL_HOURS):
                return
            self.jwt = saved.get("jwt")
            self.feed_token = saved.get("feedToken")
            self.refresh_token = saved.get("refreshToken")
            self.logged_in_at = at
        except (OSError, ValueError, KeyError):
            return              # a broken cache is not worth a failure

    def _save_cache(self) -> None:
        if not self._cache_path or not self.jwt or self.logged_in_at is None:
            return
        try:
            os.makedirs(os.path.dirname(self._cache_path), exist_ok=True)
            tmp = self._cache_path + ".part"
            with open(tmp, "w") as fh:
                json.dump({"clientCode": self.credentials.client_code, "jwt": self.jwt,
                           "feedToken": self.feed_token, "refreshToken": self.refresh_token,
                           "at": self.logged_in_at.isoformat()}, fh)
            # Readable only by this user: it holds a live token.
            os.chmod(tmp, stat.S_IRUSR | stat.S_IWUSR)
            os.replace(tmp, self._cache_path)
        except OSError:
            pass                # a cache that cannot be written is not fatal

    # ------------------------------------------------------------- plumbing

    def _poster(self) -> Callable:
        if self._post is not None:
            return self._post
        import requests

        def post(url: str, json: dict, headers: dict) -> dict:
            response = requests.post(url, json=json, headers=headers, timeout=30)
            try:
                body = response.json()
            except ValueError:
                raise AngelAuthError(f"SmartAPI answered HTTP {response.status_code} with no JSON") from None
            if response.status_code >= 400 and isinstance(body, dict) and not body.get("message"):
                raise AngelAuthError(f"SmartAPI answered HTTP {response.status_code}")
            return body

        self._post = post
        return post

    @property
    def is_live(self) -> bool:
        if not self.jwt or self.logged_in_at is None:
            return False
        return self._clock() - self.logged_in_at < timedelta(hours=SESSION_TTL_HOURS)

    def headers(self) -> Dict[str, str]:
        base = client_headers(self.credentials.api_key)
        if self.jwt:
            base["Authorization"] = f"Bearer {self.jwt}"
        return base

    # ---------------------------------------------------------------- login

    def login(self, force: bool = False) -> "AngelSession":
        """Log in unless a live session is already held. Raises AngelAuthError with the vendor's own words."""
        if self.is_live and not force:
            return self
        missing = self.credentials.missing
        if missing:
            raise AngelAuthError(
                "Angel One is not configured: set " + ", ".join(missing) + " in .env. "
                "The client code is the login id, the PIN the 4-digit trading PIN, and the TOTP secret "
                "the base32 string shown when 2FA was set up.")
        body = {
            "clientcode": self.credentials.client_code,
            "password": self.credentials.pin,
            "totp": totp_now(self.credentials.totp_secret, at=(self._totp_clock() if self._totp_clock else None)),
        }
        answer = self._poster()(f"{self.credentials.root_url}{LOGIN_PATH}", body, client_headers(self.credentials.api_key))
        if not isinstance(answer, dict) or not answer.get("status"):
            raise AngelAuthError(_refusal(answer))
        data = answer.get("data") or {}
        self.jwt = data.get("jwtToken") or data.get("jwtoken")
        self.refresh_token = data.get("refreshToken")
        self.feed_token = data.get("feedToken")
        if not self.jwt:
            raise AngelAuthError("SmartAPI accepted the login but returned no token")
        self.logged_in_at = self._clock()
        self._save_cache()
        return self


def _refusal(answer: Any) -> str:
    """The vendor's reason, with the two that have a cause worth naming spelled out."""
    if not isinstance(answer, dict):
        return f"SmartAPI refused the login: {str(answer)[:200]}"
    message = str(answer.get("message") or "").strip() or "no reason given"
    code = str(answer.get("errorcode") or "").strip()
    hint = ""
    if "invalid totp" in message.lower():
        hint = " (the TOTP secret or the machine clock is wrong)"
    elif code in ("AB1004", "AB1050") or "block" in message.lower():
        hint = " (the app is bound to its registered static IP — calls from another machine are refused)"
    return f"SmartAPI refused the login: {message}{(' [' + code + ']') if code else ''}{hint}"
