# Mutual funds

Every Indian mutual fund scheme and its daily NAV, from AMFI's public files,
cleaned into a scheme master the desk can trust. This is Phase 1, the data
foundation: the tables, the nightly sync, the history backfill, a freshness
check and a coverage page. The fund analyst report (returns, risk, cost,
holdings) builds on it in Phase 2.

Facts only. Nothing here ranks, rates or recommends a fund: that needs SEBI
Research Analyst or Investment Adviser registration, which the desk does not
hold. No broker is involved: Dhan, FYERS and Angel document no mutual fund API.

Code:
- `src/AlgoTrading.Infrastructure/Services/MutualFunds/`: the parser
  (`AmfiNavParser`), the daily sync (`MutualFundNavSync`), the history backfill
  (`MutualFundHistory`), the writes (`MutualFundStore`), the schedule
  (`MutualFundSchedule`) and the reads (`MutualFundQueries`);
- `src/AlgoTrading.Api/Services/MutualFundNavSyncService.cs` and
  `MutualFundBackfillService.cs`: the two hosted services;
- `src/AlgoTrading.Api/Controllers/MutualFundsController.cs`;
- console: **Data → Mutual funds** (`/data/mutual-funds`, admin).

## Sources

| What | Where | Notes |
| --- | --- | --- |
| Today's NAV of every scheme | `https://portal.amfiindia.com/spages/NAVAll.txt` | About 1.5 MB, semicolon-separated, one line per scheme with its latest NAV. The old `www.amfiindia.com/spages/NAVAll.txt` redirects here. 14,441 schemes on 1 Nov 2026. |
| NAV history | `https://portal.amfiindia.com/DownloadNAVHistoryReport_Po.aspx?tp=1&frmdt=01-Apr-2020&todt=29-Jun-2020` | `tp` is the scheme type: 1 open-ended, 2 close-ended, 3 interval funds. Dates as `dd-MMM-yyyy`. All schemes of the type in one answer. Starts on 1 Apr 2006. Gzipped on request. |
| A check, never a source | `https://api.mfapi.in/mf/{code}` | mfapi.in, a free mirror of AMFI's history. `GET api/MutualFunds/{code}/check` compares it with what we stored. |

AMFI's own page says 90 days per history request; the server answered a
105-day window too, but the backfill keeps to 90. A 90-day open-ended window
is 40–80 MB of text (113,000 NAVs in 2006, 445,000 in 2023, 540,000 in 2026),
and AMFI takes from a few seconds to two minutes to produce it.

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
- **Out of market hours.** It does not run 09:00–15:40 IST on trading days
  (15:55 from 7 Dec 2026); a window in progress at 09:00 finishes its current
  request.
- **Failures.** A request that fails is tried again after 30 s, 2 and 5
  minutes; then the backfill waits an hour and tries that window again. An
  open-ended window that comes back empty is a failure, never a gap: every
  window since 2006 has open-ended NAVs.
- **Compression as it goes.** After each window it compresses the chunks it has
  finished with, so it never leaves gigabytes for the 12-hourly policy job to
  compress in one go, perhaps during a session.
- `maxWindows=N` stops after N windows (a trial); `restart=true` starts again
  from yesterday.

Measured on a Mac against AMFI (1 Nov 2026): 17 s to 2 minutes a window. From
yesterday back to April 2006 is 84 windows: expect about 25–30 million NAVs,
two to four hours, and roughly 350 MB once compressed. Follow it on the
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

## Configuration

`MutualFunds` in appsettings:

| Key | Default | |
| --- | --- | --- |
| `SyncEnabled` | true | the daily sync |
| `BackfillEnabled` | true | whether the backfill may run at all; it still starts only when asked |
| `RequestPaceSeconds` | 2.5 | the pause before each history request |
| `DailyUrl`, `HistoryUrl`, `SpotCheckUrl` | AMFI's and mfapi.in's | in case a source moves |
