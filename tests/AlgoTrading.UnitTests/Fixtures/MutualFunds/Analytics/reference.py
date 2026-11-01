#!/usr/bin/env python3
"""Independent reference calculation for the mutual fund analytics metrics.

This script is the "second implementation" of the metric definitions in docs/modules/mutual_funds_module.md
("How each figure is computed") for the fund analyst report
(trailing returns, calendar years, rolling 3-year returns, SIP + XIRR, lump sum, volatility with
Sharpe / Sortino, drawdowns, benchmark excess / beta / tracking error / capture ratios, and direct
versus regular). It was written from the written spec only, without looking at the C# code, so that
its numbers can serve as expected values in the C# unit tests.

Inputs (all in this folder, oldest first):
  nav_<scheme code>.csv   header "date,nav"    - NAVs from mfapi.in, trimmed to 2016-09-01..2026-10-30
  tri_<slug>.csv          header "date,value"  - NIFTY total return index values from niftyindices.com

Output: expected.json in this folder (full-precision floats, nothing rounded), plus a short summary on
stdout. Run:  python3 reference.py

Standard library only. Plain float (IEEE double) arithmetic throughout.
"""

import bisect
import csv
import datetime as dt
import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))

AS_OF = dt.date(2026, 10, 30)   # the requested as-of date
RISK_FREE = 0.0573              # 5.73 % a year, used for Sharpe and Sortino
TOLERANCE_DAYS = 7              # a looked-up point may be at most 7 calendar days from the date asked for
PERIODS = (1, 3, 5, 10)         # trailing / SIP / lump-sum / excess periods, in years
ROLLING_YEARS = 3
SIP_AMOUNT = 10000.0
LUMP_SUM = 200000.0

# The six schemes. Names are the mfapi.in meta.scheme_name values (checked when the data was fetched).
FUNDS = [
    {"code": "120716", "name": "UTI Nifty 50 Index Fund - Direct Plan - Growth",
     "benchmark": "NIFTY 50", "tri": "nifty50", "indexFund": True},
    {"code": "100822", "name": "UTI Nifty 50 Index Fund - Regular Plan - Growth",
     "benchmark": "NIFTY 50", "tri": "nifty50", "indexFund": True},
    {"code": "118825", "name": "Mirae Asset Large Cap Fund - Direct Plan - Growth",
     "benchmark": "NIFTY 100", "tri": "nifty100", "indexFund": False},
    {"code": "118778", "name": "Nippon India Small Cap Fund - Direct Plan - Growth Option",
     "benchmark": "NIFTY SMALLCAP 250", "tri": "niftysmallcap250", "indexFund": False},
    {"code": "119091", "name": "HDFC Liquid Fund - Direct Plan - Growth Option",
     "benchmark": None, "tri": None, "indexFund": False},
    {"code": "143341", "name": "UTI - Nifty Next 50 Index Fund - Direct Plan - Growth",
     "benchmark": "NIFTY NEXT 50", "tri": "niftynext50", "indexFund": True},
]

# (direct code, regular code) pairs for section 9.
DIRECT_VS_REGULAR = [("120716", "100822")]


# ---------------------------------------------------------------------------------------------------
# Date helpers
# ---------------------------------------------------------------------------------------------------

def add_years(d, n):
    """Same month and day n years away; 29 Feb becomes 28 Feb in a non-leap year."""
    y = d.year + n
    try:
        return dt.date(y, d.month, d.day)
    except ValueError:  # only 29 Feb can fail
        return dt.date(y, 2, 28)


def days_in_month(y, m):
    nxt = dt.date(y + (m == 12), m % 12 + 1, 1)
    return (nxt - dt.date(y, m, 1)).days


def add_months(d, k):
    """Same day k months away; a day past the target month's end becomes that month's last day."""
    total = d.year * 12 + (d.month - 1) + k
    y, m0 = divmod(total, 12)
    m = m0 + 1
    return dt.date(y, m, min(d.day, days_in_month(y, m)))


def days(a, b):
    """Calendar days from a to b (b - a)."""
    return (b - a).days


def iso(d):
    return d.isoformat() if d is not None else None


# ---------------------------------------------------------------------------------------------------
# Series
# ---------------------------------------------------------------------------------------------------

