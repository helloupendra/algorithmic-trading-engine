"""
research/report.py

The research run as JSON and as one self-contained HTML file: inline CSS and
inline SVG, no scripts, fonts or images from anywhere. Written to
`private/research/` (gitignored) - research reports stay local.
"""

from __future__ import annotations

import html
import json
import math
from collections import Counter
from datetime import datetime, timedelta
from typing import Any, Dict, List, Optional, Sequence

from backtest.timeutil import IST
from research.metrics import equity_curve, standard_breakdowns, summarize
from research.regime import LABELS, RANGE, TREND_DOWN, TREND_UP, VOLATILE_CHOP

LABEL_TEXT = {TREND_UP: "Trend up", TREND_DOWN: "Trend down", RANGE: "Range", VOLATILE_CHOP: "Volatile chop",
              None: "Not ready"}
LABEL_CLASS = {TREND_UP: "up", TREND_DOWN: "down", RANGE: "range", VOLATILE_CHOP: "chop", None: "na"}


# ----------------------------------------------------------------- payload --

def trades_payload(trades: Sequence[Any]) -> Dict[str, Any]:
    return {
        "summary": summarize(trades),
        "breakdowns": standard_breakdowns(trades),
        "equity": equity_curve(trades),
        "trades": [t.to_dict() for t in trades],
    }


def regime_payload(features, sessions, regime_config, spotlight: Sequence[str]) -> Dict[str, Any]:
    ready = [r for r in features.readings if r.ready]
    share = Counter(r.label for r in ready)
    spot = []
    wanted = set(spotlight)
    for s in sessions:
        if s.session not in wanted:
            continue
        idx = [i for i, key in enumerate(features.sessions) if key == s.session]
        spot.append({
            "session": s.session,
            "dominant": s.dominant,
            "first_called_ist": s.first_called_ist,
            "confirmed_ist": s.confirmed_ist,
            "change_pct": s.change_pct,
            "bars": [{"t": features.moments[i].strftime("%H:%M"), "label": features.readings[i].label,
                      "close": features.closes[i], "reason": features.readings[i].reason,
                      "adx": features.readings[i].adx, "slope": features.readings[i].ema_slope_atr,
                      "vwap_dist": features.readings[i].vwap_distance_atr,
                      "vol_ratio": features.readings[i].vol_ratio} for i in idx],
        })
    config = dict(regime_config.__dict__)
    config["session_open_ist"] = regime_config.session_open_ist.strftime("%H:%M")
    return {
        "config": config,
        "ready_bars": len(ready),
        "bars": len(features.readings),
        "label_share": {label: share.get(label, 0) for label in LABELS},
        "dominant_days": dict(Counter(s.dominant for s in sessions)),
        "sessions": [s.to_dict() for s in sessions],
        "spotlight": spot,
    }


def write_json(path: str, payload: Dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2, default=str)


# -------------------------------------------------------------- formatting --

def esc(value: Any) -> str:
    return html.escape("" if value is None else str(value))


def rupees(value: Optional[float], signed: bool = True) -> str:
    """Indian digit grouping: 1,23,456."""
    if value is None:
        return "–"
    negative = value < 0
    whole = f"{abs(value):.0f}"
    if len(whole) > 3:
        head, tail = whole[:-3], whole[-3:]
        groups = []
        while len(head) > 2:
            groups.insert(0, head[-2:])
            head = head[:-2]
        if head:
            groups.insert(0, head)
        whole = ",".join(groups) + "," + tail
    sign = "−" if negative else ("+" if signed and value > 0 else "")
    return f"{sign}₹{whole}"


def num(value: Optional[float], digits: int = 2, signed: bool = False) -> str:
    if value is None:
        return "–"
    text = f"{value:+.{digits}f}" if signed else f"{value:.{digits}f}"
    return text.replace("-", "−")


def pct(value: Optional[float]) -> str:
    return "–" if value is None else f"{value * 100:.0f}%"


# ------------------------------------------------------------------- charts --

