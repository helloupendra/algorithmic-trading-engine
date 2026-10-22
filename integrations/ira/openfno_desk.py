"""OpenFNO desk tools for an IRA assistant: ask the desk's own assistant, read-only.

Load this file from an IRA instance's ira.yaml (``tools: [./openfno_desk.py]``) and put the desk's address and a
phone token in that instance's .env (chmod 600):

    OPENFNO_URL=https://www.openfno.com
    OPENFNO_PHONE_TOKEN=ira_...

The token is made on the desk under AI -> Assistant -> Phones. It opens only ``POST /api/Ira/ask``: the desk's
assistant answers from its own read-only tools, and anything it proposes (stop a run, close an account) still waits
for the owner's PIN on the desk. Nothing here can place, change or close a trade.
"""

from __future__ import annotations

import os
from typing import Annotated

import httpx
from pydantic import Field

from ira import ToolContext, ToolError, tool

_url = "https://www.openfno.com"
_token: str | None = None
TIMEOUT_SECONDS = 90.0


def setup(ctx: ToolContext) -> None:
    """Read the desk's address and token from the instance's environment (its .env)."""
    global _url, _token
    _url = (os.environ.get("OPENFNO_URL") or _url).rstrip("/")
    _token = os.environ.get("OPENFNO_PHONE_TOKEN") or None


@tool
def trading_desk(
    question: Annotated[
        str,
        Field(
            min_length=2,
            max_length=1000,
            description="The question for the trading desk, in the owner's own words (Hindi, English or Hinglish).",
        ),
    ],
) -> dict:
    """Ask the owner's trading desk (OpenFNO) about itself: today's P&L by account and run, live strategy runs,
    open positions, incidents, the data feeds, the AI Trader, strategies and their results. The desk answers from
    its own records. It cannot place, change or close anything from here: anything it proposes still needs the
    owner's PIN on the desk."""
    if not _token:
        raise ToolError("the trading desk is not connected: set OPENFNO_PHONE_TOKEN in this instance's .env")
    try:
        response = httpx.post(
            f"{_url}/api/Ira/ask",
            json={"text": question},
            headers={"Authorization": f"Bearer {_token}"},
            timeout=TIMEOUT_SECONDS,
        )
    except httpx.HTTPError as exc:
        raise ToolError(f"the trading desk did not answer ({type(exc).__name__})") from None
    try:
        body = response.json()
    except ValueError:
        body = {}
    if response.status_code != 200:
        reason = body.get("error") or body.get("spoken") or f"HTTP {response.status_code}"
        raise ToolError(f"the trading desk refused: {reason}")
    return {"answer": body.get("text") or "", "short": body.get("spoken") or "", "desk_call": body.get("callId")}
