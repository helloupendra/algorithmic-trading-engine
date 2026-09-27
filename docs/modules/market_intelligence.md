# Market intelligence

What moves the Indian market, recorded from the day the recorders started,
with history back to 2020 where a source keeps one:
- the news;
- the exchange's corporate filings and board-meeting calendar;
- GIFT Nifty and the overseas markets;
- NSE's cash-market breadth;
- the participant-wise open interest history.

The [forecasts](analysis.md) are issued at 08:50 IST from index candles alone.
This module is the data they will learn from next. A separate Python scorer
turns each headline and filing into a sentiment, an importance, the stocks it
is about and its topics.

Every row records **when the desk first knew it**. A model that says "at 08:50
I knew X" must be able to prove it, and a replay of a past morning must see
exactly what that morning saw. RSS keeps no archive: a headline not recorded
today is lost for good. So news and filings are recorded around the clock,
before anything else.

Code:
- `src/AlgoTrading.Infrastructure/Services/MarketIntelligence/` (the recorders,
  the parsers and the backfill);
- `src/AlgoTrading.Api/Services/MarketIntelligence*.cs` and
  `NewsScoringScheduler.cs` (the schedules).

API (admin only): `api/MarketIntelligence/...`, below. No console page yet:
the dashboard is being redesigned and will show it.

## The point-in-time rule

| Table | The stamp | A model issued at 08:50 IST on day D may use a row when |
| --- | --- | --- |
| `news_items` | `FirstSeenUtc`: when the recorder first saw the headline | `FirstSeenUtc` < 08:50 IST on D |
| `corporate_announcements` | `FirstSeenUtc`; `AnnouncedUtc` is NSE's broadcast time | `FirstSeenUtc` < 08:50 IST on D |
| `corporate_calendar` | `FirstSeenUtc`: when the meeting was first listed | `FirstSeenUtc` < 08:50 IST on D |
| `market_quote_snapshots` | `FetchedUtc`: when the snapshot was taken | the latest row per `Key` with `FetchedUtc` < 08:50 IST on D |
| `market_global_daily` | `Date`, the market's own trading date | `Date` < D |
| `market_breadth_daily` | `Date`, the NSE session | `Date` < D |

The first-seen stamps are written once and never changed. A headline seen
again, on the next poll or in another feed, is skipped rather than updated.
Every stamp is taken once the answers are in, never before the request, so it
can only err late: a filing broadcast while the request was in flight never
reads as known before it existed.
The tests check exactly this, by recording the same answers twice, later.

`PublishedUtc` (a feed's own date) is there to read, not to decide
availability: feeds back-date, re-date and omit it. `AsOfUtc` on a snapshot is
the source's quote time, which for a closed market can be many hours old.

`Date` < D is safe for every overseas symbol. At 08:50 IST:
- New York's bar for the day before is over (the cash close is 01:30-02:30 IST,
  the futures close 02:30-03:30);
- Asia's bar for D is still trading, and `Date` < D leaves it out.

The recorder also writes only bars that are surely over (see below), so no row
holds a half-formed day.

## What is recorded, and when

All times are IST, which has no daylight saving.

| What | Table | Source | When | History |
| --- | --- | --- | --- | --- |
| Headlines | `news_items` | 33 RSS feeds, each the publisher's own | every 5 minutes, all day, every day | none: from the first poll |
| Filings | `corporate_announcements` | NSE `api/corporate-announcements` | every 10 minutes, 06:00-23:30, every day | none (see [gaps](#known-gaps)) |
| Board meetings | `corporate_calendar` | NSE `api/event-calendar`, today to 90 days ahead | every 4 hours, 06:00-23:30, every day | none |
| Morning prices | `market_quote_snapshots` | GIFT Nifty from NSE IX; 16 overseas keys from Yahoo | every 15 minutes, 06:00-16:00, weekdays, 08:45 included | none |
| Overseas daily bars | `market_global_daily` | Yahoo chart, daily | once a day from 07:00 | from 2020-01-01 |
| Breadth | `market_breadth_daily` | NSE CM bhavcopy + 52-week file | every 30 minutes, 18:00-23:30, the last 10 sessions | from 2020-01-01 |
| Participant OI | `market_participant_oi` (Market factors) | NSE `fao_participant_oi_DDMMYYYY.csv` | the evening sync as before | from 2020-01-01 |
| Scores | the six scoring columns | `python -m analysis news-score` | every 10 minutes, 06:00-23:30, not 08:40-15:40 on trading days | scores what is recorded |

Filings are polled on weekends and holidays too. By the evening of Sunday
27 Sep 2026 NSE had broadcast 33, the latest at 18:47, and a Sunday filing is
exactly what Monday's open reacts to.

## News

The feeds are the console's news categories (India markets, global,
commodities and eight sectors), plus seventeen the recorder alone reads:
- India's economy desks: ET, Business Standard, Mint and BusinessLine;
- BusinessLine's markets section;
- RBI's press releases and SEBI's feed;
- CNBC (US top news, economy, Asia), Nikkei Asia, MarketWatch's top
  stories, the FT's markets section;
- the Federal Reserve's and the ECB's press releases;
- BBC World;
- OilPrice.com.

Each extra feed is filed under the console's own category keys (`india`,
`global`, `commodities`), so `Category` has one vocabulary. The list lives in
`NewsFeedCatalog`, shared with the console's live news, so the two never read
different feeds.

