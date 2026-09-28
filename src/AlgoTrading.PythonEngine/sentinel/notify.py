"""
Telling a person.

Straight to Telegram, not through the platform's alert path: that path runs
through the API, and "the API is down" is one of the things this has to say.
Nothing secret goes into a message — agents put evidence in, and evidence is
redacted here before it leaves the machine.
"""
from __future__ import annotations

import logging
import re
import time
from abc import ABC, abstractmethod
from datetime import datetime
from typing import Optional

import requests

from sentinel.clock import to_ist
from sentinel.model import CONTEXT_PREFIX, FLAP_PREFIX, FLAP_WINDOW, HISTORY_PREFIX, Finding, Severity

log = logging.getLogger("sentinel.notify")

# Of an incident's context pack (sentinel/pack.py) a message carries the first
# few lines — the deploy, the commit, the runs, the nearest log lines; the
# console has all of it.
CONTEXT_IN_MESSAGE = 5

# Anything that looks like a credential is masked before a message is sent.
# A masked harmless string costs nothing; a leaked token in a chat history
# costs a rotation. But a mask that eats this desk's own prose is a failure
# too: its incidents say "token expired", "password guessing", "credential
# (token expired or invalid)" all day, so a key counts only when ':' or '='
# follows it on the same line.
#
# ONE spec, identical in every layer that sends or shows incident text:
# this redactor (Telegram), IncidentRedaction in IncidentsController.cs (the
# API), maskSecrets in web/src/lib/incidents.ts (the console), and the logs
# agent's filter for lines that may hold a secret. Change one, change all;
# each has a table-driven test with the same cases.
#
# 1. Authorization: Bearer|Basic <value>  -> the value.
# 2. Bearer <12+ token characters> anywhere (a header quoted without its name).
# 3. scheme://user:password@ and scheme://:password@  -> the password.
# 4. key=value / key: value / "key": "value", where the key is an identifier
#    ([A-Za-z0-9_], or api-key/private-key) with a whole part (underscore
#    separated, or the end of a camelCase key) that is secret, password,
#    passwd, pwd, token, api_key, private_key, totp or pin: DHAN_PIN,
#    trading_pin, JWT_SECRET_KEY, TELEGRAM_BOT_TOKEN, access_token,
#    accessToken, X-Api-Key. Not "tokens", "spinning" or "Skipping". The
#    separator is an optional quote, spaces or tabs (never a newline), then
#    ':' or '=' (not '==', which is code). The value runs to whitespace, a
#    quote, '&', ',' or ';' — or, quoted, to its closing quote.
# 5. Telegram bot tokens anywhere, /bot<token>/ in a URL included.
# 6. JWTs: eyJ….eyJ….<signature>.
#
# Lengths are bounded so a long line cannot make a pattern backtrack for
# seconds (the API gives its regexes 250 ms, and a round must not stall).
HIDDEN = "…"
_AUTH_HEADER = re.compile(r"(?i)(authorization[\"']?[ \t]*[:=][ \t]*[\"']?(?:bearer|basic)[ \t]+)[^\s\"'&,;]+")
_BEARER = re.compile(r"(?i)(\bbearer[ \t]+)[A-Za-z0-9._~+/=-]{12,}")
_URL_PASSWORD = re.compile(r"(?i)\b([a-z][a-z0-9+.-]*://[^\s:/@]*:)[^\s@/]+(?=@)")
_KEY_VALUE = re.compile(
    r"(?i)(?<![A-Za-z0-9_])([A-Za-z0-9_]{0,64}?"
    r"(?:secret|password|passwd|pwd|token|api[_-]?key|private[_-]?key|totp|pin)"
    r"(?:_[A-Za-z0-9]{1,32}){0,8}[\"']?[ \t]*[:=](?!=)[ \t]*)"
    r"(\"[^\"\r\n]+\"|'[^'\r\n]+'|[\"']?[^\s\"'&,;]+)")
_TELEGRAM_BOT_TOKEN = re.compile(r"(?<![0-9])[0-9]{8,10}:[A-Za-z0-9_-]{30,}")
_JWT = re.compile(r"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")

#: Text longer than this is cut before it is redacted: a message carries 3,900
#: characters, and no pattern should be asked to read a megabyte of traceback.
REDACT_MAX_CHARS = 20_000


def _mask_value(m: re.Match) -> str:
    """The key and separator kept, the value hidden; a quoted value keeps its quotes."""
    value = m.group(2)
    lead = value[0] if value[0] in "\"'" else ""
    tail = lead if lead and len(value) > 1 and value.endswith(lead) else ""
    return m.group(1) + lead + HIDDEN + tail