class Series:
    """A date-sorted series with one value per date (dates strictly increasing, values > 0)."""

    def __init__(self, points):
        self.dates = [p[0] for p in points]
        self.values = [p[1] for p in points]
        self.index = {d: i for i, d in enumerate(self.dates)}
        for a, b in zip(self.dates, self.dates[1:]):
            assert a < b, "series must be strictly increasing in date"
        assert all(v > 0 for v in self.values), "values must be > 0"

    def __len__(self):
        return len(self.dates)

    def cut(self, end):
        """Everything after `end` dropped."""
        n = bisect.bisect_right(self.dates, end)
        return Series(list(zip(self.dates[:n], self.values[:n])))

    def on_or_after(self, d, last_idx):
        """Index of the first point with date >= d and index <= last_idx (i.e. date <= E); None if none."""
        i = bisect.bisect_left(self.dates, d)
        return i if i <= last_idx and i < len(self.dates) else None

    def on_or_before(self, d):
        """Index of the last point with date <= d; None if none."""
        i = bisect.bisect_right(self.dates, d) - 1
        return i if i >= 0 else None

    def after_within(self, d, last_idx):
        """OnOrAfter(d) usable only if its date - d <= 7 days."""
        i = self.on_or_after(d, last_idx)
        if i is None or days(d, self.dates[i]) > TOLERANCE_DAYS:
            return None
        return i

    def before_within(self, d):
        """OnOrBefore(d) usable only if d - its date <= 7 days."""
        i = self.on_or_before(d)
        if i is None or days(self.dates[i], d) > TOLERANCE_DAYS:
            return None
        return i


def read_csv(path):
    with open(path, newline="") as f:
        rows = list(csv.reader(f))
    assert rows[0] in (["date", "nav"], ["date", "value"]), rows[0]
    return Series([(dt.date.fromisoformat(r[0]), float(r[1])) for r in rows[1:] if r])


# ---------------------------------------------------------------------------------------------------
# Small numeric helpers
# ---------------------------------------------------------------------------------------------------

def cagr(v0, d0, v1, d1):
    """(v1 / v0) ^ (365 / days(d1 - d0)) - 1 (365, not 365.25)."""
    return math.pow(v1 / v0, 365.0 / days(d0, d1)) - 1.0


def mean(xs):
    return sum(xs) / len(xs)


def sample_stdev(xs):
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def sample_cov(xs, ys):
    mx, my = mean(xs), mean(ys)
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / (len(xs) - 1)


