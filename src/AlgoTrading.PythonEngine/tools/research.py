"""
tools/research.py

Offline research runner for option-buying candidates. Read-only against the
local database; writes JSON + a self-contained HTML report to
private/research/ (gitignored). Research reports are local files, never
published.

Usage (from src/AlgoTrading.PythonEngine):
    python tools/research.py coverage --underlying NIFTY
    python tools/research.py regime   --underlying NIFTY [--from 2026-06-01] [--to 2026-09-15]
                                      [--spotlight 2026-09-08,2026-09-10]
    python tools/research.py run      --candidate orb_breakout_buy|all --underlying NIFTY
                                      [--from ...] [--to ...] [--lots 1] [--train-months 3] [--test-months 1]
                                      [--slippage-pct 0.5] [--expiry-flag WEEK] [--expiry-code 1]

`run` needs option premiums in option_history_bars. Without them it says so,
writes the regime analysis and a signal census (where the candidate would have
signalled, no P&L), and exits 0 - it never estimates premiums from the index.
"""

from __future__ import annotations

import argparse
import os
import sys
from dataclasses import asdict, replace
from datetime import date, datetime, timedelta
from typing import Any, Dict, List, Optional

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from backtest.timeutil import IST  # noqa: E402
from core.config import REPO_ROOT  # noqa: E402
from research import candidates, data, harness, regime, report  # noqa: E402
from research.candidate import Features  # noqa: E402
from research.costs import CostModel  # noqa: E402

DEFAULT_OUT = os.path.join(str(REPO_ROOT), "private", "research")


def _date(text: Optional[str]) -> Optional[date]:
    return date.fromisoformat(text) if text else None


def _today_ist() -> date:
    return datetime.now(IST).date()


def _load(conn, args) -> Dict[str, Any]:
    start = _date(args.date_from) or date(2017, 7, 3)
    end = _date(args.date_to) or _today_ist()
    index = data.load_index_bars(conn, args.underlying, start, end, resolution=args.resolution)
    if not index.bars:
        raise data.ResearchDataError(
            f"no {index.symbol} {args.resolution}m candles between {start} and {end} in the local database. "
            "Backfill them first: Data -> Historical in the console, or POST /api/Backfill/history "
            "(tools/db_backfill_cli.py wraps it); FYERS allows 100 days of intraday per request."
        )
    rc = replace(regime.RegimeConfig(), bar_minutes=index.resolution_minutes)
    readings = regime.classify(index.bars, rc)
    features = Features(index.bars, readings, bar_minutes=index.resolution_minutes)
    sessions = regime.summarize_sessions(index.bars, readings, rc)
    notes = list(index.notes)
    last = index.sessions[-1].session
    if args.date_to and last < args.date_to:
        notes.append(f"requested range ends {args.date_to}; the latest local {index.symbol} session is {last}")
    return {"start": start, "end": end, "index": index, "rc": rc, "readings": readings, "features": features,
            "sessions": sessions, "notes": notes}


def _index_meta(index: data.IndexData) -> Dict[str, Any]:
    return {"symbol": index.symbol, "resolution_minutes": index.resolution_minutes, "sessions": len(index.sessions),
            "first": index.sessions[0].session, "last": index.sessions[-1].session,
            "partial_sessions": [asdict(s) for s in index.partial_sessions]}


def _spotlight(args, sessions) -> List[str]:
    keys = [s.session for s in sessions]
    wanted = [k.strip() for k in (args.spotlight or "").split(",") if k.strip()]
    chosen = [k for k in wanted if k in keys] or keys[-5:]
    return chosen


def _write(out_dir: str, stem: str, payload: Dict[str, Any]) -> str:
    os.makedirs(out_dir, exist_ok=True)
    report.write_json(os.path.join(out_dir, f"{stem}.json"), payload)
    path = os.path.join(out_dir, f"{stem}.html")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(report.render_html(payload))
    return path


def _sim_config(conn, args) -> harness.SimConfig:
    costs = CostModel(slippage_pct=args.slippage_pct)
    return harness.SimConfig(underlying=args.underlying.upper(), lot_size=data.lot_size(conn, args.underlying),
                             lots=args.lots, costs=costs)


# ---------------------------------------------------------------- commands --