def equity_svg(series: List[Dict[str, Any]]) -> str:
    """
    Closed-trade equity. `series`: [{"name", "points": [{"at", "equity"}], "role": "primary"|"reference"}].
    One y-axis in rupees, a zero line (the "no trade" outcome), native hover titles.
    """
    lines = [s for s in series if s["points"]]
    if not lines:
        return '<p class="muted">No closed trades to plot.</p>'
    width, height, left, right, top, bottom = 720, 240, 64, 16, 14, 28
    stamps = sorted({p["at"] for s in lines for p in s["points"]})
    first_day = datetime.fromisoformat(stamps[0].replace("Z", "+00:00"))
    last_day = datetime.fromisoformat(stamps[-1].replace("Z", "+00:00"))
    span = max((last_day - first_day).total_seconds(), 1.0)
    values = [0.0] + [p["equity"] for s in lines for p in s["points"]]
    step = _nice_step((max(values) - min(values)) / 4 or 1000.0)
    lo = math.floor(min(values) / step) * step
    hi = math.ceil(max(values) / step) * step
    if hi == lo:
        hi = lo + step

    def x(at: str) -> float:
        moment = datetime.fromisoformat(at.replace("Z", "+00:00"))
        return left + (moment - first_day).total_seconds() / span * (width - left - right)

    def y(value: float) -> float:
        return top + (hi - value) / (hi - lo) * (height - top - bottom)

    parts = [f'<svg class="chart" viewBox="0 0 {width} {height}" role="img" aria-label="Equity curve">']
    ticks = int(round((hi - lo) / step))
    for k in range(ticks + 1):
        value = lo + step * k
        parts.append(f'<line class="grid" x1="{left}" x2="{width - right}" y1="{y(value):.1f}" y2="{y(value):.1f}"/>')
        parts.append(f'<text class="tick" x="{left - 6}" y="{y(value) + 4:.1f}" text-anchor="end">'
                     f'{esc(rupees(value, signed=False))}</text>')
    parts.append(f'<line class="zero" x1="{left}" x2="{width - right}" y1="{y(0):.1f}" y2="{y(0):.1f}"/>')
    parts.append(f'<text class="tick" x="{width - right}" y="{y(0) - 5:.1f}" text-anchor="end">no trade = ₹0</text>')
    for k in range(4):
        moment = first_day + timedelta(seconds=span * k / 3)
        parts.append(f'<text class="tick" x="{left + (width - left - right) * k / 3:.1f}" y="{height - 8}" '
                     f'text-anchor="{"start" if k == 0 else ("end" if k == 3 else "middle")}">'
                     f'{moment.astimezone(IST).strftime("%d %b")}</text>')
    for s in lines:
        cls = "line-primary" if s.get("role") == "primary" else "line-reference"
        pts = [(x(p["at"]), y(p["equity"])) for p in s["points"]]
        pts = [(left, y(0))] + pts
        d = " ".join(f"{'M' if i == 0 else 'L'}{px:.1f},{py:.1f}" for i, (px, py) in enumerate(pts))
        parts.append(f'<path class="{cls}" d="{d}"/>')
        for p in s["points"]:
            label = f'{s["name"]} · {datetime.fromisoformat(p["at"].replace("Z", "+00:00")).astimezone(IST):%d %b %H:%M} · {rupees(p["equity"])}'
            parts.append(f'<circle class="hit" cx="{x(p["at"]):.1f}" cy="{y(p["equity"]):.1f}" r="6">'
                         f'<title>{esc(label)}</title></circle>')
    parts.append("</svg>")
    legend = "".join(f'<span class="key"><i class="sw {("line-primary" if s.get("role") == "primary" else "line-reference")}">'
                     f'</i>{esc(s["name"])} {esc(rupees(s["points"][-1]["equity"]))}</span>' for s in lines)
    return f'<div class="legend">{legend}</div>' + "".join(parts)


def _nice_step(raw: float) -> float:
    """1, 2, 2.5 or 5 times a power of ten, at least `raw`."""
    power = 10 ** math.floor(math.log10(raw))
    for multiple in (1, 2, 2.5, 5, 10):
        if multiple * power >= raw:
            return multiple * power
    return 10 * power