Checked on 27 Sep 2026 and left out:
- Moneycontrol (its feeds stopped in April 2024);
- WSJ and MarketWatch's market pulse (stopped in 2025).

The duplicate check is `LinkHash`: SHA-256 of the link reduced to what names
the article. The scheme, `www.`, the fragment and a trailing slash are
dropped, and so are click-tracking parameters (`utm_*`, BBC's `at_*`,
MarketWatch's `mod`, ET's `from`). An item with no link is keyed on source and
title. Two feeds of one publisher carrying one story make one row, filed by
the first feed in catalogue order.

Dates: RBI writes its pubDate with no zone, and SEBI writes
"24 Sep, 2026 +0530". A date with no zone is read in the feed's own zone
(IST for Indian publishers) rather than as UTC. A date that cannot be read is
stored as null; the headline is still stored.

## NSE filings

NSE's announcements answer holds the latest 20 filings, and a results evening
files more than 20 in ten minutes. So the recorder also asks for **whole days**
by date (NSE answers those in full) in two cases:
- the first poll after the API starts;
- a poll where none of the 20 is already stored.

It asks from the newest stored filing's day, at most 3 days back: one request
of about a megabyte after a restart or a burst, and the small one otherwise.
A filing caught that way has its real broadcast time in `AnnouncedUtc` and the
fetch time in `FirstSeenUtc`.

The key is NSE's own id for the filing (`seq_id`), checked stable between the
latest and the by-date answers.

NSE's website answers only a caller that looks like the browser on its page.
`NseWebClient` loads the page first for its cookies, sends the page as the
referrer, keeps the cookies itself (the HTTP handlers are recycled every few
minutes) and loads the page again every 20 minutes or on a 401/403.

The event calendar is keyed on symbol, date and purpose, so NSE's duplicate
listings make one row. A meeting that is moved shows up as a new row with the
new date; the old row stays, because it was once what was known.

## GIFT Nifty and overseas markets

Keys and tickers (every one checked, none changed):

| Key | Yahoo | Key | Yahoo | Key | Yahoo | Key | Yahoo |
| --- | --- | --- | --- | --- | --- | --- | --- |
| SPX | `^GSPC` | N225 | `^N225` | FTSE | `^FTSE` | DXY | `DX-Y.NYB` |
| NDX | `^NDX` | HSI | `^HSI` | BRENT | `BZ=F` | US10Y | `^TNX` |
| DJI | `^DJI` | KS11 | `^KS11` | WTI | `CL=F` | USDINR | `INR=X` |
| VIX | `^VIX` | STOXX50E | `^STOXX50E` | GOLD | `GC=F` | ES | `ES=F` |

`GIFTNIFTY` is the nearest unexpired NIFTY future on NSE IX, read with the
Market factors panel's code and token.

Snapshots:
- Every price in one snapshot shares one `FetchedUtc`.
- GIFT Nifty is fetched first. Each market is fetched on its own, so one
  failing costs only its row.
- The morning job restarts the API at about 08:45. A slot missed by a restart
  is taken as soon as the API is back, if it is less than 10 minutes old. A
  slot already taken before the restart is not taken twice.
- So the 08:50 forecast reads the latest snapshot before 08:50, not the one
  stamped exactly 08:45.

Daily bars:
- A bar is dated in its exchange's own zone, daylight saving included. E-mini
  futures are stamped at midnight New York time, and a fixed offset would put
  every winter bar on the day before.
- The daily run re-reads the last 10 days of each market and upserts them, so
  a close Yahoo revises is corrected.
- Only bars whose day is surely over are written: dated before the IST date of
  seven hours ago. The last overseas day to close is the rupee's London day,
  at 05:30 IST in winter.
- A market with no rows gets its history from 2020, outside market hours.

## Breadth

