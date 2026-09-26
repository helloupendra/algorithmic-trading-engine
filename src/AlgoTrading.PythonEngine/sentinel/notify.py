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
from typing import Optional

import requests

from sentinel.model import CONTEXT_PREFIX, Finding, Severity

log = logging.getLogger("sentinel.notify")

# Of an incident's context pack (sentinel/pack.py) a message carries the first
# few lines — the deploy, the commit, the runs, the nearest log lines; the
# console has all of it.
CONTEXT_IN_MESSAGE = 5

# Anything that looks like a credential is masked before a message is sent.
# Deliberately broad: a masked harmless string costs nothing, a leaked token
# in a chat history costs a rotation.
_SECRET_PATTERNS = [
    re.compile(r"(?i)(bearer\s+)[A-Za-z0-9._\-]{12,}"),
    re.compile(r"(?i)((?:password|passwd|secret|token|api[_-]?key|access[_-]?token|app[_-]?secret|totp)"
               r"[\"'\s:=]+)[^\s\"',}]{4,}"),
    re.compile(r"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}"),  # JWTs
    re.compile(r"\b[0-9]{8,10}:[A-Za-z0-9_\-]{30,}\b"),  # Telegram bot tokens
]


def redact(text: str) -> str:
    for pattern in _SECRET_PATTERNS:
        text = pattern.sub(lambda m: (m.group(1) if m.groups() else "") + "…", text)
    return text


def format_opened(finding: Finding, incident_id: int, escalated: bool = False,
                  context: Optional[list[str]] = None) -> str:
    head = "ESCALATED" if escalated else "NEW"
    lines = [
        f"{finding.severity.icon} {head} [{finding.severity.value.upper()}] {finding.title}",
        f"#{incident_id} · {finding.agent}/{finding.rule}" + (f" · {finding.where}" if finding.where else ""),
        "",
        finding.summary,
    ]
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
    return redact("\n".join(lines))[:3900]


def format_resolved(title: str, incident_id: int, severity: Severity) -> str:
    return redact(f"✅ RESOLVED #{incident_id} [{severity.value.upper()}] {title}")[:3900]


class Notifier(ABC):
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
    dying opens several incidents at once — is spaced rather than dropped.
    """

    def __init__(self, token: str, chat_id: str, timeout: float = 10.0) -> None:
        self._url = f"https://api.telegram.org/bot{token}/sendMessage"
        self._chat_id = chat_id
        self._timeout = timeout
        self._last_sent = 0.0

    def send(self, text: str) -> bool:
        wait = 1.1 - (time.monotonic() - self._last_sent)
        if wait > 0:
            time.sleep(wait)
        try:
            response = requests.post(
                self._url,
                json={"chat_id": self._chat_id, "text": text, "disable_web_page_preview": True},
                timeout=self._timeout,
            )
            self._last_sent = time.monotonic()
            if response.status_code == 429:
                retry = response.json().get("parameters", {}).get("retry_after", 5)
                time.sleep(min(float(retry), 30.0))
                response = requests.post(self._url, json={"chat_id": self._chat_id, "text": text},
                                         timeout=self._timeout)
            if not response.ok:
                # The body can echo the request; never log the URL, it holds the token.
                log.warning("Telegram refused a message: HTTP %s", response.status_code)
            return response.ok
        except requests.RequestException as exc:
            log.warning("Telegram unreachable: %s", type(exc).__name__)
            return False


def notifier_from_env(env: dict[str, str], dry_run: bool) -> Notifier:
    token: Optional[str] = env.get("TELEGRAM_BOT_TOKEN") or None
    chat: Optional[str] = env.get("TELEGRAM_CHAT_ID") or None
    if dry_run or not token or not chat:
        if not dry_run:
            log.warning("TELEGRAM_BOT_TOKEN / TELEGRAM_CHAT_ID not set — incidents will only be logged and stored.")
        return LogNotifier()
    return TelegramNotifier(token, chat)
