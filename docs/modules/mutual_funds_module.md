# Mutual funds

Every Indian mutual fund scheme and its daily NAV, from AMFI's public files,
cleaned into a scheme master the desk can trust (Phase 1: the tables, the
nightly sync, the history backfill, a freshness check and a coverage page), and
on top of it a **fund analyst report** for any scheme (Phase 2, below): returns,
a worked ₹ SIP and lump sum, risk, the benchmark and category comparisons, the
expense ratio and the direct-versus-regular gap, each figure with its dates and
its source.

Facts only. Nothing here rates or recommends a fund: that needs SEBI Research
Analyst or Investment Adviser registration, which the desk does not hold. A
position in the category is a rank by a stated return among stated peers ("4th
of 31 by 3-year return"), never "best" or "top". No broker is involved: Dhan,
FYERS and Angel document no mutual fund API.

Code:
- `src/AlgoTrading.Infrastructure/Services/MutualFunds/`: the parser
  (`AmfiNavParser`), the daily sync (`MutualFundNavSync`), the history backfill
  (`MutualFundHistory`), the writes (`MutualFundStore`), the schedule
  (`MutualFundSchedule`) and the reads (`MutualFundQueries`);
- `src/AlgoTrading.Api/Services/MutualFundNavSyncService.cs` and
  `MutualFundBackfillService.cs`: the two hosted services;
- `src/AlgoTrading.Api/Controllers/MutualFundsController.cs`;
- console: **Data → Mutual funds** (`/data/mutual-funds`, admin).

Phase 2 (the fund report):
- `Services/MutualFunds/Analytics/`: the arithmetic (`FundAnalytics`, on a
  `NavSeries`), category peer groups and benchmark choice
  (`FundClassification.cs`);
- `MutualFundBenchmarkSync`, `MutualFundTerSync`, `MutualFundStatsJob`: the
  nightly jobs, scheduled by `MutualFundAnalyticsSchedule`
  (`MutualFundAnalyticsState.cs`) and run by
  `src/AlgoTrading.Api/Services/MutualFundAnalyticsService.cs`;
- `MutualFundReports` (reads) and `MutualFundReportBuilder` (builds);
- `src/AlgoTrading.Api/Services/MutualFunds/MutualFundSummaryWriter.cs`: the
  written summary;
- console: a scheme search on **Data → Mutual funds**, the report at
  `/data/mutual-funds/{code}` and the comparison at
  `/data/mutual-funds/compare?codes=`.

## Sources

| What | Where | Notes |
| --- | --- | --- |
| Today's NAV of every scheme | `https://portal.amfiindia.com/spages/NAVAll.txt` | About 1.5 MB, semicolon-separated, one line per scheme with its latest NAV. The old `www.amfiindia.com/spages/NAVAll.txt` redirects here. 14,441 schemes on 1 Nov 2026. |
| NAV history | `https://portal.amfiindia.com/DownloadNAVHistoryReport_Po.aspx?tp=1&frmdt=01-Apr-2020&todt=29-Jun-2020` | `tp` is the scheme type: 1 open-ended, 2 close-ended, 3 interval funds. Dates as `dd-MMM-yyyy`. All schemes of the type in one answer. Starts on 1 Apr 2006. Gzipped on request. |
| A check, never a source | `https://api.mfapi.in/mf/{code}` | mfapi.in, a free mirror of AMFI's history. `GET api/MutualFunds/{code}/check` compares it with what we stored. |
| Benchmarks: NSE total-return indices | `POST https://www.niftyindices.com/BackPage/getTotalReturnIndexString` | The request niftyindices.com's own historical-data page makes: `{"cinfo":"{'name':'NIFTY 50','startDate':'01-Oct-2025','endDate':'30-Sep-2026','indexName':'NIFTY 50'}"}`, answered with a JSON array, newest first (`"Date":"30 Oct 2026","TotalReturnsIndex":"35043.45"`). The page allows a year a request; under a second each; `[]` for a range with no values. No cookie needed (1 Nov 2026). History from 30 Jun 1999 (NIFTY 50) and 1 Apr 2005 (the newer indices). |
| Total expense ratio | `https://www.amfiindia.com/api/populate-te-rdata-revised?MF_ID=All&Month=10-2026&strCat=-1&strType=-1&excel=true` | AMFI's TER page's download: one workbook a month, every scheme's TER for every day, the regular and direct plans side by side. About 4.4 MB, 62,000 rows, 20–30 s to produce. The page's JSON form serves 100 rows a request (625 a month), so it is not used. |
| Risk-free rate (Sharpe, Sortino) | RBI, National Summary Data Page (`rbi.org.in/Scripts/BS_NSDPDisplay.aspx?param=4`, "Ratios and Rates") | The 91-day Treasury Bill (primary) yield, read by hand and set in configuration with its date: 5.73% for the week ended 16 Oct 2026. Not fetched. |

AMFI's own page says 90 days per history request; the server answered a
105-day window too, but the backfill keeps to 90. A 90-day open-ended window
is 40–80 MB of text, and AMFI takes from a few seconds to two minutes to
produce it. A whole window, all three types, held 113,000 NAVs in 2006,
578,000 in 2020 (close-ended FMPs were many then), 445,000 in 2023 and
540,000 in 2026.

## Schedule

All times IST. Decided in `MutualFundSchedule.cs`; the result of every sync is
kept in `system_settings` (`mutualfunds.sync.last`), so a restart neither
repeats a sync nor forgets a failure.

| When | What |
| --- | --- |
| 23:30, every day | The daily file. AMFI publishes each business day's NAVs by about 23:00. Weekends and holidays too: liquid and overnight funds publish a NAV for calendar days, and the file holds only each scheme's latest one. |
| 07:00, when needed | The daily file again, only if the 23:30 run failed or did not reach the newest business day. |
| an hour after a failure | The daily file again. |
| a few minutes after the API starts | The first sync on a new server; afterwards the rules above make up anything missed while the API was down (a missed 07:00 still runs at 10:00). |

A **business day** is a weekday the NSE calendar does not close, the closest
public rule to AMFI's (AMFI publishes no holiday list). The NAV day **due** at
any moment is the last business day on or before the date 23 hours 30 minutes
earlier: on Tuesday at 07:00 that is Monday; on Monday at 23:00 it is still
Friday.

The history backfill (below) never starts by itself.

The fund report's jobs run after the NAV sync, one after another in one
service (`MutualFundAnalyticsService`), never 09:00–15:40 IST on trading days
(15:55 from 7 Dec 2026) and never while the NAV sync runs:

| When | What |
| --- | --- |
| 23:45, every day | The benchmark TRIs: the last ten days of each index, then any history not yet fetched. Then AMFI's TER for the current month (and the month before during a month's first five days). |
| 07:15, when needed | Either again, only if the 23:45 run failed or the TRIs fell short of the business day due. |
| an hour after a failure | That job again. |
| when a newer full NAV day is stored | The category ranking. While the NAV history backfill fills older years, again at most every six hours, so more schemes get their 3 and 5-year figures. |
| when an admin asks | `POST api/MutualFunds/analytics/{benchmarks,ter,stats}`: at the service's next look outside market hours. |