def cmd_coverage(args) -> int:
    conn = data.connect()
    try:
        summary = data.coverage_summary(conn, args.underlying)
        print(f"{summary['underlying']} ({summary['symbol']})")
        for row in summary["candles"]:
            print(f"  candles  {row['resolution']:>2}  {row['from']} .. {row['to']}  rows={row['rows']} "
                  f"sessions={row['sessions']}")
        for row in summary["live_bars"]:
            print(f"  live_bars {row['resolution']:>2} {row['from']} .. {row['to']}  rows={row['rows']}")
        opt = summary["option_history_bars"]
        if opt.get("available"):
            for row in opt["series"]:
                print(f"  options  {row['expiry_flag']}/{row['expiry_code']} {row['resolution']} {row['from']} .. "
                      f"{row['to']} rows={row['rows']} offsets={row['offsets']}")
        else:
            print(f"  options  unavailable: {opt['reason']}")
    finally:
        conn.close()
    return 0


def cmd_regime(args) -> int:
    conn = data.connect()
    try:
        loaded = _load(conn, args)
        sim = _sim_config(conn, args)
    finally:
        conn.close()
    sessions = loaded["sessions"]
    spotlight = _spotlight(args, sessions)
    payload = {
        "generated_ist": datetime.now(IST).strftime("%Y-%m-%d %H:%M"),
        "underlying": args.underlying.upper(),
        "candidate": None,
        "index": _index_meta(loaded["index"]),
        "status": {"code": "regime", "headline": "Regime analysis only",
                   "detail": "Labels per 5m bar and per session, computed causally from index candles."},
        "regime": report.regime_payload(loaded["features"], sessions, loaded["rc"], spotlight),
        "method": report.method_notes(sim, loaded["rc"]),
        "notes": loaded["notes"],
    }
    stem = f"regime-{args.underlying.upper()}-{_today_ist().isoformat()}"
    path = _write(args.out, stem, payload)
    _print_sessions(sessions, spotlight)
    print(f"report: {path}")
    return 0


def _print_sessions(sessions, spotlight: List[str]) -> None:
    wanted = set(spotlight)
    for s in sessions:
        if s.session in wanted:
            c = s.counts
            print(f"  {s.session}  {str(s.dominant):13}  first {s.first_called_ist or '-':5}  confirmed "
                  f"{s.confirmed_ist or '-':5}  up/down/range/chop {c['TREND_UP']}/{c['TREND_DOWN']}/"
                  f"{c['RANGE']}/{c['VOLATILE_CHOP']}  open->close {s.change_pct:+.2f}%")