def redact(text: str) -> str:
    text = text[:REDACT_MAX_CHARS]
    text = _AUTH_HEADER.sub(lambda m: m.group(1) + HIDDEN, text)
    text = _BEARER.sub(lambda m: m.group(1) + HIDDEN, text)
    text = _URL_PASSWORD.sub(lambda m: m.group(1) + HIDDEN, text)
    text = _KEY_VALUE.sub(_mask_value, text)
    text = _JWT.sub(HIDDEN, text)
    return _TELEGRAM_BOT_TOKEN.sub(HIDDEN, text)


#: "Last time: …" carries at most this much of what a person wrote; the console has the rest.
LAST_TIME_CHARS = 300


def format_seen_before(count: int, last_seen_utc: Optional[datetime], resolution: str = "") -> list[str]:
    """
    The evidence lines saying a problem has happened before: how often, when
    last, and what was done then. None when it has not. A resolution written
    over several lines becomes one.
    """
    if count <= 0:
        return []
    times = "once" if count == 1 else f"{count} times"
    when = f", last on {to_ist(last_seen_utc).strftime('%d %b %Y, %H:%M')} IST" if last_seen_utc else ""
    lines = [f"{HISTORY_PREFIX}Seen before: {times}{when}"]
    done = " ".join(resolution.split())
    if done:
        if len(done) > LAST_TIME_CHARS:
            done = done[:LAST_TIME_CHARS - 1].rstrip() + "…"
        lines.append(f"{HISTORY_PREFIX}Last time: {done}")
    return lines


def _ordinal(n: int) -> str:
    suffix = "th" if 10 <= n % 100 <= 20 else {1: "st", 2: "nd", 3: "rd"}.get(n % 10, "th")
    return f"{n}{suffix}"


def _clock(moment: datetime, now: datetime) -> str:
    """HH:MM IST, with the day when it is not today's."""
    when = to_ist(moment)
    return when.strftime("%H:%M") if when.date() == to_ist(now).date() else when.strftime("%d %b %H:%M").lstrip("0")


