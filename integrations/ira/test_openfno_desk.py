"""Run with an IRA virtualenv: ``python -m pytest integrations/ira``."""

from __future__ import annotations

import httpx
import pytest

import openfno_desk as desk
from ira import ToolError


def _desk_answers(status: int, body: dict, seen: list):
    def post(url, json, headers, timeout):
        seen.append((url, json, headers))
        return httpx.Response(status, json=body)

    return post


@pytest.fixture(autouse=True)
def configured(monkeypatch):
    monkeypatch.setenv("OPENFNO_URL", "https://desk.example/")
    monkeypatch.setenv("OPENFNO_PHONE_TOKEN", "ira_test")
    desk.setup(None)


def test_the_question_goes_to_the_phone_endpoint_and_both_answers_come_back(monkeypatch):
    seen: list = []
    monkeypatch.setattr(desk.httpx, "post", _desk_answers(200, {"text": "admin +₹1,200", "spoken": "Up 1,200.", "callId": 7}, seen))

    result = desk.trading_desk("aaj ka P&L?")

    assert result == {"answer": "admin +₹1,200", "short": "Up 1,200.", "desk_call": 7}
    assert seen == [("https://desk.example/api/Ira/ask", {"text": "aaj ka P&L?"}, {"Authorization": "Bearer ira_test"})]


def test_a_refusal_is_a_tool_error_with_the_desks_reason(monkeypatch):
    monkeypatch.setattr(desk.httpx, "post", _desk_answers(401, {"error": "The phone token is not valid."}, []))
    with pytest.raises(ToolError, match="not valid"):
        desk.trading_desk("aaj ka P&L?")


def test_a_desk_that_does_not_answer_is_a_tool_error_not_a_crash(monkeypatch):
    def down(*args, **kwargs):
        raise httpx.ConnectTimeout("no route")

    monkeypatch.setattr(desk.httpx, "post", down)
    with pytest.raises(ToolError, match="did not answer"):
        desk.trading_desk("aaj ka P&L?")


def test_without_a_token_it_says_how_to_connect(monkeypatch):
    monkeypatch.delenv("OPENFNO_PHONE_TOKEN")
    desk.setup(None)
    with pytest.raises(ToolError, match="OPENFNO_PHONE_TOKEN"):
        desk.trading_desk("aaj ka P&L?")
