"""
tools/data_catalog.py

One catalog of every research and market dataset on this workstation, built by
scanning the data itself (never from memory): what it is, where it lives,
which vendor it came from, the range it covers, how many sessions of that range
are present, its size, and its known gaps.

Writes <data root>/CATALOG.json and CATALOG.md, and an offline page when --html
is given. Read-only: it never changes a dataset.

Usage (from src/AlgoTrading.PythonEngine):
    python tools/data_catalog.py [--html ../../private/data-catalog.html] [--server-json server.json]

--server-json: facts gathered on the production server by a separate, private
script (table sizes, chunk ranges, the Drive archive manifest). The server is
never queried from here.
"""

from __future__ import annotations

import argparse
import csv
import glob
import gzip
import html
import json
import os
import subprocess
import sys
from collections import Counter, defaultdict
from datetime import date, datetime, timezone
from typing import Dict, List, Optional

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

DATA = os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data"))
REPO = os.path.abspath(os.path.join(ENGINE_DIR, "..", ".."))


def du(path: str) -> int:
    total = 0
    for root, _, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total


def human(n: Optional[float]) -> str:
    if n is None:
        return "–"
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return f"{n:,.0f} {unit}" if unit in ("B", "KB") else f"{n:,.1f} {unit}"
        n /= 1024


def nse_sessions() -> List[str]:
    """Trading sessions as the exchange files prove them: days with a verified NSE bhavcopy."""
    path = os.path.join(DATA, "equities", "eod", "manifest.jsonl")
    ok = set()
    if os.path.exists(path):
        with open(path) as fh:
            latest = {}
            for line in fh:
                r = json.loads(line)
                latest[(r["day"], r["source"])] = r["status"]
        ok = {d for (d, s), st in latest.items() if s.startswith("nse_") and s != "nse_delivery" and st == "ok"}
    return sorted(ok)


def coverage(days: set, sessions: List[str]) -> Dict[str, object]:
    if not days:
        return {"sessions_present": 0}
    lo, hi = min(days), max(days)
    expected = [s for s in sessions if lo <= s <= hi]
    missing = [s for s in expected if s not in days]
    return {"first": lo, "last": hi, "sessions_present": len(days & set(expected)) if expected else len(days),
            "sessions_expected": len(expected) or None, "missing_sessions": len(missing), "missing_sample": missing[:8]}


# --------------------------------------------------------------- database --

def psql(sql: str) -> List[List[str]]:
    out = subprocess.run(["docker", "exec", "algotrading_db", "psql", "-U", "postgres", "-d", "algotrading", "-F", "\t",
                          "-Atc", sql], capture_output=True, text=True, timeout=600)
    if out.returncode != 0:
        raise RuntimeError(out.stderr.strip()[:300])
    return [line.split("\t") for line in out.stdout.splitlines() if line.strip()]


