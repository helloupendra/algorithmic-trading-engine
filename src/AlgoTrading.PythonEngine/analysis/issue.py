"""
analysis/issue.py

The morning's forecasts: for each underlying and model, fit on every session
before today and forecast today, then POST it before 09:15 IST. The API
stamps the time and refuses (409) once the session has opened or when the
same forecast exists — both logged, neither an error.

A forecast is only as fresh as its inputs. If the last complete session is not
the previous trading day (the archive did not run, the live bars have a hole),
nothing is issued for that underlying and the run fails loudly: a forecast
built on the day before yesterday, stored as today's, would be a stale input
recorded as a fact. The same holds for India VIX and the models that read it,
and for version 2's context: a v2 model whose inputs are missing this morning
(a global series stopped arriving, the FII file was not fetched) is not issued,
and the run says which input and which table.

Every forecast, v1's included, also carries `inputs.liveOnly`: the GIFT Nifty
gap, the news since the previous close and the earnings load as known at
issue time. No model reads them; they are recorded so that, once there is
enough of them, whether they would have helped can be tested on forecasts
written before the open.
"""

from __future__ import annotations

import json
import logging
import math
from dataclasses import dataclass, field
from datetime import date
from typing import Any, Callable, Dict, List, Optional, Sequence

from analysis.api import Answer
from analysis.context import Context
from analysis.data import BY_NAME, MarketData, is_trading_day, previous_trading_day
from analysis.models import SPECS, ForecastUnavailable, build_table, forecast, forecast_payload

log = logging.getLogger("analysis.issue")


@dataclass
class IssueResult:
    issued: int = 0
    refused: int = 0
    skipped: List[str] = field(default_factory=list)
    errors: List[str] = field(default_factory=list)
    payloads: List[dict] = field(default_factory=list)

    @property
    def exit_code(self) -> int:
        return 1 if self.errors else 0


def issue_session(session: date, market: MarketData, model_keys: Sequence[str], underlyings: Sequence[str],
                  post: Optional[Callable[[dict], Answer]], context: Optional[Context] = None,
                  live_only: Optional[Dict[str, Any]] = None) -> IssueResult:
    """
    Forecast `session` for every underlying and model. `post` sends one
    payload to the API; None is a dry run (the payloads are collected, not
    sent). `context` is version 2's inputs (needed only by v2 models);
    `live_only` is recorded on every payload's inputs.
    """
    result = IssueResult()
    needs_context = any(SPECS[k].extras for k in model_keys)
    for name in underlyings:
        underlying = BY_NAME[name]
        if not is_trading_day(underlying.exchange, session, market.holidays):
            result.skipped.append(f"{name}: {session} is not a trading day on {underlying.exchange}")
            continue

        series = market.series[name].before(session)
        expected = previous_trading_day(underlying.exchange, session, market.holidays)
        if series.last != expected:
            why = market.series[name].dropped_for(expected) or "no bars stored"
            result.errors.append(f"{name}: the last complete session is {series.last}, but {session} follows "
                                 f"{expected} ({why}); nothing issued for {name}")
            continue

        vix = market.vix.before(session)
        t = build_table(name, series.sessions, vix.closes(), market.is_expiry(name), pending=session,
                        context=context if needs_context else None)
        i = len(t.days) - 1
        for key in model_keys:
            spec = SPECS[key]
            if spec.extras and context is None:
                result.errors.append(f"{key} {name}: the version 2 inputs were not loaded; not issued")
                continue
            if spec.uses_vix and not math.isfinite(t.vix_prev[i]):
                why = market.vix.dropped_for(expected) or "no bars stored"
                result.errors.append(f"{key} {name}: India VIX has no complete session for {expected} ({why}; "
                                     f"its last is {vix.last}); not issued")
                continue
            try:
                fc = forecast(spec, t, i, fit_before=session)
            except ForecastUnavailable as ex:
                result.errors.append(f"{ex}; not issued")
                continue
            payload = forecast_payload(spec, name, session, fc)
            if live_only is not None:
                payload["inputs"]["liveOnly"] = live_only
            result.payloads.append(payload)
            if post is None:
                result.issued += 1
                continue
            answer = post(payload)
            if answer.ok:
                result.issued += 1
                body = answer.body if isinstance(answer.body, dict) else {}
                log.info("issued %s %s %s: id %s at %s", key, name, session, body.get("id"), body.get("issuedUtc"))
            elif answer.conflict:
                result.refused += 1
                log.info("refused %s %s %s (409): %s", key, name, session, answer.message)
            else:
                result.errors.append(f"{key} {name} {session}: HTTP {answer.status} {answer.message}")
    return result


def describe(result: IssueResult, session: date, dry_run: bool) -> None:
    for payload in result.payloads if dry_run else []:
        print(json.dumps(payload, sort_keys=False))
    for line in result.skipped:
        log.info("skipped %s", line)
    for line in result.errors:
        log.error("%s", line)
    verb = "built (dry run, nothing sent)" if dry_run else "issued"
    log.info("%s: %d forecasts %s, %d refused as already issued or too late, %d errors",
             session, result.issued, verb, result.refused, len(result.errors))
