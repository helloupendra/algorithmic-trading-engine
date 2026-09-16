"""
market_data/angel/totp.py

The 6-digit code SmartAPI's login asks for.

Angel One's login is client code + PIN + a time-based one-time password, so an
unattended session needs the TOTP secret (the base32 string shown when 2FA is
set up) and a clock. This is RFC 6238 in twenty lines rather than a new
dependency, and being pure it is tested against the RFC's own vectors.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import struct
import time


def totp_now(secret: str, at: float | None = None, step: int = 30, digits: int = 6,
             digest=hashlib.sha1) -> str:
    """The code for `at` (default: now) from a base32 secret, spaces and case ignored."""
    key = base64.b32decode(_normalise(secret), casefold=True)
    counter = int((time.time() if at is None else at) // step)
    mac = hmac.new(key, struct.pack(">Q", counter), digest).digest()
    offset = mac[-1] & 0x0F
    code = struct.unpack(">I", mac[offset:offset + 4])[0] & 0x7FFFFFFF
    return str(code % (10 ** digits)).zfill(digits)


def _normalise(secret: str) -> str:
    text = (secret or "").strip().replace(" ", "").upper()
    if not text:
        raise ValueError("the TOTP secret is empty; set ANGEL_TOTP_SECRET")
    # Base32 decodes in 8-character blocks; Angel shows the secret unpadded.
    return text + "=" * (-len(text) % 8)