def regime_strip(day: Dict[str, Any]) -> str:
    """One session: close sparkline above a strip of per-bar regime cells."""
    bars = day["bars"]
    if not bars:
        return ""
    cell, gap = 8, 2
    width = len(bars) * (cell + gap)
    spark_h, strip_h = 44, 16
    closes = [b["close"] for b in bars]
    lo, hi = min(closes), max(closes)
    rng = (hi - lo) or 1.0
    pts = " ".join(f"{k * (cell + gap) + cell / 2:.1f},{spark_h - 4 - (c - lo) / rng * (spark_h - 8):.1f}"
                   for k, c in enumerate(closes))
    parts = [f'<svg class="strip" viewBox="0 0 {width} {spark_h + strip_h + 4}" preserveAspectRatio="none" '
             f'role="img" aria-label="Regime by bar for {esc(day["session"])}">',
             f'<polyline class="spark" points="{pts}"/>']
    for k, b in enumerate(bars):
        tip = (f'{b["t"]} {LABEL_TEXT[b["label"]]} · close {b["close"]:.1f} · ADX {num(b["adx"], 0)} · '
               f'slope {num(b["slope"], 2)} ATR · VWAP {num(b["vwap_dist"], 2)} ATR · vol {num(b["vol_ratio"], 2)}x')
        parts.append(f'<rect class="cell {LABEL_CLASS[b["label"]]}" x="{k * (cell + gap)}" y="{spark_h + 4}" '
                     f'width="{cell}" height="{strip_h}" rx="2"><title>{esc(tip)}</title></rect>')
    parts.append("</svg>")
    return "".join(parts)


# ------------------------------------------------------------------- tables --

def summary_table(columns: List[tuple]) -> str:
    """columns: [(heading, summary dict or None)]"""
    rows = [
        ("Trades", lambda s: str(s["trades"])),
        ("Win rate", lambda s: pct(s["win_rate"])),
        ("Average win", lambda s: rupees(s["avg_win"])),
        ("Average loss", lambda s: rupees(s["avg_loss"])),
        ("Expectancy / trade", lambda s: rupees(s["expectancy"])),
        ("Expectancy / trade (R)", lambda s: num(s["expectancy_r"], 2, signed=True)),
        ("Profit factor", lambda s: num(s["profit_factor"], 2)),
        ("Net after costs", lambda s: rupees(s["net"])),
        ("Charges paid", lambda s: rupees(s["charges"], signed=False)),
        ("Max drawdown", lambda s: rupees(-s["max_drawdown"]) if s["max_drawdown"] else "₹0"),
        ("Longest losing streak", lambda s: str(s["longest_losing_streak"])),
        ("Stale exits", lambda s: str(s["stale_exits"])),
    ]
    head = "".join(f"<th>{esc(h)}</th>" for h, _ in columns)
    body = []
    for name, fn in rows:
        cells = "".join(f"<td>{esc(fn(s)) if s else '–'}</td>" for _, s in columns)
        body.append(f"<tr><th>{esc(name)}</th>{cells}</tr>")
    return f'<div class="scroll"><table class="num"><thead><tr><th></th>{head}</tr></thead><tbody>{"".join(body)}</tbody></table></div>'


def breakdown_table(title: str, rows: List[Dict[str, Any]]) -> str:
    if not rows:
        return ""
    body = "".join(
        f"<tr><th>{esc(LABEL_TEXT.get(r['group'], r['group']))}</th><td>{r['trades']}</td><td>{pct(r['win_rate'])}</td>"
        f"<td>{esc(rupees(r['expectancy']))}</td><td>{esc(num(r['expectancy_r'], 2, signed=True))}</td>"
        f"<td>{esc(rupees(r['net']))}</td></tr>" for r in rows)
    return (f'<div class="card"><h4>{esc(title)}</h4><div class="scroll"><table class="num"><thead><tr><th></th>'
            f"<th>Trades</th><th>Win</th><th>Exp ₹</th><th>Exp R</th><th>Net</th></tr></thead><tbody>{body}"
            "</tbody></table></div></div>")