Each benchmark's history back to `BenchmarkFrom` (1 Jan 2006, before AMFI's NAV
history begins) is fetched **once, by itself**, unlike the NAV backfill: about 21
one-year requests an index, 189 in all, 2.5 s apart, about ten minutes on the
first night. It resumes where it stopped; a window that comes back empty ends that
index's history (the index did not exist yet).

## Tables

`mf_schemes`, one row per AMFI scheme code (a code is one plan and one option
of a fund, so "UTI Nifty 50 Index Fund" is 120716 direct growth and 100822
regular growth, and more):

| Column | |
| --- | --- |
| `SchemeCode` | AMFI's code, the key |
| `IsinGrowth`, `IsinReinvest` | AMFI's "ISIN Div Payout/ ISIN Growth" and "ISIN Div Reinvestment"; null unless a well-formed ISIN |
| `Amc`, `SchemeName` | the AMC line the scheme sits under; the name as AMFI writes it (the base name in the daily file) |
| `Plan` | `direct`, `regular` or `unknown` |
| `Option` | `growth`, `idcw`, `other` (bonus, unclaimed amounts) or `unknown`; `OptionLabel` keeps AMFI's words ("Monthly IDCW Option") |
| `Category` | the section line, e.g. `Open Ended Schemes(Equity Scheme - Large Cap Fund)` |
| `FirstNavDate`, `LastNavDate`, `LastNav` | the stored NAV range and the newest NAV |
| `IsActive` | a NAV within 30 days of the newest NAV day in AMFI's latest file (not of today: a sync that has failed for a week must not retire every scheme) |
| `FirstSeenUtc`, `LastSeenUtc` | when a sync first and last read it |

