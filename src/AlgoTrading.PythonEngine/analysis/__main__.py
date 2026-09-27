"""
Run the Analysis module (from src/AlgoTrading.PythonEngine, as the API does).

    python -m analysis backtest [--dry-run] [--models K,..] [--underlyings N,..] [--report-dir DIR]
        Walk every model forward over the stored history, write
        logs/analysis/backtest-<date>.md and register each model version with
        its backtest (POST /api/Forecasts/models). --dry-run writes the report
        and registers nothing; so does any run narrowed by --models or
        --underlyings, because the sessions a model is scored on depend on
        which models run together.

    python -m analysis issue --session YYYY-MM-DD [--dry-run] [--models ..] [--underlyings ..]
        Forecast that session (before 09:15 IST) and POST each forecast.
        --dry-run prints the payloads instead.

    python -m analysis score [--from YYYY-MM-DD] [--to YYYY-MM-DD] [--dry-run]
        Score every unscored forecast in the window (default: the last 30
        days) whose session has closed. --dry-run prints the scores instead.

Exit status 0 when everything asked for was done or refused by the API as
already done / too late (409); 1 when anything failed; 2 for bad arguments.
Reads the repo-root .env: POSTGRES_* (read-only), API_BASE_URL and
ENGINE_SERVICE_USERNAME / ENGINE_SERVICE_PASSWORD.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
from datetime import date, datetime, timedelta
from pathlib import Path
from typing import List, Optional, Sequence

from backtest.timeutil import IST

log = logging.getLogger("analysis")

ALL_MODELS = ("range.har", "range.har-vix", "trend.logit", "direction.logit")
ALL_UNDERLYINGS = ("NIFTY", "BANKNIFTY", "SENSEX")
SCORE_LOOKBACK_DAYS = 30


def now_ist() -> datetime:
    return datetime.now(IST)


def _day(text: str) -> date:
    try:
        return date.fromisoformat(text)
    except ValueError:
        raise argparse.ArgumentTypeError(f"{text!r} is not a date (YYYY-MM-DD)") from None


def _choices(allowed: Sequence[str]):
    def parse(text: str) -> List[str]:
        picked = [p.strip() for p in text.split(",") if p.strip()]
        unknown = [p for p in picked if p not in allowed]
        if unknown or not picked:
            raise argparse.ArgumentTypeError(f"unknown {', '.join(unknown) or 'empty list'}; choose from "
                                             f"{', '.join(allowed)}")
        return [a for a in allowed if a in picked]
    return parse


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="python -m analysis", description="Forecasts with proof.")
    sub = p.add_subparsers(dest="command", required=True)

    b = sub.add_parser("backtest", help="walk-forward history; register the model versions")
    b.add_argument("--dry-run", action="store_true", help="write the report, register nothing")
    b.add_argument("--models", type=_choices(ALL_MODELS), default=list(ALL_MODELS))
    b.add_argument("--underlyings", type=_choices(ALL_UNDERLYINGS), default=list(ALL_UNDERLYINGS))
    b.add_argument("--report-dir", type=Path, default=None, help="default: <repo>/logs/analysis")

    i = sub.add_parser("issue", help="forecast a session before it opens")
    i.add_argument("--session", type=_day, required=True, help="the IST session date, YYYY-MM-DD")
    i.add_argument("--dry-run", action="store_true", help="print the payloads, send nothing")
    i.add_argument("--models", type=_choices(ALL_MODELS), default=list(ALL_MODELS))
    i.add_argument("--underlyings", type=_choices(ALL_UNDERLYINGS), default=list(ALL_UNDERLYINGS))

    s = sub.add_parser("score", help="score forecasts whose session has closed")
    s.add_argument("--from", dest="from_day", type=_day, default=None)
    s.add_argument("--to", dest="to_day", type=_day, default=None)
    s.add_argument("--dry-run", action="store_true", help="print the scores, send nothing")
    return p


def last_closed_day(now: datetime) -> date:
    """Today once its session has closed, else yesterday: a half-built day is never history."""
    from analysis.score import session_closed

    today = now.date()
    return today if session_closed(today, now) else today - timedelta(days=1)


def _api():
    from analysis.api import ForecastApi

    return ForecastApi()


def _connect():
    from analysis import data

    return data.connect()


# ------------------------------------------------------------------ commands --

def cmd_backtest(args) -> int:
    from analysis import backtest, data
    from analysis.models import MODELS
    import core.config

    now = now_ist()
    conn = _connect()
    try:
        market = data.load_market(conn, args.underlyings, None, last_closed_day(now))
    finally:
        conn.close()
    if market.vix.last is None:
        log.warning("no India VIX sessions stored: range.har-vix and the logits cannot forecast, so no model "
                    "has a common session to be scored on")
    for name, series in market.series.items():
        if not series.sessions:
            log.error("%s: no complete sessions in candles; nothing to backtest", name)
            return 1
        if market.vix.last is not None and market.vix.last < series.last:
            log.warning("India VIX ends %s, %s %s: the VIX models cannot forecast the sessions between",
                        market.vix.last, name, series.last)

    records = backtest.run(market, args.models, args.underlyings)
    summaries = {k: backtest.summarize(MODELS[k], records[k]) for k in args.models}
    geometric = {k: backtest.geometric_baseline_check(records[k], market)
                 for k in args.models if MODELS[k].target == "range"}
    text = backtest.report(summaries, market, now, geometric)
    out_dir = args.report_dir or (core.config.REPO_ROOT / "logs" / "analysis")
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"backtest-{now.date().isoformat()}.md"
    path.write_text(text, encoding="utf-8")
    log.info("report written to %s", path)

    for key, summary in summaries.items():
        log.info("%s: %s", key, "; ".join(
            f"{split} n={(summary[split] or {}).get('n', 0)} skill={(summary[split] or {}).get('skill')}"
            for split in backtest.SPLITS))

    full = list(args.models) == list(ALL_MODELS) and list(args.underlyings) == list(ALL_UNDERLYINGS)
    if args.dry_run or not full:
        log.info("nothing registered (%s)", "dry run" if args.dry_run else "narrowed run")
        return 0
    if any(summaries[k]["design"] is None for k in args.models):
        log.error("a model has no design-period forecasts; nothing registered")
        return 1

    api = _api()
    failed = 0
    for key in args.models:
        answer = api.register_model(backtest.registration(MODELS[key], summaries[key]))
        if answer.ok:
            log.info("registered %s", key)
        else:
            failed += 1
            log.error("registering %s: HTTP %s %s", key, answer.status, answer.message)
    return 1 if failed else 0


def cmd_issue(args) -> int:
    from analysis import data
    from analysis.issue import describe, issue_session

    session = args.session
    now = now_ist()
    if now.date() > session or (now.date() == session and now.time() >= data.SESSION_OPEN):
        log.warning("%s has already opened; the API will refuse these forecasts (409)", session)
    conn = _connect()
    try:
        market = data.load_market(conn, args.underlyings, None, session - timedelta(days=1))
    finally:
        conn.close()
    post = None if args.dry_run else _api().issue
    result = issue_session(session, market, args.models, args.underlyings, post)
    describe(result, session, args.dry_run)
    return result.exit_code


def cmd_score(args) -> int:
    from analysis import data
    from analysis.score import score_forecasts, unscored

    now = now_ist()
    to_day = args.to_day or now.date()
    from_day = args.from_day or (now.date() - timedelta(days=SCORE_LOOKBACK_DAYS))
    api = _api()
    pending = unscored(api.forecasts(from_day, to_day), now)
    if not pending:
        log.info("nothing to score between %s and %s", from_day, to_day)
        return 0
    names = sorted({str(f["underlying"]) for f in pending})
    unknown = [n for n in names if n not in data.BY_NAME]
    days = [date.fromisoformat(str(f["sessionDate"])[:10]) for f in pending]
    conn = _connect()
    try:
        # Two weeks before the first session is enough for its previous close.
        market = data.load_market(conn, [n for n in names if n in data.BY_NAME], min(days) - timedelta(days=15),
                                  last_closed_day(now), with_vix=False)
    finally:
        conn.close()
    post = None if args.dry_run else api.post_outcome
    result = score_forecasts([f for f in pending if f["underlying"] not in unknown], market, now, post)
    for name in unknown:
        result.errors.append(f"forecasts for {name}: no index symbol known to score them with")
    for payload in result.payloads if args.dry_run else []:
        print(json.dumps(payload))
    for line in result.waiting:
        log.info("waiting: %s", line)
    for line in result.errors:
        log.error("%s", line)
    log.info("%d scored%s, %d refused (409), %d waiting for bars, %d errors", result.scored,
             " (dry run, nothing sent)" if args.dry_run else "", result.refused, len(result.waiting),
             len(result.errors))
    return result.exit_code


COMMANDS = {"backtest": cmd_backtest, "issue": cmd_issue, "score": cmd_score}


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parser().parse_args(argv)
    logging.basicConfig(level=logging.INFO, stream=sys.stdout,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    from analysis.api import ApiError
    from analysis.data import DataError

    try:
        return COMMANDS[args.command](args)
    except (DataError, ApiError) as ex:
        log.error("%s", ex)
        return 1


if __name__ == "__main__":
    sys.exit(main())