def trades_table(trades: List[Dict[str, Any]]) -> str:
    if not trades:
        return '<p class="muted">No trades.</p>'
    body = "".join(
        f"<tr><td>{esc(t['session'])}</td><td>{esc(t['signal_ist'])}</td><td>{esc(t['side'])} {t['strike']:.0f}</td>"
        f"<td>{esc(LABEL_TEXT.get(t['regime_at_signal'], t['regime_at_signal']))}</td>"
        f"<td>{t['entry_fill']:.2f}</td><td>{t['exit_fill']:.2f}</td><td>{esc(t['exit_ist'])}</td>"
        f"<td>{esc(t['exit_reason'])}{' ⚠' if t['stale_exit'] else ''}</td><td>{esc(rupees(t['net_pnl']))}</td>"
        f"<td>{esc(num(t['r_multiple'], 2, signed=True))}</td></tr>" for t in trades)
    return ('<div class="scroll tall"><table class="num"><thead><tr><th>Session</th><th>Signal</th><th>Contract</th>'
            "<th>Regime</th><th>Entry</th><th>Exit</th><th>Exit at</th><th>Reason</th><th>Net</th><th>R</th></tr>"
            f"</thead><tbody>{body}</tbody></table></div>")


def label_chip(label: Optional[str]) -> str:
    return f'<span class="chip {LABEL_CLASS.get(label, "na")}"><i></i>{esc(LABEL_TEXT.get(label, label))}</span>'


def sessions_table(sessions: List[Dict[str, Any]]) -> str:
    body = []
    for s in reversed(sessions):
        c = s["counts"]
        body.append(
            f"<tr><td>{esc(s['session'])}</td><td>{label_chip(s['dominant'])}</td>"
            f"<td>{esc(s['first_called_ist'] or '–')}</td><td>{esc(s['confirmed_ist'] or '–')}</td>"
            f"<td>{c[TREND_UP]}</td><td>{c[TREND_DOWN]}</td><td>{c[RANGE]}</td><td>{c[VOLATILE_CHOP]}</td>"
            f"<td>{s['bars']}</td><td>{esc(num(s['change_pct'], 2, signed=True))}%</td>"
            f"<td>{s['high'] - s['low']:.0f}</td></tr>")
    return ('<div class="scroll tall"><table class="num"><thead><tr><th>Session</th><th>Dominant</th>'
            "<th>First called</th><th>Confirmed</th><th>Up bars</th><th>Down bars</th><th>Range bars</th>"
            "<th>Chop bars</th><th>Bars</th><th>Open→last bar</th><th>Day range</th></tr></thead>"
            f"<tbody>{''.join(body)}</tbody></table></div>")


# --------------------------------------------------------------------- page --