def database_entries(sessions: List[str]) -> List[dict]:
    entries = []
    rows = psql('SELECT "Underlying", "ExpiryFlag", "Resolution", min("StrikeOffset"), max("StrikeOffset"), count(*), '
                'string_agg(DISTINCT to_char("BarStartUtc" AT TIME ZONE \'Asia/Kolkata\', \'YYYY-MM-DD\'), \',\') '
                'FROM option_history_bars GROUP BY 1, 2, 3 ORDER BY 1')
    for u, flag, res, lo, hi, n, days in rows:
        entries.append({
            "id": f"db.option_history_bars.{u}", "group": "Index options", "vendor": "Dhan (expired options API)",
            "what": f"{u} option premiums, nearest {flag.lower()} expiry, ATM{int(lo):+d}…ATM{int(hi):+d}, CE and PE, "
                    f"{res} bars with OI, IV and spot",
            "where": "Mac database: option_history_bars", "rows": int(n), **coverage(set(days.split(",")), sessions)})
    rows = psql('SELECT "Symbol", "Resolution", count(*), string_agg(DISTINCT to_char("TimeStampUtc" AT TIME ZONE '
                '\'Asia/Kolkata\', \'YYYY-MM-DD\'), \',\') FROM candles WHERE "Symbol" LIKE \'%-INDEX\' GROUP BY 1, 2 ORDER BY 1, 2')
    for sym, res, n, days in rows:
        entries.append({
            "id": f"db.candles.{sym}.{res}", "group": "Index candles", "vendor": "FYERS / Dhan history, live archive",
            "what": f"{sym} {res}{'' if res == 'D' else '-minute'} candles", "where": "Mac database: candles",
            "rows": int(n), **coverage(set(days.split(",")), sessions)})
    other = psql('SELECT CASE WHEN "Symbol" LIKE \'MCX:%\' THEN \'MCX contracts\' ELSE \'option contracts\' END, count(*), '
                 'min("TimeStampUtc")::date, max("TimeStampUtc")::date FROM candles WHERE "Symbol" NOT LIKE \'%-INDEX\' GROUP BY 1')
    for kind, n, lo, hi in other:
        entries.append({"id": f"db.candles.{kind.replace(' ', '_')}", "group": "Contract candles (short, from live runs)",
                        "vendor": "live archive", "what": f"{kind}, 1/5/15-minute", "where": "Mac database: candles",
                        "rows": int(n), "first": lo, "last": hi})
    size = psql("SELECT pg_database_size('algotrading')")[0][0]
    entries.append({"id": "db.size", "group": "Storage", "vendor": "–", "what": "Mac database (docker algotrading_db, port 5433)",
                    "where": "docker volume", "bytes": int(size)})
    return entries


# ------------------------------------------------------------------ files --

def manifest_counts(path: str, key_fields, status_field="status") -> Dict[str, int]:
    latest = {}
    if os.path.exists(path):
        with open(path) as fh:
            for line in fh:
                if line.strip():
                    r = json.loads(line)
                    latest[tuple(r[k] for k in key_fields)] = r[status_field]
    return dict(Counter(latest.values()))


def _last_day(folder: str, sub: str) -> Optional[str]:
    """The last day a file of the series holds: candle files end on their window's end date, option folders are months."""
    if sub.startswith("candles"):
        ends = [os.path.basename(f)[9:17] for f in glob.glob(os.path.join(folder, "*.csv.gz"))]
        if not ends:
            return None
        last_file = max(glob.glob(os.path.join(folder, "*.csv.gz")), key=lambda f: os.path.basename(f)[9:17])
    else:
        months = sorted(glob.glob(os.path.join(folder, "*")))
        if not months:
            return None
        files = glob.glob(os.path.join(months[-1], "*.csv.gz"))
        if not files:
            return os.path.basename(months[-1])
        last_file = files[0]
    last = None
    with gzip.open(last_file, "rt") as fh:
        next(fh, None)
        for line in fh:
            last = line[:10]
    return last