def cmd_run(args) -> int:
    names = candidates.names() if args.candidate == "all" else [args.candidate]
    for name in names:
        candidates.get(name)  # fail fast on a typo
    conn = data.connect()
    try:
        loaded = _load(conn, args)
        sim = _sim_config(conn, args)
        premium_error = None
        book = None
        try:
            index = loaded["index"]
            book = data.load_premium_book(conn, args.underlying, date.fromisoformat(index.sessions[0].session),
                                          date.fromisoformat(index.sessions[-1].session),
                                          resolution=f"{index.resolution_minutes}m",
                                          expiry_flag=args.expiry_flag, expiry_code=args.expiry_code)
        except data.PremiumDataUnavailable as ex:
            premium_error = str(ex)
    finally:
        conn.close()

    features, sessions = loaded["features"], loaded["sessions"]
    spotlight = _spotlight(args, sessions)
    regime_block = report.regime_payload(features, sessions, loaded["rc"], spotlight)
    baseline = candidates.get(candidates.BASELINE_NAME)
    paths = []
    for name in names:
        candidate = candidates.get(name)
        payload: Dict[str, Any] = {
            "generated_ist": datetime.now(IST).strftime("%Y-%m-%d %H:%M"),
            "underlying": args.underlying.upper(),
            "candidate": {"name": candidate.name, "title": candidate.title, "rules": candidate.rules,
                          "defaults": dict(candidate.defaults), "parameter_notes": dict(candidate.parameter_notes),
                          "grid": {k: list(v) for k, v in candidate.grid.items()},
                          "strike_offset": candidate.strike_offset,
                          "exit_policy": candidate.exits(candidate.params()).to_dict()},
            "index": _index_meta(loaded["index"]),
            "config": sim.to_dict(),
            "regime": regime_block,
            "method": report.method_notes(sim, loaded["rc"]),
            "notes": list(loaded["notes"]),
        }
        if book is None:
            payload["status"] = {
                "code": "awaiting_premiums",
                "headline": "Premium backtest not run: no option premium history",
                "detail": premium_error + " Below: the regime analysis and a signal census only.",
            }
            census = harness.signal_census(candidate, candidate.params(), features, sim)
            payload["census"] = {"rows": [asdict(r) for r in census]}
            against = sum(1 for r in census if r.against_regime)
            print(f"{name}: premiums unavailable; census {len(census)} signals, {against} against a called trend")
        else:
            payload["notes"] += book.notes
            payload["premium"] = {"rows": book.rows, "sessions": len(book.sessions),
                                  "offset_window": book.offset_window}
            wf = harness.walk_forward(candidate, features, book, sim, baseline=baseline,
                                      train_months=args.train_months, test_months=args.test_months,
                                      min_train_trades=args.min_train_trades)
            full = harness.simulate(candidate, candidate.params(), features, book, sim)
            payload["in_sample"] = report.trades_payload(full.trades)
            skipped = [asdict(s) for f in wf.folds for s in f.test.skipped] or [asdict(s) for s in full.skipped]
            payload["skipped"] = skipped
            payload["notes"] += wf.notes
            if wf.folds:
                oos = report.trades_payload(wf.test_trades)
                block = {
                    "oos": oos,
                    "folds": [{"window": f.window.to_dict(), "chosen_params": f.chosen_params,
                               "choice_reason": f.choice_reason, "grid": f.grid, "train": f.train_summary,
                               "test": f.test.summary,
                               "baseline_test": f.baseline_test.summary if f.baseline_test else None}
                              for f in wf.folds],
                }
                if candidate.name != baseline.name:
                    base = report.trades_payload(wf.baseline_trades)
                    block["baseline"] = {"name": baseline.name, "summary": base["summary"],
                                         "equity": base["equity"]}
                payload["walk_forward"] = block
                s = oos["summary"]
                payload["status"] = {"code": "complete", "headline": "Premium backtest complete",
                                     "detail": f"{len(wf.folds)} walk-forward folds; {s['trades']} out-of-sample "
                                               f"trades, net {report.rupees(s['net'])} after costs."}
                print(f"{name}: {len(wf.folds)} folds, OOS trades {s['trades']}, net {s['net']:.0f}, "
                      f"expectancy R {s['expectancy_r']}")
            else:
                payload["status"] = {"code": "in_sample_only",
                                     "headline": "In-sample only: too little history for walk-forward",
                                     "detail": "; ".join(wf.notes) + ". The numbers below are not out-of-sample."}
                print(f"{name}: in-sample only ({len(full.trades)} trades)")
        stem = f"{candidate.name}-{args.underlying.upper()}-{_today_ist().isoformat()}"
        paths.append(_write(args.out, stem, payload))
    _print_sessions(sessions, spotlight)
    for path in paths:
        print(f"report: {path}")
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    def common(p):
        p.add_argument("--underlying", default="NIFTY")
        p.add_argument("--from", dest="date_from", help="first IST date (default: all history)")
        p.add_argument("--to", dest="date_to", help="last IST date (default: today)")
        p.add_argument("--resolution", default="5")
        p.add_argument("--spotlight", help="comma-separated sessions to draw bar by bar (default: last 5)")
        p.add_argument("--lots", type=int, default=1)
        p.add_argument("--slippage-pct", type=float, default=0.5)
        p.add_argument("--out", default=DEFAULT_OUT)

    p = sub.add_parser("coverage", help="what the local database holds")
    p.add_argument("--underlying", default="NIFTY")
    p.set_defaults(fn=cmd_coverage)

    p = sub.add_parser("regime", help="regime labels and the session summary")
    common(p)
    p.set_defaults(fn=cmd_regime)

    p = sub.add_parser("run", help="simulate a candidate (walk-forward when premiums exist)")
    common(p)
    p.add_argument("--candidate", required=True, help=f"one of {', '.join(candidates.names())}, or all")
    p.add_argument("--train-months", type=int, default=3)
    p.add_argument("--test-months", type=int, default=1)
    p.add_argument("--min-train-trades", type=int, default=8)
    p.add_argument("--expiry-flag", default="WEEK")
    p.add_argument("--expiry-code", type=int, default=1)
    p.set_defaults(fn=cmd_run)

    args = parser.parse_args(argv)
    try:
        return args.fn(args)
    except data.ResearchDataError as ex:
        print(f"research: {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