CSS = """
:root{--bg:#f9f9f7;--surface:#fcfcfb;--ink:#0b0b0b;--ink2:#52514e;--muted:#6f6d68;--grid:#e1e0d9;--axis:#c3c2b7;
--border:rgba(11,11,11,.10);--primary:#2a78d6;--reference:#898781;--up:#1baf7a;--down:#eb6834;--chop:#4a3aa7;
--range:#d6d5ce;--na:#efeee9;--warn-bg:#fff4dc;--warn-ink:#6b4a00}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#0d0d0d;--surface:#1a1a19;--ink:#fff;
--ink2:#c3c2b7;--muted:#9a9890;--grid:#2c2c2a;--axis:#383835;--border:rgba(255,255,255,.10);--primary:#3987e5;
--reference:#898781;--up:#199e70;--down:#d95926;--chop:#9085e9;--range:#4a4a46;--na:#262624;--warn-bg:#2e2410;--warn-ink:#f3c969}}
:root[data-theme="dark"]{--bg:#0d0d0d;--surface:#1a1a19;--ink:#fff;--ink2:#c3c2b7;--muted:#9a9890;--grid:#2c2c2a;
--axis:#383835;--border:rgba(255,255,255,.10);--primary:#3987e5;--reference:#898781;--up:#199e70;--down:#d95926;
--chop:#9085e9;--range:#4a4a46;--na:#262624;--warn-bg:#2e2410;--warn-ink:#f3c969}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:1040px;margin:0 auto;padding:28px 20px 60px}
h1{font-size:22px;margin:0 0 4px;font-weight:600}
h2{font-size:16px;margin:34px 0 10px;font-weight:600}
h3{font-size:14px;margin:18px 0 8px;font-weight:600}
h4{font-size:13px;margin:0 0 6px;font-weight:600;color:var(--ink2)}
p{margin:6px 0}
.muted{color:var(--muted)}
.meta{color:var(--ink2);font-size:13px}
.banner{border:1px solid var(--border);border-radius:8px;padding:10px 14px;margin:16px 0;background:var(--surface)}
.banner.warn{background:var(--warn-bg);color:var(--warn-ink)}
.card{background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:12px 14px}
.grid2{display:grid;grid-template-columns:repeat(auto-fit,minmax(380px,1fr));gap:12px}
.scroll{overflow-x:auto}
.scroll.tall{max-height:460px;overflow-y:auto}
table{border-collapse:collapse;width:100%;font-size:13px}
th,td{padding:5px 8px;border-bottom:1px solid var(--grid);text-align:left;white-space:nowrap}
thead th{color:var(--muted);font-weight:500;position:sticky;top:0;background:var(--surface)}
table.num td{font-variant-numeric:tabular-nums}
tbody th{font-weight:500;color:var(--ink2)}
dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:0}
dt{color:var(--muted)}dd{margin:0}
code{font:12px ui-monospace,SFMono-Regular,Menlo,monospace}
.chart{width:100%;height:auto;display:block}
.chart .grid{stroke:var(--grid);stroke-width:1}
.chart .zero{stroke:var(--axis);stroke-width:1;stroke-dasharray:4 3}
.chart .tick{fill:var(--muted);font-size:11px}
.chart .line-primary{fill:none;stroke:var(--primary);stroke-width:2;stroke-linejoin:round}
.chart .line-reference{fill:none;stroke:var(--reference);stroke-width:2;stroke-dasharray:6 4;stroke-linejoin:round}
.chart .hit{fill:transparent}
.chart .hit:hover{fill:var(--ink);fill-opacity:.25}
.legend{display:flex;flex-wrap:wrap;gap:6px 18px;margin:4px 0 6px;font-size:13px;color:var(--ink2)}
.key{display:inline-flex;align-items:center;gap:6px}
.sw{display:inline-block;width:18px;height:0;border-top:2px solid var(--primary)}
.sw.line-reference{border-top:2px dashed var(--reference)}
.chip{display:inline-flex;align-items:center;gap:6px}
.chip i{display:inline-block;width:10px;height:10px;border-radius:2px;background:var(--na)}
.chip.up i{background:var(--up)}.chip.down i{background:var(--down)}.chip.range i{background:var(--range)}
.chip.chop i{background:var(--chop)}
.day{margin:14px 0 4px}
.day header{display:flex;flex-wrap:wrap;gap:4px 14px;align-items:baseline;font-size:13px}
.strip{width:100%;height:72px;display:block}
.strip .spark{fill:none;stroke:var(--ink2);stroke-width:1.5;vector-effect:non-scaling-stroke}
.strip .cell{fill:var(--na)}.strip .cell.up{fill:var(--up)}.strip .cell.down{fill:var(--down)}
.strip .cell.range{fill:var(--range)}.strip .cell.chop{fill:var(--chop)}
.axis-times{display:flex;justify-content:space-between;font-size:11px;color:var(--muted)}
ul.notes{margin:6px 0;padding-left:18px}ul.notes li{margin:3px 0}
details summary{cursor:pointer;color:var(--ink2);margin:8px 0}
"""