def median(xs):
    s = sorted(xs)
    n = len(s)
    if n % 2:
        return s[n // 2]
    return (s[n // 2 - 1] + s[n // 2]) / 2.0


def xirr(flows):
    """Rate r with sum(a_k / (1 + r) ^ (days(d_k - d_0) / 365)) = 0, d_0 = the first flow's date.

    Solved by bisection on a sign-changing bracket, run until the bracket cannot shrink any further
    in double precision (far tighter than 1e-10). For a SIP (outflows, then one inflow at the end)
    the function is strictly decreasing in r, so the root is unique.
    """
    d0 = flows[0][0]
    ts = [(days(d0, d) / 365.0, a) for d, a in flows]

    def npv(r):
        return sum(a * math.pow(1.0 + r, -t) for t, a in ts)

    lo, hi = -0.99, 1.0
    f_lo, f_hi = npv(lo), npv(hi)
    while f_lo * f_hi > 0 and hi < 1e6:  # widen upwards until the sign changes
        hi = hi * 2.0 + 1.0
        f_hi = npv(hi)
    if f_lo * f_hi > 0:
        raise ValueError("XIRR: no sign change found")
    for _ in range(2000):
        mid = (lo + hi) / 2.0
        if mid <= lo or mid >= hi:
            break  # bracket is as narrow as doubles allow
        f_mid = npv(mid)
        if f_mid == 0.0:
            return mid
        if (f_mid > 0) == (f_lo > 0):
            lo, f_lo = mid, f_mid
        else:
            hi, f_hi = mid, f_mid
    # pick the endpoint whose NPV is closer to zero
    return lo if abs(f_lo) <= abs(f_hi) else hi


# ---------------------------------------------------------------------------------------------------
# Section 1: trailing return
# ---------------------------------------------------------------------------------------------------

def trailing(s, e_idx, n_years):
    """Trailing N-year return as of the point e_idx (which plays the role of E).

    Returns None when not covered (series starts after T) or when there is a gap at T;
    else a dict with the start index, CAGR and absolute return.
    """
    E = s.dates[e_idx]
    T = add_years(E, -n_years)
    if s.dates[0] > T:
        return None  # "not enough history"
    i = s.after_within(T, e_idx)
    if i is None:
        return None  # gap at T
    v0, v1 = s.values[i], s.values[e_idx]
    return {"startIdx": i, "start": s.dates[i],
            "cagr": cagr(v0, s.dates[i], v1, E),
            "absolute": v1 / v0 - 1.0}


# ---------------------------------------------------------------------------------------------------
# Section 2: calendar-year returns
# ---------------------------------------------------------------------------------------------------

def calendar_years(s, e_idx):
    E = s.dates[e_idx]
    out = []
    for y in range(s.dates[0].year, E.year + 1):
        a = s.before_within(dt.date(y - 1, 12, 31))  # must be on or after 24 Dec of y-1
        if a is None:
            continue
        if y < E.year:
            b = s.before_within(dt.date(y, 12, 31))
            if b is None:
                continue
            ytd = False
        else:
            b = e_idx
            ytd = True
        out.append({"year": y, "start": iso(s.dates[a]), "end": iso(s.dates[b]),
                    "return": s.values[b] / s.values[a] - 1.0, "ytd": ytd})
    return out


# ---------------------------------------------------------------------------------------------------
# Section 3: rolling 3-year returns
# ---------------------------------------------------------------------------------------------------

def rolling(s, e_idx, bench):
    windows = []  # (end date, fund return, fund start date, end idx)
    for e in range(e_idx + 1):
        tr = trailing(s, e, ROLLING_YEARS)
        if tr is not None:
            windows.append((s.dates[e], tr["cagr"], tr["start"]))
    if not windows:
        return None
    rets = [w[1] for w in windows]
    # lowest / highest: the first window that reaches the extreme (strictly lower / higher replaces)
    lo_i = hi_i = 0
    for i, r in enumerate(rets):
        if r < rets[lo_i]:
            lo_i = i
        if r > rets[hi_i]:
            hi_i = i
    result = {
        "windows": len(windows),
        "firstEnd": iso(windows[0][0]),
        "lastEnd": iso(windows[-1][0]),
        "min": rets[lo_i], "minEnd": iso(windows[lo_i][0]),
        "max": rets[hi_i], "maxEnd": iso(windows[hi_i][0]),
        "median": median(rets),
        "negativeShare": sum(1 for r in rets if r < 0) / len(rets),
        "benchmarkCompared": None,
        "benchmarkBeatShare": None,
    }
    if bench is not None:
        compared = beats = 0
        for e_date, f_ret, s_date in windows:
            be = bench.index.get(e_date)              # benchmark value dated exactly e
            bs = bench.before_within(s_date)          # OnOrBefore(S_e) within tolerance
            if be is None or bs is None:
                continue
            # exponent uses the fund's dates S_e..e
            b_ret = math.pow(bench.values[be] / bench.values[bs], 365.0 / days(s_date, e_date)) - 1.0
            compared += 1
            if f_ret > b_ret:
                beats += 1
        result["benchmarkCompared"] = compared
        result["benchmarkBeatShare"] = beats / compared if compared else None
    return result


# ---------------------------------------------------------------------------------------------------
# Sections 4 and 5: SIP and lump sum
# ---------------------------------------------------------------------------------------------------

def first_sip_month(E, n_years):
    """1st of the first instalment month: M - 1 months before E's month (M = 12 N)."""
    return add_months(dt.date(E.year, E.month, 1), -(12 * n_years - 1))


def sip(s, e_idx, n_years):
    E = s.dates[e_idx]
    M = 12 * n_years
    first = first_sip_month(E, n_years)
    if s.dates[0] > first:
        return None  # "not enough history"
    inst = []
    for k in range(M):
        i = s.after_within(add_months(first, k), e_idx)  # OnOrAfter(1st of month), <= E, within 7 days
        if i is None:
            return None  # an instalment cannot be placed
        inst.append(i)
    units = 0.0
    for i in inst:  # chronological order
        units += SIP_AMOUNT / s.values[i]
    value = units * s.values[e_idx]
    flows = [(s.dates[i], -SIP_AMOUNT) for i in inst] + [(E, value)]
    return {"firstDate": iso(s.dates[inst[0]]), "instalments": M,
            "invested": SIP_AMOUNT * M, "value": value, "xirr": xirr(flows)}


def lump_sum(s, e_idx, n_years):
    E = s.dates[e_idx]
    first = first_sip_month(E, n_years)
    if s.dates[0] > first:
        return None
    i = s.after_within(first, e_idx)  # the SIP's first instalment date
    if i is None:
        return None
    value = LUMP_SUM / s.values[i] * s.values[e_idx]
    return {"start": iso(s.dates[i]), "value": value, "cagr": cagr(LUMP_SUM, s.dates[i], value, E)}


# ---------------------------------------------------------------------------------------------------
# Section 6: volatility, Sharpe, Sortino
# ---------------------------------------------------------------------------------------------------

def volatility(s, e_idx, rf):
    E = s.dates[e_idx]
    F = s.dates[0]
    if F <= add_years(E, -3):
        n_years = 3
    elif F <= add_years(E, -1):
        n_years = 1
    else:
        return None
    a = s.after_within(add_years(E, -n_years), e_idx)
    if a is None:
        return None
    r = [s.values[i] / s.values[i - 1] - 1.0 for i in range(a + 1, e_idx + 1)]
    n = len(r)
    years = days(s.dates[a], E) / 365.25
    P = n / years
    sd = sample_stdev(r)
    rf_p = rf / P
    ex = [x - rf_p for x in r]
    m_ex = mean(ex)
    dd = math.sqrt(sum(min(0.0, x) ** 2 for x in ex) / n)
    return {"years": n_years, "start": iso(s.dates[a]), "returns": n, "perYear": P,
            "annualised": sd * math.sqrt(P),
            "sharpe": m_ex / sd * math.sqrt(P),
            "sortino": m_ex / dd * math.sqrt(P)}


# ---------------------------------------------------------------------------------------------------
# Section 7: drawdowns
# ---------------------------------------------------------------------------------------------------

def max_drawdown(s, a, e_idx):
    """Max drawdown over points a..e_idx inclusive."""
    peak_i = a
    best = best_peak = best_trough = None
    for i in range(a, e_idx + 1):
        if s.values[i] > s.values[peak_i]:  # new peak only when strictly higher
            peak_i = i
        dd = s.values[i] / s.values[peak_i] - 1.0
        if best is None or dd < best:       # strictly lower replaces
            best, best_peak, best_trough = dd, peak_i, i
    recovery = None
    for j in range(best_trough + 1, e_idx + 1):
        if s.values[j] >= s.values[best_peak]:
            recovery = j
            break
    return {"start": iso(s.dates[a]), "maxDrawdown": best,
            "peak": iso(s.dates[best_peak]), "trough": iso(s.dates[best_trough]),
            "recovery": iso(s.dates[recovery]) if recovery is not None else None}


def drawdowns(s, e_idx):
    E = s.dates[e_idx]
    out = {}
    for n in (3, 5, 10):
        T = add_years(E, -n)
        a = s.after_within(T, e_idx) if s.dates[0] <= T else None  # covered = trailing coverage
        out[str(n)] = max_drawdown(s, a, e_idx) if a is not None else None
    out["all"] = max_drawdown(s, 0, e_idx)
    return out


def current_drawdown(s, e_idx):
    hi = 0
    for i in range(e_idx + 1):
        if s.values[i] > s.values[hi]:  # first date the highest NAV was reached
            hi = i
    return {"value": s.values[e_idx] / s.values[hi] - 1.0, "peakDate": iso(s.dates[hi])}


# ---------------------------------------------------------------------------------------------------
# Section 8: against a benchmark TRI
# ---------------------------------------------------------------------------------------------------

def common_window_returns(fund, bench, common, a, b):
    """Returns between consecutive common dates common[a..b], for fund and benchmark."""
    f, r = [], []
    for k in range(a + 1, b + 1):
        d0, d1 = common[k - 1], common[k]
        f.append(fund.values[fund.index[d1]] / fund.values[fund.index[d0]] - 1.0)
        r.append(bench.values[bench.index[d1]] / bench.values[bench.index[d0]] - 1.0)
    return f, r


def first_common_within(common, target):
    """Index of the first common date >= target, usable only within 7 days; None otherwise."""
    k = bisect.bisect_left(common, target)
    if k >= len(common) or days(target, common[k]) > TOLERANCE_DAYS:
        return None
    return k


def benchmark_stats(s, e_idx, bench, index_fund):
    E = s.dates[e_idx]
    F = s.dates[0]

    # Period excess return ("tracking difference" for an index fund)
    excess, bench_cagr = {}, {}
    for n in PERIODS:
        excess[str(n)] = bench_cagr[str(n)] = None
        tr = trailing(s, e_idx, n)
        if tr is None:
            continue
        be = bench.index.get(E)               # benchmark value dated exactly E
        bs = bench.before_within(tr["start"])  # OnOrBefore(S) within tolerance
        if be is None or bs is None:
            continue
        bc = math.pow(bench.values[be] / bench.values[bs], 365.0 / days(tr["start"], E)) - 1.0
        bench_cagr[str(n)] = bc
        excess[str(n)] = tr["cagr"] - bc

    # Common dates up to E; E_c = the last one
    common = sorted(set(s.dates[:e_idx + 1]) & set(bench.dates))
    last = len(common) - 1
    Ec = common[last]
    B0 = bench.dates[0]

    # Beta: 3 years if both series start on or before AddYears(E_c,-3), else 1 year, else none
    beta = None
    if F <= add_years(Ec, -3) and B0 <= add_years(Ec, -3):
        n_beta = 3
    elif F <= add_years(Ec, -1) and B0 <= add_years(Ec, -1):
        n_beta = 1
    else:
        n_beta = None
    if n_beta is not None:
        a = first_common_within(common, add_years(Ec, -n_beta))
        if a is not None:
            f, b = common_window_returns(s, bench, common, a, last)
            beta = {"years": n_beta, "start": iso(common[a]), "end": iso(Ec), "returns": len(f),
                    "value": sample_cov(f, b) / sample_cov(b, b)}

    # Tracking error (index funds only): 1-year window from E_c
    te = None
    if index_fund:
        a = first_common_within(common, add_years(Ec, -1))
        if a is not None:
            f, b = common_window_returns(s, bench, common, a, last)
            d = [x - y for x, y in zip(f, b)]
            P = len(d) / (days(common[a], Ec) / 365.25)
            te = {"start": iso(common[a]), "end": iso(Ec), "returns": len(d),
                  "value": sample_stdev(d) * math.sqrt(P)}

    # Upside / downside capture over 36 monthly steps back from E_c
    down_cap = up_cap = None
    down_n = up_n = 0
    T3 = add_years(Ec, -3)
    if F <= T3 and B0 <= T3:
        points = [first_common_within(common, add_months(Ec, -k)) for k in range(37)]
        if all(p is not None for p in points):
            down, up = [], []
            for k in range(36):  # month between point_{k+1} (older) and point_k (newer)
                newer, older = common[points[k]], common[points[k + 1]]
                fm = s.values[s.index[newer]] / s.values[s.index[older]] - 1.0
                bm = bench.values[bench.index[newer]] / bench.values[bench.index[older]] - 1.0
                if bm < 0:
                    down.append((fm, bm))
                elif bm > 0:
                    up.append((fm, bm))
            down_n, up_n = len(down), len(up)
            down_cap = capture(down)
            up_cap = capture(up)

    return {"excess": excess, "benchmarkCagr": bench_cagr, "beta": beta, "trackingError": te,
            "downsideCapture": down_cap, "upsideCapture": up_cap,
            "downMonths": down_n, "upMonths": up_n}


def capture(months):
    """(prod(1+f))^(1/k) - 1 over (prod(1+b))^(1/k) - 1, times 100; needs at least 3 months."""
    k = len(months)
    if k < 3:
        return None
    pf = pb = 1.0
    for fm, bm in months:
        pf *= 1.0 + fm
        pb *= 1.0 + bm
    return (math.pow(pf, 1.0 / k) - 1.0) / (math.pow(pb, 1.0 / k) - 1.0) * 100.0


# ---------------------------------------------------------------------------------------------------
# Whole report for one fund
# ---------------------------------------------------------------------------------------------------

def fund_report(fund, series, bench):
    s = series.cut(AS_OF)          # everything after the as-of date is ignored
    e_idx = len(s) - 1             # E = the last date on or before the requested as-of date
    E = s.dates[e_idx]

    trailing_out = {}
    for n in PERIODS:
        tr = trailing(s, e_idx, n)
        trailing_out[str(n)] = (None if tr is None else
                                {"start": iso(tr["start"]), "cagr": tr["cagr"], "absolute": tr["absolute"]})

    return {
        "name": fund["name"],
        "benchmark": fund["benchmark"],
        "indexFund": fund["indexFund"],
        "firstDate": iso(s.dates[0]),
        "asOf": iso(E),
        "trailing": trailing_out,
        "calendarYears": calendar_years(s, e_idx),
        "rolling3y": rolling(s, e_idx, bench),
        "sip": {str(n): sip(s, e_idx, n) for n in PERIODS},
        "lumpSum": {str(n): lump_sum(s, e_idx, n) for n in PERIODS},
        "volatility": volatility(s, e_idx, RISK_FREE),
        "drawdowns": drawdowns(s, e_idx),
        "currentDrawdown": current_drawdown(s, e_idx),
        "benchmarkStats": None if bench is None else benchmark_stats(s, e_idx, bench, fund["indexFund"]),
    }


def direct_vs_regular(direct, regular):
    """Section 9: same SIP and lump sum on both plans, both as of the direct report's E."""
    d = direct.cut(AS_OF)
    E = d.dates[-1]
    r = regular.cut(E)  # the other plan's series is cut at E ...
    if len(r) == 0 or days(r.dates[-1], E) > TOLERANCE_DAYS:
        return None     # ... and must have a NAV within 7 days before E
    out = {"sip": {}, "lumpSum": {}}
    for n in PERIODS:
        for key, fn in (("sip", sip), ("lumpSum", lump_sum)):
            a = fn(d, len(d) - 1, n)
            b = fn(r, len(r) - 1, n)
            out[key][str(n)] = (None if a is None or b is None else
                                {"direct": a["value"], "regular": b["value"],
                                 "gap": a["value"] - b["value"]})
    return out


def main():
    series = {f["code"]: read_csv(os.path.join(HERE, "nav_%s.csv" % f["code"])) for f in FUNDS}
    tris = {}
    for f in FUNDS:
        if f["tri"] and f["tri"] not in tris:
            tris[f["tri"]] = read_csv(os.path.join(HERE, "tri_%s.csv" % f["tri"]))

    report = {"asOf": iso(AS_OF), "riskFree": RISK_FREE, "funds": {}, "directVsRegular": {}}
    for f in FUNDS:
        report["funds"][f["code"]] = fund_report(f, series[f["code"]], tris.get(f["tri"]))
    for dcode, rcode in DIRECT_VS_REGULAR:
        report["directVsRegular"]["%s/%s" % (dcode, rcode)] = direct_vs_regular(series[dcode], series[rcode])

    with open(os.path.join(HERE, "expected.json"), "w") as fh:
        json.dump(report, fh, indent=1, allow_nan=False)
        fh.write("\n")

    # Short human-readable summary
    def pct(x):
        return "n/a" if x is None else "%.2f%%" % (100 * x)

    for code, r in report["funds"].items():
        tr = r["trailing"]
        print("%s %s  as of %s" % (code, r["name"], r["asOf"]))
        print("  CAGR 1/3/5/10: %s" % " / ".join(pct(tr[k]["cagr"]) if tr[k] else "n/a" for k in ("1", "3", "5", "10")))
        s5, l5 = r["sip"]["5"], r["lumpSum"]["5"]
        if s5:
            print("  SIP 5y: %d instalments from %s, value %.0f, XIRR %s" % (
                s5["instalments"], s5["firstDate"], s5["value"], pct(s5["xirr"])))
        if l5:
            print("  Lump sum 5y from %s: value %.0f" % (l5["start"], l5["value"]))
        dd5 = r["drawdowns"]["5"]
        if dd5:
            print("  Max drawdown 5y: %s (peak %s, trough %s, recovery %s)" % (
                pct(dd5["maxDrawdown"]), dd5["peak"], dd5["trough"], dd5["recovery"]))
    print("wrote", os.path.join(HERE, "expected.json"))


if __name__ == "__main__":
    main()