def file_entries(sessions: List[str]) -> List[dict]:
    entries = []
    eod = os.path.join(DATA, "equities", "eod")
    if os.path.isdir(eod):
        entries.append({"id": "files.equities.eod", "group": "Stocks, end of day", "vendor": "NSE and BSE archives",
                        "what": "Daily bhavcopy of every security (OHLC, volume, turnover, trades) and NSE delivery %",
                        "where": eod, "bytes": du(eod),
                        "status": manifest_counts(os.path.join(eod, "manifest.jsonl"), ("day", "source")),
                        **coverage(set(sessions), sessions)})
    bars = os.path.join(DATA, "equities", "intraday-5m")
    if os.path.isdir(bars):
        stocks = [d for d in os.listdir(os.path.join(bars, "raw"))] if os.path.isdir(os.path.join(bars, "raw")) else []
        months = set()
        for f in glob.glob(os.path.join(bars, "raw", "*", "*.csv.gz")):
            name = os.path.basename(f)
            months.add(name[:4] + "-" + name[4:6])
            months.add(name[9:13] + "-" + name[13:15])
        entries.append({"id": "files.equities.intraday-5m", "group": "Stocks, intraday", "vendor": "Dhan (intraday charts API)",
                        "what": f"5-minute bars for {len(stocks):,} stocks: every stock that was ever in the research universe, "
                                "delisted ones by their old token", "where": bars, "bytes": du(bars),
                        "first": min(months) if months else None, "last": max(months) if months else None,
                        "status": manifest_counts(os.path.join(bars, "manifest.jsonl"), ("symbol", "window_from"))})
    opts = os.path.join(DATA, "equities", "options-5m")
    if os.path.isdir(opts):
        months = sorted({os.path.basename(p) for p in glob.glob(os.path.join(opts, "raw", "*", "*"))})
        stocks = os.listdir(os.path.join(opts, "raw")) if os.path.isdir(os.path.join(opts, "raw")) else []
        entries.append({"id": "files.equities.options-5m", "group": "Stock options", "vendor": "Dhan (expired options API)",
                        "what": f"5-minute option bars (nearest monthly, ATM-3…ATM+3, CE and PE, OI and IV) for the F&O stocks "
                                f"on each day's 09:30 mover list; {len(stocks)} stocks so far",
                        "where": opts, "bytes": du(opts), "first": months[0] if months else None,
                        "last": months[-1] if months else None,
                        "status": manifest_counts(os.path.join(opts, "manifest.jsonl"),
                                                  ("symbol", "month", "option_type", "offset"))})
    index_root = os.path.join(DATA, "index")
    for sub, what in (("options-1m", "1-minute option premiums, nearest expiry, ATM-5…ATM+5, CE and PE, with OI, IV and spot"),
                      ("candles-1m", "1-minute index candles"), ("candles-5m", "5-minute index candles")):
        manifest = os.path.join(index_root, sub, "manifest.jsonl")
        if not os.path.exists(manifest):
            continue
        latest = {}
        with open(manifest) as fh:
            for line in fh:
                if line.strip():
                    r = json.loads(line)
                    latest[tuple(r["key"])] = r
        by_name = defaultdict(list)
        for key, r in latest.items():
            by_name[key[1]].append((key, r))
        for name, items in sorted(by_name.items()):
            ok = [(k, r) for k, r in items if r["status"] == "ok"]
            entries.append({
                "id": f"files.index.{sub}.{name}", "group": "Index history (files, from 2020)", "vendor": "Dhan",
                "what": f"{name}: {what}", "where": os.path.join(index_root, sub, "raw", name),
                "bytes": du(os.path.join(index_root, sub, "raw", name)), "rows": sum(r["rows"] for _, r in ok),
                "first": min(k[3] for k, _ in ok) if ok else None,
                "last": _last_day(os.path.join(index_root, sub, "raw", name), sub),
                "status": dict(Counter(r["status"] for _, r in items))})
    ref = os.path.join(DATA, "equities", "reference")
    if os.path.isdir(ref):
        snaps = sorted(glob.glob(os.path.join(ref, "dhan-scrip-master-detailed-*")))
        entries.append({"id": "files.equities.reference", "group": "Reference", "vendor": "Dhan",
                        "what": "Detailed scrip master snapshots (ids, ISIN, series, lot size, circuit limits, ASM/GSM)",
                        "where": ref, "bytes": du(ref), "snapshots": [os.path.basename(s)[27:37] for s in snaps]})
    screen = os.path.join(DATA, "equities", "screen")
    if os.path.isdir(screen):
        entries.append({"id": "files.equities.screen", "group": "Mover screen", "vendor": "Dhan quotes + own baselines",
                        "what": "Next-session baselines and every quote the live screen polled",
                        "where": screen, "bytes": du(screen),
                        "baselines": sorted(os.path.basename(f)[9:19] for f in glob.glob(os.path.join(screen, "baseline-*.json"))),
                        "recorded_days": sorted(os.path.basename(f)[:10] for f in glob.glob(os.path.join(screen, "20*.jsonl")))})
    backup = os.path.join(DATA, "research-backup")
    if os.path.isdir(backup):
        entries.append({"id": "files.research-backup", "group": "Backups", "vendor": "–",
                        "what": "Verified backup copies (option bars and candles of 15 Sep; stock 5-minute bars tar of 16 Sep)",
                        "where": backup, "bytes": du(backup), "folders": sorted(os.listdir(backup))})
    cache = os.path.join(REPO, "private", "research", "cache")
    if os.path.isdir(cache):
        entries.append({"id": "files.research-cache", "group": "Derived (rebuildable)", "vendor": "–",
                        "what": "Minute grids and decision sets built from option_history_bars for fast research",
                        "where": cache, "bytes": du(cache), "files": sorted(os.listdir(cache))})
    return entries