def render_html(payload: Dict[str, Any]) -> str:
    cand = payload.get("candidate")
    title = cand["title"] if cand else f"Regime study · {payload['underlying']}"
    out: List[str] = ["<!doctype html>", '<html lang="en"><head><meta charset="utf-8">',
                      '<meta name="viewport" content="width=device-width, initial-scale=1">',
                      f"<title>{esc((cand['name'] if cand else 'regime') + ' · ' + payload['underlying'])} research</title>",
                      f"<style>{CSS}</style>", "</head><body><main>"]
    idx = payload["index"]
    out.append(f"<h1>{esc(title)}</h1>")
    out.append(f'<p class="meta">{esc(payload["underlying"])} · {esc(idx["symbol"])} {idx["resolution_minutes"]}m · '
               f'{idx["sessions"]} sessions, {esc(idx["first"])} to {esc(idx["last"])} · generated '
               f'{esc(payload["generated_ist"])} IST · local research file, not published</p>')

    status = payload["status"]
    warn = status["code"] != "complete"
    out.append(f'<div class="banner{" warn" if warn else ""}"><strong>{esc(status["headline"])}</strong>'
               f'<br>{esc(status["detail"])}</div>')

    if cand:
        out.append("<h2>Candidate</h2><div class=\"card\">")
        out.append(f"<p>{esc(cand['rules'])}</p><dl>")
        for key, value in cand["defaults"].items():
            out.append(f"<dt><code>{esc(key)}</code> = {esc(value)}</dt><dd>{esc(cand['parameter_notes'][key])}</dd>")
        out.append(f"<dt>Strike</dt><dd>ATM{cand['strike_offset']:+d} at entry, held fixed</dd>")
        if cand["grid"]:
            grid = "; ".join(f"{k} ∈ {v}" for k, v in cand["grid"].items())
            out.append(f"<dt>Train-window grid</dt><dd>{esc(grid)}</dd>")
        out.append("</dl></div>")

    wf = payload.get("walk_forward")
    if wf:
        out.append("<h2>Out-of-sample result (walk-forward test windows only)</h2>")
        columns = [(cand["name"], wf["oos"]["summary"])]
        if wf.get("baseline"):
            columns.append((wf["baseline"]["name"] + " (defaults)", wf["baseline"]["summary"]))
        columns.append(("No trade", summarize([])))
        out.append(summary_table(columns))
        series = [{"name": cand["name"], "points": wf["oos"]["equity"], "role": "primary"}]
        if wf.get("baseline"):
            series.append({"name": wf["baseline"]["name"], "points": wf["baseline"]["equity"], "role": "reference"})
        out.append('<h3>Equity after costs, test windows joined</h3><div class="card">' + equity_svg(series) + "</div>")
        out.append("<h3>Folds</h3>")
        fold_rows = "".join(
            f"<tr><td>{esc(f['window']['train'][0])} → {esc(f['window']['train'][1])}</td>"
            f"<td>{esc(f['window']['test'][0])} → {esc(f['window']['test'][1])}{' (partial)' if f['window']['partial_test'] else ''}</td>"
            f"<td><code>{esc(', '.join(f'{k}={v}' for k, v in f['chosen_params'].items()))}</code><br>"
            f"<span class=\"muted\">{esc(f['choice_reason'])}</span></td>"
            f"<td>{f['train']['trades']} · {esc(rupees(f['train']['net']))}</td>"
            f"<td>{f['test']['trades']} · {esc(rupees(f['test']['net']))}</td>"
            f"<td>{esc(rupees(f['baseline_test']['net'])) if f.get('baseline_test') else '–'}</td></tr>"
            for f in wf["folds"])
        out.append('<div class="scroll"><table class="num"><thead><tr><th>Train</th><th>Test</th><th>Chosen on train</th>'
                   f"<th>Train trades · net</th><th>Test trades · net</th><th>Baseline test net</th></tr></thead><tbody>{fold_rows}</tbody></table></div>")
        b = wf["oos"]["breakdowns"]
        out.append("<h3>Breakdowns (out-of-sample)</h3><div class=\"grid2\">")
        out.append(breakdown_table("Regime at signal", b["regime"]))
        out.append(breakdown_table("Time of day (signal)", b["time_of_day"]))
        out.append(breakdown_table("Weekday", b["weekday"]))
        out.append(breakdown_table("Month", b["month"]))
        out.append(breakdown_table("Side", b["side"]))
        out.append(breakdown_table("Exit reason", b["exit_reason"]))
        out.append("</div>")
        out.append("<details><summary>All out-of-sample trades</summary>" + trades_table(wf["oos"]["trades"]) + "</details>")

    ins = payload.get("in_sample")
    if ins:
        out.append("<h2>In-sample, defaults over the whole period</h2>")
        out.append('<p class="muted">Every session, default parameters, no selection. Shown for context only: it is '
                   "not evidence, because the defaults were written knowing this market.</p>")
        out.append(summary_table([(cand["name"], ins["summary"])]))
        out.append("<details><summary>In-sample trades</summary>" + trades_table(ins["trades"]) + "</details>")

    if payload.get("skipped"):
        counts = Counter(s["reason"] for s in payload["skipped"])
        items = "".join(f"<li>{n} × {esc(reason)}</li>" for reason, n in counts.most_common())
        out.append(f"<h3>Entries not taken</h3><ul class=\"notes\">{items}</ul>")

    census = payload.get("census")
    if census:
        out.append("<h2>Signal census (no premiums)</h2>")
        out.append('<p class="muted">Where the candidate would have signalled, and in which regime. The last column is '
                   "the index move from the fill bar to 15:15 in the signal's direction: a direction check only. "
                   "It ignores stops, theta, IV and costs, and it is not option P&amp;L.</p>")
        rows = census["rows"]
        by_regime = Counter(r["regime"] for r in rows)
        against = sum(1 for r in rows if r["against_regime"])
        out.append(f"<p>{len(rows)} signals · {against} against a called trend · "
                   + " · ".join(f"{esc(LABEL_TEXT.get(k, k))} {v}" for k, v in by_regime.most_common()) + "</p>")
        body = "".join(
            f"<tr><td>{esc(r['session'])}</td><td>{esc(r['signal_ist'])}</td><td>{esc(r['side'])}</td>"
            f"<td>{label_chip(r['regime'])}</td><td>{'yes' if r['against_regime'] else ''}</td>"
            f"<td>{esc(num(r['index_points_to_exit'], 0, signed=True))}</td><td>{esc(r['reason'])}</td></tr>"
            for r in reversed(rows))
        out.append('<div class="scroll tall"><table class="num"><thead><tr><th>Session</th><th>Signal</th><th>Side</th>'
                   "<th>Regime</th><th>Against trend</th><th>Index pts to 15:15</th><th>Reason</th></tr></thead>"
                   f"<tbody>{body}</tbody></table></div>")

    reg = payload["regime"]
    out.append("<h2>Market regime</h2>")
    share = reg["label_share"]
    total = sum(share.values()) or 1
    out.append('<div class="legend">' + "".join(
        f'<span class="key">{label_chip(label)} {share[label] / total * 100:.0f}% of ready bars</span>' for label in LABELS)
        + f'<span class="key">{label_chip(None)} warming up</span></div>')
    days = Counter(reg["dominant_days"])
    out.append("<p class=\"muted\">Dominant regime by session: " + " · ".join(
        f"{esc(LABEL_TEXT.get(k if k != 'None' else None, k))} {v}" for k, v in days.most_common()) + "</p>")
    for day in reversed(reg["spotlight"]):
        out.append(f'<div class="day card"><header><strong>{esc(day["session"])}</strong>{label_chip(day["dominant"])}'
                   f'<span class="muted">first called {esc(day["first_called_ist"] or "–")} · confirmed '
                   f'{esc(day["confirmed_ist"] or "–")} · open→last bar {esc(num(day["change_pct"], 2, signed=True))}%</span>'
                   f"</header>{regime_strip(day)}"
                   f'<div class="axis-times"><span>{esc(day["bars"][0]["t"] if day["bars"] else "")}</span>'
                   f'<span>{esc(day["bars"][-1]["t"] if day["bars"] else "")}</span></div></div>')
    out.append("<h3>Every session</h3>")
    out.append('<p class="muted">Times are bar close (when the call was known). Open→last bar and day range are '
               "hindsight columns for checking the labels; the classifier never reads them.</p>")
    out.append(sessions_table(reg["sessions"]))

    out.append("<h2>Method</h2><ul class=\"notes\">")
    for note in payload["method"]:
        out.append(f"<li>{esc(note)}</li>")
    out.append("</ul>")
    if payload.get("notes"):
        out.append("<h3>Data notes</h3><ul class=\"notes\">")
        for note in payload["notes"]:
            out.append(f"<li>{esc(note)}</li>")
        out.append("</ul>")
    rc = reg["config"]
    out.append("<details><summary>Regime thresholds</summary><dl>" + "".join(
        f"<dt><code>{esc(k)}</code></dt><dd>{esc(v)}</dd>" for k, v in rc.items()) + "</dl></details>")
    out.append("</main></body></html>")
    return "\n".join(out)


