"""
Run the Analysis module (from src/AlgoTrading.PythonEngine, as the API does).

    python -m analysis backtest [--dry-run] [--models K,..] [--underlyings N,..] [--report-dir DIR]
        Walk every model forward over the stored history, write
        logs/analysis/backtest-<date>.md and register each model version with
        its backtest (POST /api/Forecasts/models). --dry-run writes the report
        and registers nothing; so does any run narrowed by --models or
        --underlyings, because the sessions a model is scored on depend on
        which models run together.

    python -m analysis backtest-v2 [--dry-run] [--report-dir DIR]
        Walk version 1 and version 2 forward together on the same sessions,
        write logs/analysis/backtest-v2-<date>.md, and register each v2 model
        that beats its v1 model on validation with a 95% interval above zero —
        and no other. --dry-run writes the report and registers nothing. Needs
        the MarketIntelligence tables and their backfill.

    python -m analysis issue --session YYYY-MM-DD [--dry-run] [--models ..] [--underlyings ..]
        Forecast that session (before 09:15 IST) and POST each forecast: the
        v1 models, and the v2 models the API has registered (asked with
        GET /api/Forecasts/models). --dry-run prints the payloads instead, for
        v1 unless --models names v2 models.

    python -m analysis score [--from YYYY-MM-DD] [--to YYYY-MM-DD] [--dry-run]
        Score every unscored forecast in the window (default: the last 30
        days) whose session has closed, of either version. --dry-run prints the
        scores instead.

    python -m analysis news-score [--limit N] [--dry-run]
        Score up to N unscored headlines and announcements, oldest first
        (FinBERT on the CPU plus rules; analysis/news.py), and write the six
        score columns. The only command that writes to the database.
        --dry-run prints the scores and writes nothing.

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
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import List, Optional, Sequence

from backtest.timeutil import IST

log = logging.getLogger("analysis")

ALL_MODELS = ("range.har", "range.har-vix", "trend.logit", "direction.logit")
V2_MODELS = ("range.har-vix-cues", "trend.logit-cues", "direction.logit-cues")
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

    b2 = sub.add_parser("backtest-v2", help="version 2 against version 1; register the v2 models that beat it")
    b2.add_argument("--dry-run", action="store_true", help="write the report, register nothing")
    b2.add_argument("--report-dir", type=Path, default=None, help="default: <repo>/logs/analysis")

    i = sub.add_parser("issue", help="forecast a session before it opens")
    i.add_argument("--session", type=_day, required=True, help="the IST session date, YYYY-MM-DD")
    i.add_argument("--dry-run", action="store_true", help="print the payloads, send nothing")
    i.add_argument("--models", type=_choices(ALL_MODELS + V2_MODELS), default=None,
                   help="default: v1 and the v2 models the API has registered")
    i.add_argument("--underlyings", type=_choices(ALL_UNDERLYINGS), default=list(ALL_UNDERLYINGS))

    s = sub.add_parser("score", help="score forecasts whose session has closed")
    s.add_argument("--from", dest="from_day", type=_day, default=None)
    s.add_argument("--to", dest="to_day", type=_day, default=None)
    s.add_argument("--dry-run", action="store_true", help="print the scores, send nothing")

    from analysis.news import DEFAULT_LIMIT, MAX_LIMIT

    n = sub.add_parser("news-score", help="score unscored headlines and announcements (FinBERT + rules)")
    n.add_argument("--limit", type=_limit(MAX_LIMIT), default=DEFAULT_LIMIT,
                   help=f"items this run, at most {MAX_LIMIT} (default {DEFAULT_LIMIT})")
    n.add_argument("--dry-run", action="store_true", help="print the scores, write nothing")
    return p


def _limit(cap: int):
    def parse(text: str) -> int:
        try:
            value = int(text)
        except ValueError:
            raise argparse.ArgumentTypeError(f"{text!r} is not a whole number") from None
        if not 1 <= value <= cap:
            # The cap is the cost guard: a backlog is worked off over runs, not in one that holds the CPU.
            raise argparse.ArgumentTypeError(f"--limit must be between 1 and {cap}")
        return value
    return parse


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


def cmd_backtest_v2(args) -> int:
    from analysis import backtest, data
    from analysis import context as ctxmod
    from analysis.models import MODELS_V2, SPECS
    import core.config

    now = now_ist()
    end = last_closed_day(now)
    conn = _connect()
    try:
        market = data.load_market(conn, ALL_UNDERLYINGS, None, end)
        context = ctxmod.load(conn, None, end, market.holidays)
    finally:
        conn.close()
    for name, series in market.series.items():
        if not series.sessions:
            log.error("%s: no complete sessions in candles; nothing to backtest", name)
            return 1

    keys = list(ALL_MODELS) + list(V2_MODELS)
    records = backtest.run(market, keys, ALL_UNDERLYINGS, context)
    summaries = {k: backtest.summarize(SPECS[k], records[k]) for k in keys}
    comparisons = {k: backtest.compare(records[MODELS_V2[k].compares_with], records[k]) for k in V2_MODELS}
    geometric = {k: backtest.geometric_baseline_check(records[k], market) for k in keys if SPECS[k].target == "range"}
    days = [s.day for s in market.series["NIFTY"].sessions]
    coverage = ctxmod.coverage(ctxmod.columns(days, context), days)
    text = backtest.report_v2(summaries, comparisons, market, coverage, len(days), now, geometric)
    out_dir = args.report_dir or (core.config.REPO_ROOT / "logs" / "analysis")
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"backtest-v2-{now.date().isoformat()}.md"
    path.write_text(text, encoding="utf-8")
    log.info("report written to %s", path)

    for key, comparison in comparisons.items():
        log.info("%s vs %s: %s", key, MODELS_V2[key].compares_with, "; ".join(
            f"{split} n={(comparison[split] or {}).get('n', 0)} skillVsV1={(comparison[split] or {}).get('skillVsV1')} "
            f"ci=[{(comparison[split] or {}).get('diffCiLow')}, {(comparison[split] or {}).get('diffCiHigh')}]"
            for split in backtest.SPLITS))
    # No validation forecasts means the context is not there yet, not that it adds nothing: say which.
    blind = [k for k in V2_MODELS if comparisons[k]["validation"] is None]
    if blind:
        log.error("%s forecast no validation session: the context tables do not cover 2025 yet (see the report's "
                  "Context inputs table); nothing registered", ", ".join(blind))
        return 1
    passing = [k for k in V2_MODELS if backtest.beats_v1(comparisons[k])]
    if not passing:
        log.info("version 2 beat version 1 on validation nowhere: nothing registered, version 1 stays")
        return 0
    if args.dry_run:
        log.info("dry run: would register %s", ", ".join(passing))
        return 0
    api = _api()
    failed = 0
    for key in passing:
        answer = api.register_model(backtest.registration_v2(MODELS_V2[key], summaries[key], comparisons[key]))
        if answer.ok:
            log.info("registered %s", key)
        else:
            failed += 1
            log.error("registering %s: HTTP %s %s", key, answer.status, answer.message)
    return 1 if failed else 0


def cmd_issue(args) -> int:
    from analysis import context as ctxmod
    from analysis import data
    from analysis.api import ApiError
    from analysis.issue import describe, issue_session

    session = args.session
    now = now_ist()
    if now.date() > session or (now.date() == session and now.time() >= data.SESSION_OPEN):
        log.warning("%s has already opened; the API will refuse these forecasts (409)", session)
    api = None if args.dry_run else _api()
    errors: List[str] = []
    keys = list(args.models) if args.models else list(ALL_MODELS)
    if not args.models and api is not None:
        try:
            keys += registered_v2(api.models())
        except ApiError as ex:     # v1 does not wait on v2's registry
            errors.append(f"version 2 not issued: {ex}")
    conn = _connect()
    try:
        market = data.load_market(conn, args.underlyings, None, session - timedelta(days=1))
        context = None
        if any(k in V2_MODELS for k in keys):
            try:
                context = ctxmod.load(conn, None, session - timedelta(days=1), market.holidays)
            except data.DataError as ex:
                errors.append(f"version 2 not issued: {ex}")
                keys = [k for k in keys if k not in V2_MODELS]
        live_only = _live_only(conn, session, market, now)
    finally:
        conn.close()
    post = None if api is None else api.issue
    result = issue_session(session, market, keys, args.underlyings, post, context, live_only)
    result.errors[:0] = errors
    describe(result, session, args.dry_run)
    return result.exit_code


def registered_v2(models: Sequence[dict]) -> List[str]:
    """The v2 models registered at this code's v2 version: the only ones the morning issues."""
    from analysis.models import MODEL_VERSION_V2

    known = {(str(m.get("key")), str(m.get("version"))) for m in models if isinstance(m, dict)}
    return [k for k in V2_MODELS if (k, MODEL_VERSION_V2) in known]


def _live_only(conn, session: date, market, now: datetime) -> dict:
    """The GIFT Nifty gap, news and earnings load as known at issue time; see context.load_live_only."""
    from analysis import context as ctxmod
    from analysis.data import BY_NAME, previous_trading_day

    prev = previous_trading_day(BY_NAME["NIFTY"].exchange, session, market.holidays)
    nifty = market.series.get("NIFTY")
    before = nifty.before(session).sessions if nifty is not None else []
    prev_close = before[-1].close if before and before[-1].day == prev else None
    cutoff = ctxmod.issue_cutoff(session, now.astimezone(timezone.utc))
    return ctxmod.load_live_only(conn, session, prev, prev_close, cutoff)


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


def cmd_news_score(args) -> int:
    from analysis import news

    return news.run(args.limit, args.dry_run)


COMMANDS = {"backtest": cmd_backtest, "backtest-v2": cmd_backtest_v2, "issue": cmd_issue, "score": cmd_score,
            "news-score": cmd_news_score}


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