# ----------------------------------------------------------------- output --

def markdown(doc: dict) -> str:
    lines = [f"# Data catalog", "", f"Built {doc['built_utc']} by scanning the data. Trading sessions known from the "
             f"exchange files: {doc['sessions']['count']} ({doc['sessions']['first']} → {doc['sessions']['last']}).", ""]
    groups = defaultdict(list)
    for e in doc["datasets"]:
        groups[e["group"]].append(e)
    for g, items in groups.items():
        lines += [f"## {g}", "", "| Dataset | Vendor | Range | Sessions | Rows / status | Size | Where |", "|---|---|---|---|---|---|---|"]
        for e in items:
            rng = f"{e.get('first') or '–'} → {e.get('last') or '–'}"
            sess = (f"{e['sessions_present']:,}/{e['sessions_expected']:,} (missing {e['missing_sessions']})"
                    if e.get("sessions_expected") else "–")
            rows = f"{e['rows']:,}" if e.get("rows") else (", ".join(f"{k} {v:,}" for k, v in e.get("status", {}).items()) or "–")
            lines.append(f"| {e['what']} | {e['vendor']} | {rng} | {sess} | {rows} | {human(e.get('bytes'))} | `{e['where']}` |")
        lines.append("")
    return "\n".join(lines)


def page(doc: dict) -> str:
    e = html.escape
    groups = defaultdict(list)
    for d in doc["datasets"]:
        groups[d["group"]].append(d)
    body = ""
    for g, items in groups.items():
        rows = ""
        for d in items:
            rng = f"{d.get('first') or '–'} → {d.get('last') or '–'}"
            if d.get("sessions_expected"):
                sess = f"{d['sessions_present']:,} / {d['sessions_expected']:,}"
                if d["missing_sessions"]:
                    sess += f"<br><span class='muted'>missing {d['missing_sessions']}: {e(', '.join(d['missing_sample']))}</span>"
            else:
                sess = "–"
            status = (f"{d['rows']:,} rows" if d.get("rows") else
                      ", ".join(f"{e(k)} {v:,}" for k, v in d.get("status", {}).items()) or "–")
            extra = ""
            for key in ("snapshots", "baselines", "recorded_days", "folders", "files"):
                if d.get(key):
                    extra += f"<br><span class='muted'>{key.replace('_', ' ')}: {e(', '.join(map(str, d[key][-8:])))}</span>"
            rows += (f"<tr><td>{e(d['what'])}{extra}</td><td>{e(d['vendor'])}</td><td class='num'>{e(rng)}</td>"
                     f"<td class='num'>{sess}</td><td class='num'>{status}</td><td class='num'>{human(d.get('bytes'))}</td>"
                     f"<td><code>{e(d['where'])}</code></td></tr>")
        body += (f"<h2>{e(g)}</h2><div class='wrap'><table><thead><tr><th>Dataset</th><th>Vendor</th><th>Range</th>"
                 f"<th>Sessions</th><th>Rows / status</th><th>Size</th><th>Where</th></tr></thead><tbody>{rows}</tbody></table></div>")
    server = doc.get("server")
    if server:
        srows = "".join(f"<tr><td>{e(str(r[0]))}</td><td>{e(str(r[1]))}</td><td class='num'>{e(str(r[2]))}</td>"
                        f"<td class='num'>{e(str(r[3]))}</td></tr>" for r in server.get("rows", []))
        body += (f"<h2>Production server (reported {e(server.get('gathered', ''))})</h2><div class='wrap'><table><thead><tr>"
                 f"<th>Kind</th><th>Table / archive</th><th>Size or range</th><th>Rows or days</th></tr></thead>"
                 f"<tbody>{srows}</tbody></table></div>")
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Data Catalog</title><style>
:root {{ --bg:#F5F6F4; --surface:#FFF; --text:#1A211C; --muted:#5F6B63; --line:#D6DCD5; --head:#ECEFEA; }}
@media (prefers-color-scheme: dark) {{ :root:not([data-theme="light"]) {{ --bg:#101412; --surface:#171C19; --text:#E5EAE6; --muted:#98A49C; --line:#2B332E; --head:#1F2622; }} }}
:root[data-theme="dark"] {{ --bg:#101412; --surface:#171C19; --text:#E5EAE6; --muted:#98A49C; --line:#2B332E; --head:#1F2622; }}
* {{ box-sizing:border-box; }} body {{ margin:0; background:var(--bg); color:var(--text); font:14px/1.5 -apple-system,"Segoe UI",system-ui,sans-serif; padding:24px 16px 60px; }}
.page {{ max-width:1200px; margin:0 auto; }} h1 {{ margin:0 0 4px; }} h2 {{ font-size:1.05rem; margin:28px 0 8px; }}
.muted {{ color:var(--muted); }} .wrap {{ overflow-x:auto; border:1px solid var(--line); border-radius:10px; background:var(--surface); }}
table {{ border-collapse:collapse; width:100%; }} th,td {{ padding:7px 10px; border-bottom:1px solid var(--line); text-align:left; vertical-align:top; }}
th {{ background:var(--head); color:var(--muted); font:600 11.5px/1.3 ui-monospace,Menlo,monospace; text-transform:uppercase; }}
td.num {{ font-family:ui-monospace,Menlo,monospace; white-space:nowrap; }} code {{ font-size:12px; overflow-wrap:anywhere; }}
</style></head><body><div class="page"><h1>Data Catalog</h1>
<p class="muted">Built {e(doc['built_utc'])} by scanning the data itself. Trading sessions known from the exchange files: {doc['sessions']['count']:,} ({e(doc['sessions']['first'])} → {e(doc['sessions']['last'])}). "Sessions" counts days present against the exchange's sessions in the dataset's own range. Regenerate: <code>python tools/data_catalog.py --html ../../private/data-catalog.html</code>.</p>
{body}</div></body></html>"""


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--html")
    ap.add_argument("--server-json")
    args = ap.parse_args(argv)
    sessions = nse_sessions()
    doc = {"built_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
           "sessions": {"count": len(sessions), "first": sessions[0] if sessions else None,
                        "last": sessions[-1] if sessions else None},
           "datasets": []}
    try:
        doc["datasets"] += database_entries(sessions)
    except Exception as ex:
        print(f"database not scanned: {ex}", flush=True)
    doc["datasets"] += file_entries(sessions)
    if args.server_json and os.path.exists(args.server_json):
        with open(args.server_json) as fh:
            doc["server"] = json.load(fh)
    os.makedirs(DATA, exist_ok=True)
    with open(os.path.join(DATA, "CATALOG.json"), "w") as fh:
        json.dump(doc, fh, indent=1)
    with open(os.path.join(DATA, "CATALOG.md"), "w") as fh:
        fh.write(markdown(doc))
    if args.html:
        with open(args.html, "w") as fh:
            fh.write(page(doc))
    print(markdown(doc))
    return 0


if __name__ == "__main__":
    sys.exit(main())