`mf_nav`: `SchemeCode`, `Date`, `Nav` (`numeric`, exactly as AMFI wrote it,
2 to 8 decimals), `Source` (`amfi-daily` or `amfi-history`). Primary key
(`SchemeCode`, `Date`). A TimescaleDB hypertable on `Date` with 90-day chunks,
compressed once a chunk is 30 days old (segmented by scheme, ordered by date).
Measured locally: a full 90-day chunk of about 540,000 NAVs is 55 MB before
compression and 6 MB after. No retention policy.

How the two sources merge:
- **The daily file** decides the name, AMC, category and ISINs, and the plan
  or option when it states one. It writes a NAV only for a day newer than the
  scheme's stored last day, or the same day with a different value (AMFI's
  correction). Dormant schemes keep a years-old NAV in the file; it is written
  once. NAVs, master and active flags go in one transaction.
- **The history** adds schemes the master lacks (closed and merged ones, marked
  inactive) and fills only what is unknown or empty. Its full NAV names often
  state what the daily file leaves blank: 119527, blank in the daily file, is
  "Aditya Birla Sun Life Large Cap Fund-DIRECT - IDCW" in the history.
- A NAV day already stored with the same value is never rewritten, so every
  run, and every repeat of a run, is idempotent.

Phase 2 adds three tables:

