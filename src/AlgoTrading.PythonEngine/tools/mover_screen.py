"""
tools/mover_screen.py

Live mover screen for the whole stock universe (see research/equities/screen.py
for what it shows and what research stands behind it).

  prepare   after the close (or before 09:00): fetch the day's bhavcopy, build
            the universe for the next session, map it to Dhan ids through the
            day's Dhan scrip master (a dated copy is kept), add the day's
            5-minute bars, and write screen/baseline-<last session>.json.
  run       during the session: one Dhan quote call a minute for the whole
            universe; writes screen/live.html (refreshes itself), appends every
            quote to screen/<date>.jsonl, and prints the lists. Stops at 15:30.
  run --once  one poll now, at any hour (a smoke test after the close).

Usage (from src/AlgoTrading.PythonEngine):
    python tools/mover_screen.py prepare [--spacing 0.5] [--skip-eod] [--skip-bars]
    python tools/mover_screen.py run [--interval 60] [--top 20] [--open] [--once]
                                     [--env-file ~/.config/openfno/dhan.env] [--token-command "…"]

Dhan credentials: DHAN_CLIENT_ID and DHAN_ACCESS_TOKEN from --env-file (read
again whenever Dhan refuses the token) or the environment. --token-command (or
$DHAN_TOKEN_COMMAND) is run when the token is refused, to write a fresh env
file first — for example a private script that reads the server's Dhan session.

Runs on a workstation. The Dhan request budget is per account and shared with
the production server: prepare makes one history request per universe stock
(~650), run makes one quote call a minute.
"""

from __future__ import annotations

import argparse
import csv
import glob
import gzip
import json
import os
import socket
import subprocess
import sys
import time
from datetime import date, datetime, timedelta, timezone
from typing import Dict, List, Optional, Tuple

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

import pandas as pd  # noqa: E402

from research.equities import bhavcopy, intraday, screen as sc, selection as sel  # noqa: E402
from research.equities.setups import sessions_from_frame  # noqa: E402