def _window_minutes() -> int:
    return int(FLAP_WINDOW.total_seconds() // 60)


def format_again(episodes: int, since_utc: datetime, now_utc: datetime) -> str:
    """
    The line under a reopened incident's header: which return this is, and
    what to expect from here — the owner's "stopped again (5th time since 13:06)".
    """
    minutes = _window_minutes()
    return (f"Back again: the {_ordinal(episodes)} time since {_clock(since_utc, now_utc)} IST. While it keeps "
            f"clearing and coming back it stays this one incident: at most one message about it every {minutes} "
            f"min, and ✅ RESOLVED once it has stayed clear for {minutes} min.")


def format_flapping(episodes: int, since_utc: datetime, now_utc: datetime) -> str:
    """The evidence line a flapping incident carries in the console, rewritten on every return."""
    return (f"{FLAP_PREFIX}{_ordinal(episodes)} episode since {_clock(since_utc, now_utc)} IST: it cleared and came "
            f"back within {_window_minutes()} min each time, so it stays this one incident")


def format_settled(episodes: int, since_utc: Optional[datetime], cleared_utc: datetime, now_utc: datetime) -> str:
    """The line under a flapping incident's held-back RESOLVED: how often, and clear since when."""
    since = f" since {_clock(since_utc, now_utc)} IST" if since_utc else ""
    return (f"It happened {episodes} times{since}; clear since {_clock(cleared_utc, now_utc)} IST "
            f"({_window_minutes()} min without coming back).")


def format_opened(finding: Finding, incident_id: int, escalated: bool = False,
                  context: Optional[list[str]] = None, history: Optional[list[str]] = None,
                  again: Optional[str] = None) -> str:
    """
    The message for an incident that opened, escalated or came back.
    ``incident_id`` 0: the database did not take it. ``history``: the
    seen-before lines (:func:`format_seen_before`), right under the summary —
    whether this has happened before, and what fixed it, is the first thing to
    know. ``again``: a reopened incident's line (:func:`format_again`), first.
    """
    head = "ESCALATED" if escalated else "AGAIN" if again else "NEW"
    number = f"#{incident_id}" if incident_id else "not stored (the database did not take it)"
    lines = [
        f"{finding.severity.icon} {head} [{finding.severity.value.upper()}] {finding.title}",
        f"{number} · {finding.agent}/{finding.rule}" + (f" · {finding.where}" if finding.where else ""),
        "",
    ]
    if again:
        lines += [again, ""]
    lines.append(finding.summary)
    if history:
        lines += [""] + [h.removeprefix(HISTORY_PREFIX) for h in history]
    if finding.evidence:
        lines += ["", "Evidence:"] + [f"• {e}" for e in finding.evidence[:6]]
        if len(finding.evidence) > 6:
            lines.append(f"• … {len(finding.evidence) - 6} more in the console")
    if context:
        lines += ["", "Around then:"] + [f"• {c.removeprefix(CONTEXT_PREFIX)}" for c in context[:CONTEXT_IN_MESSAGE]]
        if len(context) > CONTEXT_IN_MESSAGE:
            lines.append(f"• … {len(context) - CONTEXT_IN_MESSAGE} more in the console")
    if finding.suggestion:
        lines += ["", f"Likely fix: {finding.suggestion}"]
    return redact("\n".join(lines))[:MESSAGE_CHARS]


def format_resolved(title: str, incident_id: int, severity: Severity, note: str = "") -> str:
    number = f"#{incident_id}" if incident_id else "(never stored)"
    text = f"✅ RESOLVED {number} [{severity.value.upper()}] {title}" + (f"\n{note}" if note else "")
    return redact(text)[:MESSAGE_CHARS]


#: What a message may carry: Telegram takes 4,096 characters, and a message
#: delivered late gets one more line (see the engine).
MESSAGE_CHARS = 3900


class Notifier(ABC):
    """
    Sends one message; True when it was delivered. A send that returns False
    leaves the message with the engine, which tries again later — so a send
    must not wait long, and never sleeps through a service's "try again in N
    seconds": it says so in ``retry_after`` and returns.
    """

    #: After a failed send: how long the service asked to be left alone, in seconds, or None.
    retry_after: Optional[float] = None
    #: After a failed send: True when the service refused this text as such (it cannot succeed on a retry).
    refused: bool = False

    @abstractmethod
    def send(self, text: str) -> bool: ...


class LogNotifier(Notifier):
    """``--dry-run``: the message goes to the log, nowhere else."""

    def send(self, text: str) -> bool:
        log.info("NOTIFY\n%s", text)
        return True


class TelegramNotifier(Notifier):
    """
    The bot the desk already uses (TELEGRAM_BOT_TOKEN / TELEGRAM_CHAT_ID).

    Telegram allows about one message a second to a chat; a burst — the feed
    dying opens several incidents at once — is spaced rather than dropped. A
    429 is not slept through: its retry_after goes back to the engine, which
    keeps the message and tries again after it, while the checks go on.
    """

    def __init__(self, token: str, chat_id: str, timeout: float = 10.0) -> None:
        self._url = f"https://api.telegram.org/bot{token}/sendMessage"
        self._chat_id = chat_id
        self._timeout = timeout
        self._last_sent = 0.0

    def send(self, text: str) -> bool:
        self.retry_after = None
        self.refused = False
        wait = 1.1 - (time.monotonic() - self._last_sent)
        if wait > 0:
            time.sleep(wait)
        # A lone surrogate from a log line decoded leniently is not UTF-8, and
        # Telegram refuses the whole message for it.
        text = text.encode("utf-8", "replace").decode("utf-8")
        try:
            response = requests.post(
                self._url,
                json={"chat_id": self._chat_id, "text": text, "disable_web_page_preview": True},
                timeout=self._timeout,
            )
        except requests.RequestException as exc:
            log.warning("Telegram unreachable: %s", type(exc).__name__)
            return False
        finally:
            self._last_sent = time.monotonic()
        if response.ok:
            return True
        body = _json_or_empty(response)
        if response.status_code == 429:
            retry = body.get("parameters", {}).get("retry_after") if isinstance(body.get("parameters"), dict) else None
            self.retry_after = float(retry) if isinstance(retry, (int, float)) and retry > 0 else 5.0
        elif response.status_code == 400:
            # "Bad Request": this text, or the chat id. Sending the same again cannot work.
            self.refused = True
        # The body can echo the request; never log the URL, it holds the token.
        description = body.get("description") if isinstance(body.get("description"), str) else ""
        log.warning("Telegram refused a message: HTTP %s %s", response.status_code, redact(description)[:120])
        return False


def _json_or_empty(response) -> dict:
    """A reply's JSON body, or {} — a proxy's HTML error page is not a reason to crash the round."""
    try:
        body = response.json()
    except ValueError:
        return {}
    return body if isinstance(body, dict) else {}


def notifier_from_env(env: dict[str, str], dry_run: bool) -> Notifier:
    token: Optional[str] = env.get("TELEGRAM_BOT_TOKEN") or None
    # Incidents are the desk's own business: the system channel when one is
    # set (TELEGRAM_SYSTEM_CHAT_ID), else the one chat the desk has.
    chat: Optional[str] = env.get("TELEGRAM_SYSTEM_CHAT_ID") or env.get("TELEGRAM_CHAT_ID") or None
    if dry_run or not token or not chat:
        if not dry_run:
            log.warning("TELEGRAM_BOT_TOKEN / TELEGRAM_CHAT_ID not set — incidents will only be logged and stored.")
        return LogNotifier()
    return TelegramNotifier(token, chat)