def method_notes(sim, rc) -> List[str]:
    """The run's rules in plain words, built from the settings actually used."""
    c = sim.costs
    return [
        f"Regime: five checks vote up/down - ADX({rc.adx_period}) >= {rc.adx_trend:g} with +DI/-DI, EMA({rc.ema_period}) "
        f"slope over {rc.slope_lookback} bars >= {rc.slope_min_atr:g} ATR, close >= {rc.vwap_min_atr:g} ATR from session "
        f"VWAP, >= {rc.side_fraction_min * 100:.0f}% of session closes on one side of VWAP (after {rc.side_min_bars} bars), "
        f"a close beyond the {rc.opening_range_minutes}-minute opening range. Trend when >= {rc.trend_votes} agree and none "
        f"disagrees; otherwise VOLATILE_CHOP when ATR% >= {rc.vol_expanding:g}x its median at the same time of day over "
        f"the last {rc.vol_lookback_sessions} finished sessions; otherwise RANGE. No label until the indicators, the "
        f"opening range and a {rc.vol_min_sessions}-session volatility baseline exist.",
        "Index VWAP is time-weighted (equal weight per bar): an index has no traded volume of its own.",
        "No look-ahead: every reading uses only bars at or before it; tested by appending and rewriting future bars.",
        "Fills: signal at a bar's close, entry at the next bar's open from option_history_bars at the candidate's "
        "offset. Premiums are never estimated from the index; a missing premium is a skipped entry.",
        "Fixed strike: the strike (and expiry) chosen at entry is followed by Strike across the rolling-ATM rows. If "
        "it drifts beyond the stored offset window its bars are unpriced: no resting exit is checked, a decided exit "
        "waits for the next priced bar, and if the session ends first it exits at the last known premium, flagged "
        "stale.",
        "Resting stop/trail/target fill at their level inside a bar (at the open if gapped through; stop before "
        "target when both are touched). Index ATR stop, time stop, regime flip and the forced exit fill at the next "
        "bar's open.",
        f"Costs: slippage {c.slippage_pct:g}% of premium (min Rs {c.min_slippage:g}) per fill; Rs {c.brokerage_per_order:g} "
        f"brokerage per order; STT {c.stt_sell_pct:g}% on sell premium; exchange {c.exchange_txn_pct:g}% and SEBI "
        f"{c.sebi_fee_pct:g}% on turnover; stamp {c.stamp_buy_pct:g}% on buys; {c.gst_pct:g}% GST on brokerage + "
        "exchange + SEBI. Rates approximate 2026 NSE index options; verify against a contract note.",
        "Walk-forward: rolling calendar windows, parameters chosen by train-window net Rs (minimum trade count, else "
        "defaults), judged on the following test window. Only test windows are out-of-sample. The baseline runs with "
        "defaults on the same test windows; 'no trade' is Rs 0.",
        f"{sim.lots} lot x {sim.lot_size}; one position at a time, at most {sim.max_entries_per_session} entry signals a "
        f"session, no fill at or after {sim.last_entry_ist:%H:%M}, a ready regime reading required. R = premium stop "
        f"(or {sim.risk_unit_pct:g}% of entry when a candidate has none) x quantity.",
    ]