IST = timezone(timedelta(hours=5, minutes=30))
DATA = os.path.join(os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data")), "equities")
EOD_ROOT = os.path.join(DATA, "eod")
BARS_ROOT = os.path.join(DATA, "intraday-5m")
SCREEN_DIR = os.path.join(DATA, "screen")
REFERENCE_DIR = os.path.join(DATA, "reference")
MASTER_URL = "https://images.dhan.co/api-data/api-scrip-master-detailed.csv"
QUOTE_URL = "https://api.dhan.co/v2/marketfeed/quote"
QUOTE_BATCH = 1000
DEFAULT_ENV_FILE = os.path.expanduser("~/.config/openfno/dhan.env")


def force_ipv4() -> None:
    """
    Resolve IPv4 only. On a network whose IPv6 route is dead, requests tries
    every IPv6 address first and a single Dhan call took 84 s (measured
    2026-09-16; IPv4 connects in under a second).
    """
    import urllib3.util.connection as connection
    connection.allowed_gai_family = lambda: socket.AF_INET


# ------------------------------------------------------------------ prepare --

def universe_for_next_session(eod_root: str, today: date) -> Tuple[str, pd.DataFrame]:
    raw = sel.load_nse(eod_root, (today - timedelta(days=420)).isoformat(), today.isoformat())
    feats = sel.add_features(raw[raw["series"] == "EQ"])
    basis = feats["trade_date"].max()
    latest = feats[(feats["trade_date"] == basis) & feats["in_universe"]]
    return basis.strftime("%Y-%m-%d"), latest


def scrip_master(today: date, log=print) -> str:
    """Today's detailed Dhan scrip master, downloaded once a day and kept compressed (it is the only record of
    that day's circuit limits and ASM/GSM flags)."""
    os.makedirs(REFERENCE_DIR, exist_ok=True)
    path = os.path.join(REFERENCE_DIR, f"dhan-scrip-master-detailed-{today:%Y-%m-%d}.csv.gz")
    if not os.path.exists(path):
        import requests
        response = requests.get(MASTER_URL, timeout=120)
        response.raise_for_status()
        tmp = path + ".part"
        with gzip.open(tmp, "wb") as fh:
            fh.write(response.content)
        os.replace(tmp, path)
        log(f"scrip master saved: {path} ({len(response.content) / 1e6:.0f} MB raw)")
    return path


def nse_equities_by_isin(master_path: str) -> Dict[str, Dict[str, str]]:
    out: Dict[str, Dict[str, str]] = {}
    opener = gzip.open if master_path.endswith(".gz") else open
    with opener(master_path, "rt", newline="") as fh:
        for r in csv.DictReader(fh):
            if r.get("EXCH_ID") == "NSE" and r.get("SEGMENT") == "E" and r.get("INSTRUMENT") == "EQUITY" \
                    and r.get("SERIES") == "EQ" and r.get("ISIN"):
                out[r["ISIN"]] = {"security_id": r["SECURITY_ID"], "symbol": r.get("UNDERLYING_SYMBOL", ""),
                                  "asm_gsm": r.get("ASM_GSM_FLAG", "")}
    return out


def map_to_dhan(latest: pd.DataFrame, by_isin: Dict[str, Dict[str, str]]) -> Tuple[List[dict], List[str]]:
    """Universe rows -> Dhan ids by ISIN (today's master only: an id is a token and changes with the series)."""
    mapped, unmapped = [], []
    for r in latest.itertuples(index=False):
        hit = by_isin.get(str(r.isin))
        if hit is None:
            unmapped.append(r.symbol)
            continue
        mapped.append({"symbol": r.symbol, "isin": r.isin, "security_id": hit["security_id"],
                       "asm_gsm": hit["asm_gsm"], "row": r})
    return mapped, unmapped


def recent_sessions(bars_root: str, symbol: str, keep_days: int = 60, basis: Optional[str] = None):
    """The stock's sessions from the window files that can hold its last `keep_days` calendar days."""
    folder = os.path.join(bars_root, "raw", symbol)
    if not os.path.isdir(folder):
        return {}
    cutoff = ((date.fromisoformat(basis) if basis else date.today()) - timedelta(days=keep_days)).strftime("%Y%m%d")
    parts = [pd.read_csv(p) for p in sorted(glob.glob(os.path.join(folder, "*.csv.gz")))
             if os.path.basename(p)[9:17] >= cutoff]
    if not parts:
        return {}
    frame = pd.concat(parts, ignore_index=True).drop_duplicates("bar_start_ist")
    return sessions_from_frame(symbol, frame)


def prepare(args) -> int:
    today = datetime.now(IST).date()
    if not args.skip_eod:
        start = today - timedelta(days=10)
        print(f"bhavcopy {start} → {today} (404s of the last 3 days are asked again)", flush=True)
        bhavcopy.download(EOD_ROOT, start, today, log=lambda line: print("  " + line, flush=True),
                          retry_missing_from=today - timedelta(days=3))
        bhavcopy.normalise(EOD_ROOT, start, today, log=lambda _: None)
    basis, latest = universe_for_next_session(EOD_ROOT, today)
    print(f"universe after the {basis} session: {len(latest)} stocks", flush=True)

    by_isin = nse_equities_by_isin(scrip_master(today))
    mapped, unmapped = map_to_dhan(latest, by_isin)
    if unmapped:
        print(f"no NSE EQ row in today's Dhan master for {len(unmapped)}: {', '.join(unmapped[:20])}", flush=True)

    if not args.skip_bars:
        lo = date.fromisoformat(basis) - timedelta(days=6)
        print(f"5-minute bars {lo} → {basis} for {len(mapped)} stocks", flush=True)
        try:
            counts = intraday.download(BARS_ROOT, [(m["symbol"], m["security_id"]) for m in mapped], lo,
                                       date.fromisoformat(basis), log=lambda line: print("  " + line, flush=True),
                                       pacer=intraday.Pacer(spacing=args.spacing))
        except intraday.AuthError as ex:
            print(f"bars not updated: {ex}. The baseline is built from the bars already on disk.", flush=True)
        else:
            print("  " + " ".join(f"{k}={v}" for k, v in counts.items()), flush=True)

    before = (date.fromisoformat(basis) + timedelta(days=1)).isoformat()
    baselines, thin = [], []
    for m in mapped:
        sessions = recent_sessions(BARS_ROOT, m["symbol"], basis=basis)
        facts = sc.baseline_from_sessions(sessions, before=before)
        last_bar_day = max(sessions) if sessions else None
        if facts["or15_volume"] is None:
            thin.append(m["symbol"])
        r = m["row"]
        baselines.append(sc.Baseline(
            m["symbol"], m["security_id"], m["isin"], basis, float(r.close), float(r.high), float(r.low),
            float(r.atr_pct), float(r.median_turnover), facts["or15_volume"], facts["cum_volume"],
            int(facts["sessions"]), m["asm_gsm"]))
        if last_bar_day and last_bar_day < basis:
            m["stale"] = last_bar_day
    stale = [m["symbol"] for m in mapped if m.get("stale")]

    os.makedirs(SCREEN_DIR, exist_ok=True)
    path = os.path.join(SCREEN_DIR, f"baseline-{basis}.json")
    doc = {"basis_date": basis, "built_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
           "stocks": len(baselines), "no_dhan_id": unmapped, "no_volume_history": thin,
           "bars_end_before_basis": stale, "baselines": [sc.baseline_to_json(b) for b in baselines]}
    tmp = path + ".part"
    with open(tmp, "w") as fh:
        json.dump(doc, fh)
    os.replace(tmp, path)
    print(f"wrote {path}: {len(baselines)} stocks, {len(thin)} without 10 sessions of bars, "
          f"{len(stale)} whose bars stop before {basis}", flush=True)
    return 0


# ---------------------------------------------------------------------- run --

def pick_baseline(files: List[str], today: date) -> Optional[str]:
    """The newest baseline built from a session before today."""
    dated = []
    for f in files:
        name = os.path.basename(f)
        try:
            day = date.fromisoformat(name[len("baseline-"):-len(".json")])
        except ValueError:
            continue
        if day < today:
            dated.append((day, f))
    return max(dated)[1] if dated else None


def read_env_file(path: str) -> Dict[str, str]:
    out = {}
    if path and os.path.exists(path):
        with open(path) as fh:
            for line in fh:
                if "=" in line and not line.lstrip().startswith("#"):
                    k, v = line.strip().split("=", 1)
                    out[k.strip()] = v.strip().strip('"').strip("'")
    return out


class Credentials:
    def __init__(self, env_file: str, token_command: Optional[str]):
        self.env_file, self.token_command = env_file, token_command

    def current(self) -> Tuple[Optional[str], Optional[str]]:
        env = read_env_file(self.env_file)
        return (env.get("DHAN_CLIENT_ID") or os.environ.get("DHAN_CLIENT_ID"),
                env.get("DHAN_ACCESS_TOKEN") or os.environ.get("DHAN_ACCESS_TOKEN"))

    def refresh(self) -> bool:
        if not self.token_command:
            return False
        result = subprocess.run(self.token_command, shell=True, capture_output=True, text=True, timeout=120)
        # Only the exit code is reported: the command's output may carry the token.
        print(f"token command exited {result.returncode}", flush=True)
        return result.returncode == 0


def fetch_quotes(ids: List[str], creds: Credentials, session) -> Tuple[Dict[str, sc.Quote], str]:
    """All quotes, in batches of 1,000 a second apart. The second value names a problem, or is empty."""
    quotes: Dict[str, sc.Quote] = {}
    for n, k in enumerate(range(0, len(ids), QUOTE_BATCH)):
        if n:
            time.sleep(1.1)
        batch = [int(x) for x in ids[k:k + QUOTE_BATCH]]
        for attempt in (1, 2):
            client_id, token = creds.current()
            if not client_id or not token:
                return quotes, "no Dhan credentials"
            response = session.post(QUOTE_URL, json={"NSE_EQ": batch}, timeout=(10, 30),
                                    headers={"access-token": token, "client-id": client_id,
                                             "Content-Type": "application/json", "Accept": "application/json"})
            if response.status_code in (401, 403) and attempt == 1 and creds.refresh():
                continue
            if response.status_code != 200:
                return quotes, f"Dhan answered HTTP {response.status_code}: {response.text[:120]}"
            quotes.update(sc.parse_quotes(response.json()))
            break
    return quotes, ""


def print_snapshot(snap: sc.Snapshot, top: int) -> None:
    print(f"\n{snap.at}  quoted {snap.quoted}/{snap.universe}", flush=True)
    if snap.frozen:
        print("  09:30 list (tested):  " + "  ".join(
            f"{f.symbol} {f.rvol15:.1f}× {'' if f.since_0930_pct is None else f'{f.since_0930_pct:+.2f}%'}"
            for f in snap.frozen), flush=True)
    elif snap.frozen is not None:
        print("  09:30 list: " + snap.frozen_note, flush=True)
    for i, r in enumerate(snap.rows[:top], 1):
        rv = "  –  " if r.rvol_now is None else f"{r.rvol_now:5.1f}×"
        atr = "  – " if r.move_atr is None else f"{r.move_atr:+4.1f}"
        print(f"  {i:2}. {r.symbol:<14} {r.ltp:>10,.2f} {r.change_pct:+6.2f}%  ATRs {atr}  rel.vol {rv}  "
              f"prev H/L {r.prev_high:,.2f}/{r.prev_low:,.2f}  {', '.join(r.flags)}", flush=True)


def write_outputs(snap: sc.Snapshot, quotes: Dict[str, sc.Quote], today: date, top: int, refresh: int,
                  status: str) -> str:
    os.makedirs(SCREEN_DIR, exist_ok=True)
    page = os.path.join(SCREEN_DIR, "live.html")
    tmp = page + ".part"
    with open(tmp, "w") as fh:
        fh.write(sc.render_html(snap, top=top, refresh_seconds=refresh, status=status))
    os.replace(tmp, page)
    if quotes:
        with open(os.path.join(SCREEN_DIR, f"{today:%Y-%m-%d}.jsonl"), "a") as fh:
            fh.write(json.dumps(sc.record(snap, quotes)) + "\n")
    return page


def run(args) -> int:
    import requests
    force_ipv4()
    today = datetime.now(IST).date()
    path = args.baseline or pick_baseline(glob.glob(os.path.join(SCREEN_DIR, "baseline-*.json")), today)
    if not path:
        print("no baseline from an earlier session: run `prepare` first", flush=True)
        return 2
    with open(path) as fh:
        doc = json.load(fh)
    state = sc.ScreenState(sc.baseline_from_json(b) for b in doc["baselines"])
    ids = list(state.baselines)
    print(f"baseline {os.path.basename(path)}: {len(ids)} stocks (levels from the {doc['basis_date']} session)",
          flush=True)
    creds = Credentials(args.env_file, args.token_command or os.environ.get("DHAN_TOKEN_COMMAND"))
    session = requests.Session()
    opened, said_waiting = False, False
    while True:
        now = datetime.now(IST)
        if not args.once and now.time() < sc.OPEN:
            if not said_waiting:
                print(f"waiting for 09:15 (now {now:%H:%M})", flush=True)
                said_waiting = True
            time.sleep(min(30.0, (datetime.combine(now.date(), sc.OPEN, IST) - now).total_seconds() + 1))
            continue
        try:
            quotes, problem = fetch_quotes(ids, creds, session)
        except Exception as ex:  # network trouble: say it and try at the next poll
            quotes, problem = {}, f"{type(ex).__name__}: {str(ex)[:120]}"
        now = datetime.now(IST)
        snap = state.update(quotes, now)
        status = problem or ("no quotes (market closed?)" if not quotes else "")
        page = write_outputs(snap, quotes, today, args.top, max(15, args.interval // 2), status)
        if problem:
            print(f"{now:%H:%M:%S} {problem}", flush=True)
        print_snapshot(snap, min(args.top, 15))
        if args.open and not opened:
            subprocess.run(["open", page], check=False)
            opened = True
        if args.once or now.time() >= sc.CLOSE:
            print(f"\npage: {page}", flush=True)
            return 0 if quotes else 1
        wait = args.interval - (time.time() % args.interval) + 2
        time.sleep(wait)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=("prepare", "run"))
    parser.add_argument("--spacing", type=float, default=0.5, help="seconds between history requests (prepare)")
    parser.add_argument("--skip-eod", action="store_true", help="prepare: do not fetch bhavcopies")
    parser.add_argument("--skip-bars", action="store_true", help="prepare: do not fetch 5-minute bars")
    parser.add_argument("--interval", type=int, default=60, help="run: seconds between quote polls")
    parser.add_argument("--top", type=int, default=20)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--open", action="store_true", help="run: open the page in the browser")
    parser.add_argument("--baseline")
    parser.add_argument("--env-file", default=DEFAULT_ENV_FILE)
    parser.add_argument("--token-command")
    args = parser.parse_args(argv)
    if args.command == "prepare":
        force_ipv4()
        client_id, token = Credentials(args.env_file, None).current()
        if client_id and token:
            os.environ.setdefault("DHAN_CLIENT_ID", client_id)
            os.environ.setdefault("DHAN_ACCESS_TOKEN", token)
        return prepare(args)
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