The EQ series of NSE's cash-market bhavcopy: what NSE itself counts as the
equity segment. It includes the ETFs listed in that series; SME, bond and
trade-for-trade series are left out.
- Advancing, declining and unchanged compare each stock's close with its
  previous close.
- Turnover is the EQ lines' traded value, in rupees crore.

The bhavcopy comes in two formats:

| Sessions | File |
| --- | --- |
| up to 5 Jul 2024 | `archives.nseindia.com/content/historical/EQUITIES/YYYY/MON/cmDDMONYYYYbhav.csv.zip` |
| from 8 Jul 2024 | `nsearchives.nseindia.com/content/cm/BhavCopy_NSE_CM_0_0_0_YYYYMMDD_F_0000.csv.zip` |

The likelier one is asked first, the other only on a 404.

New 52-week highs and lows **are** filled, but not from raw bhavcopies. Those
are not adjusted for corporate actions, so a stock after a 1:10 split would
read as a new 52-week low. They come from NSE's own adjusted file instead
(`nsearchives.nseindia.com/content/CM_52_wk_High_low_DDMMYYYY.csv`, available
back to 2020):
- The file "effective for" a session holds the 52 weeks before it.
- A stock counts as a new high when the day's high is above that file's
  adjusted high; lows the same way.
- On 24 Sep 2026 this gave 58 highs and 64 lows. Counting instead the stocks
  the next day's file dates to 24 Sep gives 60 and 68: ties and the window's
  edge.
- Where the file is missing, the two counts are left null and the row is still
  stored.

## Backfills

| Dataset | From | Requests | Stops short of |
| --- | --- | --- | --- |
| `breadth` | 2020-01-01 | two per session (bhavcopy, 52-week file) | nothing: the evening run and the backfill are one loop |
| `participant-oi` | 2020-01-01 | one per session, the same archive URL as the evening sync | the last 40 sessions, which the Market factors sync keeps |
| `global-daily` | 2020-01-01 | one per market (16 in all) | nothing |

How they run:
- **Newest first**, skipping what is stored. A run that stops for any reason
  leaves nothing half-done; the next one asks which days are still missing.
- **About one request a second.** Every request to NSE goes through one
  shared pacer, the Market factors sync's included, so a backfill and the
  evening sync on the same evening do not add up to a burst. Yahoo history is
  spaced two seconds apart.
- **Never 09:00-15:40 IST on an NSE trading day.** A backfill in progress
  stops at 09:00 and carries on after 15:40. It also gives way to the evening
  breadth run whenever that is due.
- **Holidays learnt the hard way.** The exchange calendar holds recent years
  only. A day with no file in either format that is at least 3 days old is
  remembered in `system_settings` (`marketintel.nofile.<dataset>`), never asked
  for again and never counted as missing. A newer day with no file is simply
  not published yet.
- **Weekends are skipped** unless the calendar lists a special session
  (Muhurat trading, a Saturday budget session).

To run or resume one now:

```bash
curl -X POST -H "Authorization: Bearer $TOKEN" https://<desk>/api/MarketIntelligence/backfill/breadth
# breadth | participant-oi | global-daily | all
```

Inside market hours the answer says it is queued and starts after 15:40.

**FII/DII cash history:** no reliable official source was found.
- NSE's `api/fiidiiTradeReact` (and `fiidiiTradeNse`) answer the latest day
  whatever dates are asked.
- NSDL's FPI reports are forms-driven pages, did not answer when tried, and
  cover FPIs only, with no DII figures.

`market_cash_flows` keeps accumulating one day each evening, as before (since
17 Sep 2026).

## News scoring

`NewsScoringScheduler` runs `python -m analysis news-score` every 10 minutes
from 06:00 to 23:30 IST. The scorer is a local model on the CPU (FinBERT),
with no paid API and no key. See `analysis/news.py`.
- **Not during the session.** A run peaked at 1.25 GB on the server, so on a
  trading day it keeps out of 08:40-15:40: the morning job starts every
  strategy runner at 08:45. The 08:50 forecast uses the scores that exist by
  then (a headline first seen after 08:40 counts as unscored), and the
  session's headlines are scored from 15:40.
- **The server needs its packages.** The desk's deploy does not install
  Python packages: torch (the CPU build) and transformers were installed by
  hand on 27 Sep (`pip install "torch>=2.6" --index-url
  https://download.pytorch.org/whl/cpu`, then `pip install -r
  src/AlgoTrading.PythonEngine/requirements.txt`). The first run downloads
  FinBERT (~440 MB) into `data/models/`.
- **One run at a time.** The next is counted from the start of the last, and
  never begins while one runs. A run is stopped after 20 minutes.