`mf_benchmark_tri`: `IndexName` (niftyindices.com's name, e.g. `NIFTY 50`),
`Date`, `Value` (`numeric`, as published). Key (`IndexName`, `Date`). A
hypertable with one-year chunks, compressed after 30 days, segmented by index.
About 5,000 rows an index from 2006.

`mf_ter`: `NsdlSchemeCode` (AMFI's TER key; for the rows AMFI files under "NSDL
Code not allocated", which several schemes share, `name:` and the name key),
`Date`, `SchemeName`, `NameKey` (the name reduced to words), `SchemeType`,
`Category`, `RegularTer`, `DirectTer` (percent a year; null when AMFI gives
none). Key (`NsdlSchemeCode`, `Date`), index (`NameKey`, `Date`). A hypertable
with one-year chunks, compressed after 90 days (the current month is reread every
night). About 62,000 rows a month. Only the two plans' total TER is kept: AMFI's
components changed with SEBI's 2026 TER rules (base expense ratio, brokerage,
transaction cost, statutory levies, in place of base TER, 6A(b), 6A(c), GST), and
the parser finds the totals by their headers in either layout.

`mf_scheme_stats`: one row per ranked scheme: `AsOfDate`, `PeerKey`, `Plan`,
`Return1y/3y/5y` (fractions), `Rank1y/3y/5y`, `Peers1y/3y/5y`, `ComputedUtc`.
Replaced by each run; not a time series.

The jobs' state is in `system_settings`, one small key each
(`mutualfunds.analytics.benchmarks`, `.benchmarks.history`, `.ter`, `.stats`,
`.requested`), since a value may not exceed 2,000 characters.

## Endpoints

The reads need the `market-data` grant (admins hold every grant). The rest are
admin-only.

| | |
| --- | --- |
| `GET api/MutualFunds/coverage` | Schemes and active schemes by plan, option, category and AMC; NAV rows (TimescaleDB's estimate above 2 million), first and last date, the newest full NAV day, the day due and how many business days behind; the last sync; the backfill. The desk checkup reads it. |
| `GET api/MutualFunds/schemes?search=&plan=&option=&active=&take=` | Name or AMC containing the text (any case), or the exact code or ISIN; active first. `take` up to 200. |
| `GET api/MutualFunds/{code}/nav?from=&to=` | One scheme's NAVs, oldest first; dates `yyyy-MM-dd`, either may be left out. |
| `POST api/MutualFunds/sync` | Runs the daily sync now and answers with its result. Admin-only. |
| `POST api/MutualFunds/backfill?from=&maxWindows=&restart=` | Starts or resumes the history backfill. Admin-only. |
| `DELETE api/MutualFunds/backfill` | Pauses it before its next request. Admin-only. |
| `GET api/MutualFunds/{code}/check` | Our NAVs against mfapi.in's, day by day. Admin-only. |
| `GET api/MutualFunds/{code}/report?asOf=` | The fund report (below), to the scheme's last NAV on or before `asOf` (its last NAV when left out). 404 `{message}` for an unknown scheme or one with no NAV by then. |
| `GET api/MutualFunds/{code}/report/summary?asOf=` | The report's written summary, or the reason there is none. Slow the first time (a model call); the page loads it after the report. |
| `GET api/MutualFunds/compare?codes=a,b,c,d` | 2 to 4 schemes' reports, all ending on the newest NAV day every one of them has. 400 for a bad list, 404 for an unknown code. |
| `POST api/MutualFunds/analytics/{job}` | `benchmarks`, `ter` or `stats`: run that job at the service's next look outside market hours. Admin-only. |

`coverage` also carries `analytics`: the benchmarks stored (rows, range,
history complete), the TER's latest day and schemes, the ranking's day and
schemes, each job's last run and the jobs asked for.

## The history backfill

`POST api/MutualFunds/backfill` (admin). It reads AMFI's history in 90-day
windows, newest first, from yesterday back to 1 Apr 2006 (`from=` stops it
earlier), each window as open-ended, close-ended and interval schemes in turn.

- **Paced and light.** One request at a time, 2.5 s before each
  (`MutualFunds:RequestPaceSeconds`); each answer is streamed and written 5,000
  NAVs per statement, so memory stays flat whatever the window's size.
- **Resumable by window.** The state (`mutualfunds.backfill` in
  `system_settings`) records the first day of the oldest window finished. A
  run stopped anywhere, a pause or a restart repeats at most one window, and
  repeating it writes nothing new. Once asked for, it carries on after deploys
  until it is done or paused.
- **Out of the desk's way.** It does not run from 08:40 IST on NSE trading days
  (ahead of the 08:45 morning job) to 15:40 (15:55 from 7 Dec 2026), nor while
  MCX is in session, when the crude strategies run live on the same small
  server. So on a trading day it runs from the MCX close (23:30 or 23:55) to
  08:40, which is more than the whole backfill needs, and all weekend. No request
  starts within 5 minutes of that edge, and a request still going when it comes
  is stopped there and its window repeated, which writes nothing new.
- **Failures.** A request that fails, or takes longer than 20 minutes, is
  tried again after 30 s, 2 and 5 minutes, each time only if the backfill may
  still run; then the backfill waits an hour and tries that window again. An
  open-ended window that comes back empty is a failure, never a gap: every
  window since 2006 has open-ended NAVs.
- **Compression as it goes.** After each window it compresses the chunks it has
  finished with, so it never leaves gigabytes for the 12-hourly policy job to
  compress in one go, perhaps during a session.
- `maxWindows=N` stops after N windows (a trial); `restart=true` starts again
  from yesterday.

Measured on a Mac against AMFI (1 Nov 2026), six windows: 17 s to 2 minutes
a window. From yesterday back to April 2006 is 84 windows: expect about 30–40
million NAVs, two to four hours on the server, and roughly 400 MB once
compressed (3–4 GB before). Follow it on the
coverage page or in `coverage` → `backfill`; check a few schemes with
`GET api/MutualFunds/{code}/check` when it is done.

## Freshness

The [desk checkup](checkup.md) (before the open, weekly and on request) reads
`coverage`: two business days without a new full NAV day is worth a look,
with the last sync's result; one day behind at 08:55 is a note. A "full" NAV
day has NAVs for at least half as many schemes as the busiest day of the
fortnight before it, so a Sunday with only the liquid funds' NAVs never passes
for a fresh file.

## Known data-quality issues

From the files of 1 Nov 2026 and the history windows read while building this:

- **Blank plans and options.** 5,709 of 14,441 daily rows have no Plan and
  5,715 no Option. A blank plan is read from the name ("Direct" first, since
  "Regular" can also be an IDCW frequency, as in "Direct Plan - Regular
  Dividend"); a blank option from the name, with fund-name words such as
  "Growth Fund" and "Dividend Yield" taken out first, then from the ISINs (only
  an IDCW option has a reinvestment ISIN). What none of them says stays
  `unknown`: ETFs, and plans from before direct plans existed (2013) such as
  "Retail Plan", "Institutional Plan" or "Plan B".
- **One option, many spellings.** Growth is "Growth", "Growth Option",
  "GROWTH", "GROWTH OPTION", "Direct Growth", "Cumulative"; IDCW in dozens of
  forms ("Payout of Income Distribution cum capital withdrawal option",
  "Daily - IDCW", "Dividend Option"…).
- **Dormant schemes.** About 5,400 rows carry a last NAV from 2008–2025 (617
  from 2018). They are stored once and marked inactive.
- **NAVs that are not NAVs.** 241 rows have no usable NAV, mostly 0.0000 for
  written-off segregated portfolios. AMFI may also write "N.A.". Neither is
  stored: a missing day is a gap.
- **ISIN fields.** "-" for none, "Redeemed", a code that is not an ISIN
  ("HDFCNIVODG"), and trailing spaces. Only well-formed ISINs are kept. A few
  growth ISINs belong to two scheme codes, so ISIN is not unique. Rows from
  2006 have no ISINs at all.
- **Look-alike schemes.** Some funds list two codes with the same name, plan
  and option (Axis Children's Fund: 135762 and 135764 are both direct growth);
  the name alone does not tell them apart.
- **Categories change name.** "Equity Scheme - Large Cap Fund" and "Equity
  Schemes - Large Cap Fund" both occur, and older sections ("Open Ended
  Schemes(Income)", "Growth") hold schemes classified before SEBI's 2017
  categories. Categories are stored as AMFI writes them (the history's
  "Open Ended Schemes ( Income )" tidied to the daily file's spacing); mapping
  them to SEBI's categories is Phase 2.
- **Weekend NAVs.** Liquid and overnight funds have NAVs for weekends: 827
  schemes dated Saturday 31 Oct and 712 Sunday 1 Nov in the daily file. Some
  have Sunday NAVs but not Saturday's. mfapi.in lacks some of these.
- **Two name forms.** The daily file gives the base name ("UTI Nifty 50 Index
  Fund") with plan and option in their own columns; the history gives the full
  NAV name ("UTI Nifty 50 Index Fund - Growth Option- Direct").
- **No data looks like success.** For a range with no NAVs (before April 2006,
  or no interval fund that week) AMFI answers HTTP 200 with its download form
  in HTML. The parser tells the form from a file by the header line.
- **Encoding.** UTF-8 with CRLF line ends; a section line carries a curly
  apostrophe ("Children’s Fund").

Spot checks against mfapi.in on 1 Nov 2026 (UTI Nifty 50 direct and regular,
a liquid fund and one more scheme) matched on every common day. 30 Oct 2026:
162.3751 (120716, direct growth) and 159.8799 (100822, regular growth), as in
AMFI's file; the parser tests hold both.

## The fund report (Phase 2)

For one scheme, as of its last NAV (or a day asked for):

- **What history it rests on**: "NAVs from 1 Sep 2016 to 30 Oct 2026 (2,503
  days with a NAV)", and whether that is the scheme's whole history. It is only
  once the NAV backfill has covered at least a week before the first stored NAV
  and found nothing (or the backfill is done, or the history reaches April
  2006). Until then the report says older NAVs may still arrive, and the longer
  periods stay empty with that reason. While the backfill runs (2–4 hours once
  started) many schemes are in this state.
- **Returns**: 1, 3, 5 and 10-year annualised returns; each calendar year (the
  current one year to date); the trailing 3-year return on every day it exists
  (lowest, highest, median, share below zero, share above the benchmark).
- **Worked ₹ examples**, for each period: ₹10,000 a month on the first NAV day
  of each month, valued on the as-of day, with its XIRR; and ₹2,00,000 invested
  on the SIP's first day. No stamp duty, exit load or tax.
- **Risk**: annualised volatility (3 years, or 1), Sharpe and Sortino with the
  risk-free rate named, the deepest fall over 3, 5, 10 years and the whole
  stored history with its peak, trough and recovery date ("not recovered by"
  otherwise), and the fall from the highest NAV stored.
- **Against the benchmark**: the benchmark's return over the same dates and the
  difference (the tracking difference for an index fund), beta, upside and
  downside capture, and for an index fund or ETF the tracking error.
- **Category**: the scheme's rank by 1, 3 and 5-year return among its peers,
  with the peers' median, highest and lowest, from the nightly ranking.
- **Cost**: the TER in effect (both plans, and this plan's), and the
  direct-versus-regular gap in ₹ on the same SIP and lump sum, from the two
  plans' own NAVs.
- **Sources** (each with its as-of day), **notes** for every gap, and the
  disclaimer: information about past NAVs, not investment advice.

An IDCW option's report says that its NAV falls by each payout, so its returns
leave the payouts out. A figure that cannot be computed is null with its reason;
"not known yet" is never shown as a number.

### How each figure is computed

The rules are written once, here; `FundAnalytics` follows them, and an
independent Python implementation of the same text checks it
(`tests/AlgoTrading.UnitTests/Fixtures/MutualFunds/Analytics/reference.py`).

- **Lookups.** A period starts on the first NAV **on or after** its anniversary
  (an N-year period to E starts on or after the same day N years before E;
  29 Feb becomes 28 Feb). A NAV found that way is usable only **within 7 days**
  of the day wanted: a longer gap is a hole in the history, not a start.
- **Coverage.** An N-year figure needs the stored series to start on or before
  the anniversary. Otherwise: "not enough history: the NAVs start on …, after …".
- **Annualised return**: (NAV_E / NAV_S) ^ (365 / days) − 1, over the actual
  days between the two NAVs.
- **Calendar year Y**: from the last NAV of Y−1 (in its last week) to the last
  NAV of Y (in its last week), or to E for the current year (year to date). Not
  annualised.
- **Rolling 3-year**: the trailing 3-year return as it stood on each NAV day.
  Against a benchmark, a window counts when the benchmark has a value on its end
  day and within 7 days before its start; "above" means strictly higher.
- **SIP**: 12N instalments, the last in E's month, each on the first NAV day on
  or after the 1st (within 7 days); units = Σ 10,000 / NAV; value = units ×
  NAV_E. XIRR solves Σ amount / (1 + r) ^ (days / 365) = 0 by bisection (one
  root: payments, then one value). **Lump sum**: ₹2,00,000 on the SIP's first
  instalment day.
- **Volatility**: the sample standard deviation (n − 1) of the returns between
  consecutive NAVs over the last 3 years (1 when the history is shorter), ×
  √(returns a year), where returns a year = returns / (days / 365.25): about 248
  for an equity fund, 326–365 for a liquid fund with weekend NAVs.
- **Sharpe** = mean(r − rf/P) / sd(r) × √P; **Sortino** = mean(r − rf/P) /
  √(Σ min(0, r − rf/P)² / n) × √P, with P the returns a year and rf the
  configured yearly rate.
- **Drawdown**: the lowest NAV / running peak − 1 in the window; the peak is the
  first day the peak value was reached; the recovery is the first day after the
  trough with the NAV back at that peak.
- **Benchmark periods**: the benchmark's value on E itself (when the TRI is not
  yet updated for E, the comparison is empty with that reason) and its last value
  within 7 days on or before the fund's start, annualised over the fund's days.
- **Beta**: cov / var (n − 1) of the returns between consecutive days both series
  have, over 3 years from the last common day (1 when shorter). **Tracking
  error**: the annualised sample standard deviation of the daily return
  difference over 1 year (SEBI's disclosure convention). **Capture**: 36 monthly
  returns ending on the last common day, each month from the first common day on
  or after the same day a month earlier; downside capture = geometric-mean fund
  return / geometric-mean benchmark return × 100 over the months the benchmark
  fell, upside over the months it rose; at least 3 such months.
- **Direct versus regular**: the same SIP and lump sum on both plans' series as
  of the report's day; gap = direct − regular, in rupees.

### Benchmarks

| Index (niftyindices.com) | Compared with |
| --- | --- |
| NIFTY 50, NIFTY NEXT 50, NIFTY 100, NIFTY 500, NIFTY MIDCAP 150, NIFTY SMALLCAP 250, NIFTY LARGEMIDCAP 250, NIFTY BANK, NIFTY500 MULTICAP 50:25:25 | Index funds and ETFs whose name names the index: its words followed by nothing or by "Index", "ETF", "Fund", "BeES", "Plan" and the like, so "Nifty 50 Equal Weight", "Nifty 100 Low Volatility 30" or "Nifty Private Bank" match nothing. |
| NIFTY 100 | Large Cap funds |
| NIFTY LARGEMIDCAP 250 | Large & Mid Cap |
| NIFTY MIDCAP 150 | Mid Cap |
| NIFTY SMALLCAP 250 | Small Cap |
| NIFTY500 MULTICAP 50:25:25 | Multi Cap |
| NIFTY 500 | Flexi Cap, Focused, Value, Contra, Dividend Yield, ELSS |

The category mapping is AMFI's first-tier benchmark list (SEBI circular of 27 Oct
2021), NSE's index of each NSE/BSE pair, and the report says so: "the usual
benchmark of the category; the fund's own SID may name another". Debt and hybrid
schemes (whose benchmarks are CRISIL's), sectoral and thematic funds (each its
own index), pre-2017 categories and index funds on other indices show "Benchmark
not collected yet" or "No benchmark", with the reason. Total-return indices, not
price indices: a NAV includes reinvested dividends, so a price index would flatter
every fund.

### Category comparison

Peers are the active schemes AMFI lists under the same SEBI category and scheme
type (open-ended, close-ended, interval), same plan, growth option, with a NAV on
the day ranked. AMFI's daily file of 1 Nov 2026 writes some categories two ways
(SEBI's 2017 names and the renamed sections of 2026: "Equity Scheme - Large Cap
Fund" for 110 schemes, "Equity Schemes - Large Cap Fund" for 54; "ELSS" and
"ELSS- Tax Saver Fund"; "Sectoral/ Thematic", "Sectoral Fund" and "Thematic
Fund"); spelling differences are folded together. Debt categories renamed in
substance ("Liquid Fund" under "Debt Scheme" and under "Income/Debt Oriented
Schemes") stay apart: the desk cannot tell that their schemes match. Pre-2017
sections ("Income", "Growth") have no peer group. A rank is 1 for the highest
return, shared on ties, among the peers with a return for the period; while the
NAV backfill runs, the 3 and 5-year counts are smaller, and the report says so.

### TER and the gap between plans

AMFI's TER file names a scheme, not each plan and option, so a scheme is matched
by its base name reduced to words (`NameKey`: "Axis Children's Fund" and the
file's escaped "Axis Children&apos;s Fund" both become "axis childrens fund"),
and only when exactly one TER scheme has that name; otherwise the report says
why. On the files of 1 Nov 2026, 2,160 of 2,172 TER names matched the daily
file's base names exactly, and the other 10 differ only by that escaped
apostrophe. The TER shown is the newest within 45 days before the report's day.
The direct-versus-regular gap does not use the TER: it comes from the two plans'
own NAVs, so it holds whatever the expense ratio was over the years.

### The written summary

A pseudo-agent, **Fund Analyst** (`fund-analyst`), describes the report in one
paragraph, through the analyst tier's Amazon Bedrock models only (NVIDIA's free
tier may not serve platform users). `MutualFundSummaryWriter` shows it only when
every number, date and time is the report's (`AiFactCheck`) and no advice or
rating word appears: the Portfolio guard (`PortfolioAdviceGuard`: English, Hindi
and Hinglish) plus best, worst, top, star, rating, outperform, underperform,
safe and guaranteed. The scheme's, AMC's, category's and benchmark's names are
taken out before the wording check, so "Target Maturity" or "Opportunities Fund"
in a name is not read as advice. A paragraph that fails is sent back once with
what failed; a second failure leaves the page without a summary, and it says
why. A summary that passed is kept in memory for 12 hours per scheme, day and
facts. Without a Bedrock model there is no summary; the report is complete
without it.

### Checks

- `MutualFundAnalyticsReferenceTests`: six schemes' real NAVs (mfapi.in) and four
  NIFTY TRIs (niftyindices.com), 1 Sep 2016 to 30 Oct 2026, against the
  independent Python implementation's numbers to 1e-9: UTI Nifty 50 direct and
  regular, Mirae Asset Large Cap, Nippon India Small Cap, HDFC Liquid (weekend
  NAVs) and UTI Nifty Next 50 (too young for 10 years).
- The module research note's worked example reproduces: UTI Nifty 50 Index Fund
  direct (120716) as of 30 Oct 2026: ₹10,000 a month from 1 Nov 2021 (60
  instalments) is ₹6,79,412, XIRR 4.92% (regular ₹6,77,240, 4.79%); ₹2,00,000
  from 1 Nov 2021 is ₹2,69,565 (₹2,68,021); 1, 3 and 5-year returns −10.16%,
  7.38%, 6.16%; the deepest 5-year fall −15.91% (17 Jan to 17 Jun 2022,
  recovered 1 Nov 2022). Its tracking error against the NIFTY 50 TRI over the
  year is 0.02%, its TER 0.25% direct and 0.37% regular.
- Against a scratch TimescaleDB (1 Nov 2026): the benchmark sync stored 9 indices
  from 2016 in two minutes, matching an independent fetch of the NIFTY 50 TRI on
  all 2,518 days; the TER sync read 62,774 rows; a warm report took 50–90 ms and
  four compared 0.4–0.65 s.

## Configuration

`MutualFunds` in appsettings:

| Key | Default | |
| --- | --- | --- |
| `SyncEnabled` | true | the daily sync |
| `BackfillEnabled` | true | whether the backfill may run at all; it still starts only when asked |
| `RequestPaceSeconds` | 2.5 | the pause before each history request |
| `DailyUrl`, `HistoryUrl`, `SpotCheckUrl` | AMFI's and mfapi.in's | in case a source moves |
| `AnalyticsEnabled` | true | the fund report's jobs (benchmarks, TER, ranking) |
| `BenchmarkBackfillEnabled` | true | each index's history back to `BenchmarkFrom`, fetched once by itself |
| `BenchmarkFrom` | 2006-01-01 | |
| `BenchmarkUrl`, `TerUrl` | niftyindices.com's and AMFI's | in case a source moves |
| `RiskFreeRatePct`, `RiskFreeRateSource`, `RiskFreeRateAsOf` | 5.73, RBI NSDP 91-day T-bill, 2026-10-16 | the rate Sharpe and Sortino use, stated on every report; update the three together, or remove the rate to leave Sharpe and Sortino out (the report then says why) |