- **Below normal priority.** The API starts it at nice 10 and the script
  lowers itself by 10 more (nice 19 in all): the box also trades.
- **A failure** is logged every time and sent to the System channel at most
  once an IST day (`marketintel.newsscore.lastFailureNotice`). The unscored
  rows wait; nothing is lost.
- **Until `analysis/news.py` exists** the scheduler waits and says so once.

The scorer writes only the six scoring columns (`Sentiment`, `Importance`,
`Symbols`, `Topics`, `ScoredUtc`, `ScoreModel`), never a recorded column.
`NewsTablesContractTests` reads its SQL and fails if it names a column or
table the model does not have, or writes anything else.

## Switches

`appsettings.json`, section `MarketIntelligence`; all `true` unless set to
`false`:

| Switch | Turns off |
| --- | --- |
| `NewsEnabled` | the news recorder |
| `AnnouncementsEnabled` | filings and the board-meeting calendar |
| `QuoteSnapshotsEnabled` | the morning snapshots |
| `GlobalDailyEnabled` | overseas daily bars and their history |
| `BreadthEnabled` | the evening breadth run |
| `BackfillEnabled` | the breadth and participant-OI history backfills |
| `NewsScoringEnabled` | the scorer's schedule |

None of them opens a broker or feed connection: they read public pages and
archives, and write only this module's tables. They are safe to leave on
anywhere.

## API

Admin only (`api/MarketIntelligence`).

`from` and `to` on news and announcements are instants: an ISO 8601 time with
its offset, or a `yyyy-MM-dd` date meaning that IST day, whole. A time
without an offset is refused, because IST and UTC are the whole morning apart.

| Endpoint | Returns |
| --- | --- |
| `GET news?from&to&category&q&skip&take` | headlines, newest first seen first; `q` matches title or summary, any case |
| `GET announcements?symbol&from&to&skip&take` | filings, newest broadcast first |
| `GET calendar?from&to` | board meetings by date (default: the next 30 days) |
| `GET global/daily?symbol&from&to` | overseas bars (default: 30 days; all symbols at most 400 days) |
| `GET global/snapshots?key&date` | every snapshot taken on an IST date (default: today) |
| `GET breadth?from&to` | breadth per session (default: 60 days) |
| `GET status` | see below |
| `POST backfill/{dataset}` | queues a backfill run (202) |

Paged answers carry `total`, `skip`, `take` and `items`; `take` is 1-500.

## Status

`GET api/MarketIntelligence/status` is what the desk checkup reads.

For each recorder:
- its last attempt, last success, last message and last error;
- the sources failing right now ("BBC World: HTTP 503"), each logged once when
  it starts failing and once when it recovers;
- `rowsToday` and `latestRowUtc`, read from the tables, so they survive a
  restart (the fields above start afresh with each API process);
- `overdue`: enabled, inside its own window, and without a success for longer
  than its schedule allows. It is counted from the later of the last success,
  the API's start and the window's opening, so a quiet night or a restart is
  not a fault.

For each backfill:
- its state (`idle`, `running`, `waiting` for market hours to end, `done`);
- its first and last stored date;
- `missingSessions`: NSE sessions since 2020 up to the latest published one
  with nothing stored and no known reason. This is the backfill's progress;
- the days known to have no file.

Overseas markets report their first date, last date and rows per symbol;
their missing sessions are not counted, because they keep their own holidays.

## Known gaps

- **No news before the recorder started.** A headline that scrolled off a
  feed while the API was down is lost; feeds carry 10-60 items, hours to days.
- **Filings are not backfilled.** NSE answers past days by date (checked), but
  a backfilled filing's `FirstSeenUtc` would be the backfill's time.
  `AnnouncedUtc` is NSE's real broadcast time, so a history for training is
  possible later. That is a decision for the models, not taken here.
- **Not stored:** NSE's longer board-meeting description (`bm_desc`) and a
  filing's industry (`smIndustry`). The contract has no column for them.
- **No official FII/DII cash history** (above).
- **Neither source of global prices is a paid feed.** Yahoo and NSE IX can
  change or refuse without notice. Each failure is reported per market.
- **The 08:45 snapshot** depends on the API being back within 10 minutes of
  the morning restart. Otherwise the model's latest snapshot before 08:50 is
  the 08:30 one.
- **Breadth's EQ series includes ETFs**, as NSE's does.

## What it will not do

- Change a recorded row's first-seen stamp, or delete a row.
- Fetch history during market hours on a trading day.
- Open a broker or data-feed connection, or call a paid API.

**Status (27 Sep 2026):** built and tested; not deployed.
