# Analysis: forecasts with proof

The desk's forecasting module. It exists to answer one question honestly: **is
there anything about the market this desk can predict better than a simple
baseline — and can it prove it?** Two weeks of strategy research
(150 technical configurations, walk-forward ML, market-structure rules with a
holdout) found no edge in predicting the *direction* of NIFTY intraday. The
things research does find predictable are volatility and regime, so version 1
forecasts those, with direction kept alongside as a control.

## The four rules that make it proof

1. **Written before, scored after.** A forecast is stored by the API with the
   server's own clock (`IssuedUtc`), and the API refuses one for a session that
   has already opened. The outcome can be written only after that session's
   close. Nobody can back-date a forecast or change it afterwards.
2. **Probabilities, not verdicts.** "38% chance of a trend day", "80% chance the
   range is between 0.7% and 1.5%". The scoreboard checks calibration: of the
   times it said 30-40%, did it happen 30-40% of the time?
3. **Always against a baseline.** Every forecast carries the baseline's
   forecast for the same session (a 20-day average range; a trailing base
   rate). A model that cannot beat it is not useful, however clever.
4. **A fixed pass mark, set before the data.** A model is *Proven* only when at
   least 60 live, scored forecasts beat the baseline with the lower end of a 95%
   bootstrap confidence interval above zero; *Retired* when after 120 the upper
   end is below zero. History (the walk-forward backtest) is shown, but it is
   never what makes a model Proven — only live forecasts are.

## Version 1

For NIFTY (`NSE:NIFTY50-INDEX`), BANKNIFTY (`NSE:NIFTYBANK-INDEX`) and SENSEX
(`BSE:SENSEX-INDEX`), issued on each trading day at **08:50 IST**, scored after
**15:50 IST**:

| Target | What | Models | Baseline | Loss (lower is better) |
| --- | --- | --- | --- | --- |
| `range` | The session's high − low, as % of the previous close | `range.har` (log-range on the logs of the 1-, 5- and 22-day mean ranges), `range.har-vix` (the same plus India VIX's previous close, an expiry-day and a Monday flag) | mean range of the last 20 sessions | \|ln(actual) − ln(predicted median)\| |
| `trend` | A trend day: \|close − open\| ≥ 0.6 × (high − low) | `trend.logit` | trailing 250-session base rate | Brier score |
| `direction` | close > open | `direction.logit` | trailing 250-session base rate | Brier score |

Data: 5-minute index candles (NIFTY from Aug 2020, the rest and India VIX from
Aug 2021), aggregated to sessions of 09:15-15:30 IST. Walk-forward: models are
refitted on an expanding window; 2021-2024 is design, 2025 validation, and
2026 the holdout, looked at once and reported separately.

## API contract

All times UTC in ISO 8601; `sessionDate` is the IST calendar date
(`yyyy-MM-dd`). JSON is camelCase. Writes are for the Service account and
admins; reads for admins (and later traders with the `analysis` module).

### Models

`POST /api/Forecasts/models` — register or update a model version (upsert on
`key` + `version`).

```json
{
  "key": "range.har-vix",
  "version": "2026-09-27.1",
  "target": "range",
  "description": "Log-range on the logs of the 1-, 5- and 22-day mean ranges, plus India VIX's previous close, expiry-day and Monday flags.",
  "backtest": {
    "design":     { "from": "2021-08-04", "to": "2024-12-31", "n": 850, "loss": 0.231, "baselineLoss": 0.262, "skill": 0.118 },
    "validation": { "from": "2025-01-01", "to": "2025-12-31", "n": 247, "loss": 0.225, "baselineLoss": 0.251, "skill": 0.104 },
    "holdout":    { "from": "2026-01-01", "to": "2026-09-25", "n": 180, "loss": 0.240, "baselineLoss": 0.259, "skill": 0.073 },
    "byUnderlying": { "NIFTY": { "n": 1277, "skill": 0.10 }, "BANKNIFTY": { "n": 1277, "skill": 0.09 }, "SENSEX": { "n": 1277, "skill": 0.11 } },
    "configurationsTried": 4,
    "notes": "free text"
  }
}
```

`GET /api/Forecasts/models` → the list, `backtest` as stored.

### Forecasts

`POST /api/Forecasts` — issue one forecast. **409** if the session for
`sessionDate` has already opened on the underlying's exchange (09:15 IST for
NSE/BSE) or a forecast for the same `modelKey` + `modelVersion` + `target` +
`underlying` + `sessionDate` exists; **400** if the model version is not
registered. Returns the stored forecast with `id` and the server's `issuedUtc`.

```json
{
  "modelKey": "range.har-vix",
  "modelVersion": "2026-09-27.1",
  "target": "range",
  "underlying": "NIFTY",
  "sessionDate": "2026-09-28",
  "prediction": { ... },
  "baseline": { ... },
  "inputs": { ... }
}
```

`prediction` / `baseline` by target:

- `range`: `{ "median": 1.02, "low80": 0.68, "high80": 1.55, "prevClose": 24650.3,
  "points": { "median": 251.4, "low80": 167.6, "high80": 382.1 },
  "buckets": { "quiet": 0.31, "normal": 0.45, "wild": 0.24 }, "bucketEdges": [0.75, 1.25] }`
  (percentages of the previous close; bucket edges are the underlying's
  terciles in the training data, so each bucket's baseline is about 1/3)
- `trend` and `direction`: `{ "p": 0.38 }`

`inputs` is what the model saw (e.g. `{ "vixPrevClose": 11.9, "r1": 0.84, "r5": 0.97, "r22": 1.05, "expiryDay": false }`), shown on the page.

`POST /api/Forecasts/{id}/outcome` — score it. **409** before the session's
close (15:30 IST) or if already scored.

```json
{
  "outcome": { "open": 24660.1, "high": 24790.4, "low": 24540.0, "close": 24771.2, "range": 1.016, "bucket": "normal", "trendDay": false, "efficiency": 0.44, "up": true },
  "scores":  { "loss": 0.004, "baselineLoss": 0.061, "metrics": { "covered80": true, "brier": 0.19, "baselineBrier": 0.22 },
               "calibration": [ { "p": 0.31, "y": 0 }, { "p": 0.45, "y": 1 }, { "p": 0.24, "y": 0 } ] }
}
```

`loss` / `baselineLoss` are the target's loss from the table above (lower is
better). `calibration` lists every probability the forecast stated with 1 if
that event happened, else 0 (one entry for `trend`/`direction`, three for the
`range` buckets).

`GET /api/Forecasts?from=2026-09-28&to=2026-09-28&target=range&underlying=NIFTY`
→ forecasts, newest session first, each:

```json
{ "id": 1, "modelKey": "range.har-vix", "modelVersion": "2026-09-27.1", "target": "range", "underlying": "NIFTY",
  "sessionDate": "2026-09-28", "issuedUtc": "2026-09-28T03:20:04Z",
  "prediction": { ... }, "baseline": { ... }, "inputs": { ... },
  "outcome": null, "scores": null, "scoredUtc": null }
```

`GET /api/Forecasts/scoreboard` → one row per model version, target and
underlying (and an `"ALL"` row per model version across underlyings):

```json
{ "modelKey": "range.har-vix", "modelVersion": "2026-09-27.1", "target": "range", "underlying": "ALL",
  "description": "...", "liveCount": 42, "meanLoss": 0.228, "meanBaselineLoss": 0.259, "skill": 0.12,
  "diffCiLow": -0.004, "diffCiHigh": 0.061,
  "status": "testing", "statusReason": "42 of 60 scored forecasts; the confidence interval still includes no improvement",
  "coverage80": 0.81,
  "calibration": [ { "from": 0.3, "to": 0.4, "n": 18, "meanP": 0.35, "hitRate": 0.39 } ],
  "firstSession": "2026-09-28", "lastSession": "2026-11-20",
  "backtest": { ... as registered ... } }
```

`skill` = 1 − meanLoss / meanBaselineLoss; `diffCiLow`/`diffCiHigh` are the 95%
bootstrap interval (2,000 resamples, fixed seed) of the mean of
`baselineLoss − loss`. Status: `collecting` under 20 scored; `proven` at ≥ 60
with `diffCiLow > 0`; `retired` at ≥ 120 with `diffCiHigh < 0`; otherwise
`testing`. Calibration bins are tenths of probability.

### Schedule

The API's `ForecastScheduler` runs `python -m analysis issue --session <date>`
at 08:50 IST and `python -m analysis score` at 15:50 IST on NSE trading days,
and `score` once at start-up to catch up. `python -m analysis backtest` refits
the models on history and registers them (run by hand after a model change);
`python -m analysis backtest-v2` does the same for version 2, under its own
rule (see Version 2). The API also runs `python -m analysis news-score` every
10 minutes, which scores the headlines the morning's forecasts record.

## How the models work

Code: `src/AlgoTrading.PythonEngine/analysis/`. One `fit` and one `predict`
(`models.py`) produce every forecast — the walk-forward history and the
morning's `issue` alike — and one `scoring.score` scores both, so a backtest
number and a scoreboard number mean the same thing.

### Sessions

A session is one IST trading day, 09:15-15:30: the first bar's open, the
highest high, the lowest low, the last bar's close. Postgres folds the bars
(one query per symbol and source; the server moves ~1,500 rows an index, not
~110,000), and Python decides which days count:

| Source | Used for | A day counts when |
| --- | --- | --- |
| `candles`, resolution `5` | all history | ≥ 60 of 75 bars, first bar by 09:20, last bar from 15:15, ≤ 10% flat bars |
| `live_bars`, `1m` | the last 14 days, only where candles have no complete day | ≥ 300 of 375 bars, first bar by 09:16, last bar from 15:25, ≤ 10% flat bars |

`live_bars` matter because the nightly archive writes a day's candles at
23:50 IST: at 15:50 today exists only there. Weekends are dropped (Budget
Saturdays and Sundays, the 2024 disaster-recovery drills), and so is a
Muhurat hour, by the bar count. "Flat" bars (high = low) are how a vendor
fills a gap with the last price — Dhan's history runs flat through the 2021
Muhurat day — so a day made mostly of them is not a quiet day and is dropped.
India VIX is the exception: it is recomputed in small steps rather than with
every trade, so only a VIX day with no movement at all is dropped. Every
dropped day is listed with its reason in the backtest report.

India VIX's candles are checked every trading night right after the archive:
the last 20 trading days, each missing bar asked of the history vendors, and
a day still short of what this table needs reported as "India VIX gap not
filled" (see [India VIX in the data module](data_module.md#india-vix)). It
exists because 22-24 Sep 2026 went missing unnoticed (incident #204).

The previous close is the previous complete session's close. A day dropped
for missing data makes the next day's range a percentage of a close two days
old; the report's dropped-day count says how often that happens.

An **expiry day** is a day an index option of that underlying expires: the
exchanges' own bhavcopies (`SeedData/index_option_expiries.json`) up to the
day they were read to, then the instrument master's listed expiries, then
the weekday rule (NIFTY Tuesdays, SENSEX Thursdays, BANKNIFTY's last-Tuesday
monthly, moved back over holidays). Refresh the seed file with
`tools/option_expiry_calendar.py` now and then so the rule is rarely needed.

### Inputs

Everything a model sees for session *t* comes from sessions before *t*,
plus two facts known in advance (expiry day, Monday). A test replaces every
session from *t* on — including *t* itself — with nonsense and checks that
the forecast for *t* does not move. Ranges are percentages of the previous
close.

| Input | Definition | Models |
| --- | --- | --- |
| `r1`, `r5`, `r22` | mean range of the last 1, 5 and 22 sessions | range.har, range.har-vix (as logs) |
| `vixPrevClose` | India VIX's close on the previous session | range.har-vix (as log), the logits |
| `expiryDay`, `monday` | 0/1 facts about the forecast session | range.har-vix, the logits |
| `rangeRatio` | `r1` / mean range of the last 20 sessions | the logits (as log) |
| `prevReturn` | previous close-to-close return, % | the logits |
| `prevEfficiency` | previous \|close − open\| / (high − low) | the logits |
| `vixChange5` | `vixPrevClose` − VIX's close five sessions earlier, points | the logits |

The HAR regressors are the logs of the *mean ranges*, not means of log
ranges as the table under Version 1 puts it; the code is the definition.
`inputs` in each forecast also carries `prevSession`, `trainingSessions` and
`trainedThrough`, so a stale input shows on the page.

### Range: `range.har`, `range.har-vix`

OLS with an intercept: ln(range) on ln r1, ln r5, ln r22 (plus ln VIX, the
expiry and Monday dummies for `-vix`). The forecast is a log-normal: centre
μ = the fitted log-range, spread σ = the training residuals' standard
deviation. From it: median e^μ, 80% interval e^(μ ± 1.2816σ), and the
probabilities of three buckets whose edges are the underlying's terciles of
range in the training window (so each held a third of training days).

The baseline in the same shape: median = the mean range of the last 20
sessions; its 80% interval = that mean times the 10th and 90th percentiles of
its own log errors in the training window; its bucket probabilities = the
buckets' shares in the training window (about 1/3 each).

A caveat the numbers must carry: ranges are skewed, so a 20-session *mean*
sits above the median it is scored as. Part of any range model's skill is
that gap alone. The backtest report measures it against a 20-session
geometric mean (the same baseline in logs); that check is reported, not
registered, and the contract's baseline is unchanged.

### Trend and direction: `trend.logit`, `direction.logit`

Logistic regression on the seven inputs above, standardised with the
training window's mean and standard deviation, fitted by iteratively
reweighted least squares with a ridge penalty of 1.0 on every coefficient
but the intercept (it keeps the fit finite; with 250+ sessions it barely
shrinks). The baseline is the trailing 250-session rate of trend days (or up
days). Direction is a control: the desk's research found nothing that
predicts it, so skill there is a reason to look for a leak.

### Fitting, the backtest and what "configurations tried" counts

A model forecasts only after 250 usable training sessions. `issue` fits on
every session before the forecast day; the backtest refits on the first day
of each month and forecasts that month's sessions (expanding window).
Periods: design to 2024-12-31, validation 2025, holdout 2026 on; nothing was
chosen on validation or holdout — the inputs, windows and penalty are fixed
in code. Every model is scored on the same sessions (those all four could
forecast; India VIX starts Aug 2021), pooled across the three indices, which
are not independent: the 95% interval of mean(baselineLoss − loss) resamples
session dates (2,000 resamples, fixed seed), keeping a day's three forecasts
together.

Each period in the registered `backtest` carries the contract's keys plus
`diffCiLow`/`diffCiHigh`, `calibration` and `baselineCalibration` (tenths),
and for range `coverage80`, `baselineCoverage80`, `bucketBrier` and
`baselineBucketBrier` (the three-bucket Brier score summed over buckets, 0
to 2 — the same sum live `metrics.brier` holds). A period with no forecasts
is `null`, not a row of nulls. `byUnderlying` covers all three periods, and
also carries `loss` and `baselineLoss`. `configurationsTried`
counts the configurations evaluated per target, siblings included (range: 2,
trend: 1, direction: 1). It is a constant in `backtest.py`; raise it, and the
version, with every change made after looking at a result.

### Scoring

Range: loss |ln actual − ln median| for the model and the baseline,
`covered80`, the three-bucket Brier scores, and one calibration entry per
bucket. The range is measured against the previous close the forecast stated
(`prediction.prevClose`). Trend and direction: Brier scores and one
calibration entry; their `outcome.bucket` is `null`, because bucket edges
belong to a range forecast and they state none (their `outcome.range` uses
the previous stored session's close). Today's session is scored from the live
1-minute bars, so its close is the last traded index value at 15:29, not the
exchange's official close (an average of the last half hour); on a day whose
body sits at the 0.6 trend threshold or near zero that can decide `trendDay`
or `up`. A session whose bars are not complete waits for the next run and
becomes an error after four days, so no forecast stays unscored quietly.

## Version 2

Version 1 reads only the index's own sessions and India VIX. Version 2
(`MODEL_VERSION_V2`, `2026-09-27.2`) asks the same three questions with what
else a desk knows at 08:50 IST: what the rest of the world did overnight, how
foreign funds are positioned, how broad the previous session was, and whether
the day holds an RBI, Fed, Budget or data release. It keeps v1's fit, its
baselines, its windows and its ridge penalty; only the inputs grow. It runs
**beside** v1, never instead of it: the morning issues both, the evening scores
both, and the scoreboard shows each version's own row.

| v2 model | Compared with | Adds to v1's inputs |
| --- | --- | --- |
| `range.har-vix-cues` | `range.har-vix` | the *sizes* of the overnight moves (a range has no sign), US VIX (log), FII net long and its last change, how lopsided breadth was, the heavyweights' dispersion (log), event flags |
| `trend.logit-cues` | `trend.logit` | the same groups with their signs: SPX, US VIX change, Asia, the rupee, Brent, FII net long and its 1- and 5-day changes, % advancing, net 52-week highs, heavyweights' return and dispersion, event flags |
| `direction.logit-cues` | `direction.logit` | as `trend.logit-cues`. Still a control: skill here is a reason to look for a leak |

Code: `analysis/context.py` builds the inputs, `models.py` fixes which model
reads which (`RANGE_EXTRAS`, `LOGIT_EXTRAS`), `backtest.py` compares the versions.

### The inputs and their point-in-time rules

One rule covers all of them: **nothing dated on or after the forecast session
is read, and nothing published after 08:50 that morning.** Each group has a
test whose fixture holds a row dated on the session with a value no honest
input could produce, and checks the input did not see it; and the v1 test
that corrupts every session from *t* on is repeated for the v2 models with
every context table corrupted too.

"The previous session" is the index table's previous row, the session v1's
inputs come from.

| Group | Inputs | Source | Rule |
| --- | --- | --- | --- |
| Overnight global | `spxRet`, `ndxRet`, `djiRet`, `esRet`, `brentRet`, `dxyRet`, `usdinrRet` (%), `usVixChange`, `us10yChange` (points), `usVix` (level) | `market_global_daily` | Rows dated before the session. These markets close after India does, so each is measured **since India's previous close**: the last row dated before the session against the last row dated before the previous session. On an ordinary day that is the last session's move; after an Indian holiday it adds up every session India missed; if they did not trade since India's close it is 0. A series whose last row is more than 5 days old has stopped arriving: missing, not flat. |
| Asia | `n225Ret`, `hsiRet`, `ks11Ret`, `asiaRet` (their mean) | `market_global_daily` | The last session dated before the forecast session (at 08:50 IST Tokyo has been open three hours: today's row is a session in progress). Asia closes before India, so this move is mostly inside India's previous session already; the models can say what it is worth. Up to 7 days old (Golden Week, the Lunar New Year). |
| FII positioning | `fiiNetLong` = (long − short) / (long + short) of FII index futures, %; `fiiChange1`, `fiiChange5` (points) | `market_participant_oi`, ClientType `FII` | The last file dated before the session, and it must be the previous session's (NSE publishes it that evening): an older one is missing, not reused. |
| Breadth | `adRatio`, `pctAdvancing`, `netHighsLows`, `netHighsLowsPct` (of stocks traded) | `market_breadth_daily`, NSE | Same rule as FII. |
| Heavyweights | `hwRet` (mean previous-session return, %), `hwDispersion` (their standard deviation), `hwCount` | `candles`, 5-minute, folded into sessions like the indices | RELIANCE, HDFCBANK, ICICIBANK, INFY, TCS, BHARTIARTL, ITC, LT, KOTAKBANK, AXISBANK: in NIFTY 50 from before 2020 to today, so each day's set is that day's members (HDFC Ltd, merged in 2023, is left out for that reason). Equal-weighted. A move over 20% is a split or bonus in unadjusted candles and sits the day out; fewer than 7 stocks and the inputs are missing. |
| Events | `majorEvent` (RBI, Fed or Budget reaches this session), `majorEve` (one is due the next session), `majorAfter`, `dataRelease` (US CPI, US jobs, India CPI); `events` lists them by category in words | `market_events` and the seed file | An event reaches its own day if announced before 15:30 IST (or at no stated time), else the next trading day: RBI at 10:00 that day, a US print at 18:00 IST and the Fed at 23:30 the next morning. A weekend Budget reaches the Monday (weekend sessions are not in the table). Dates are published in advance — except an emergency decision (title with "unscheduled" or "off-cycle": the Fed's March 2020 cuts, RBI's 4 May 2022 hike). That one never flags the day before, and flags its own session only if it was out before 08:50 IST that morning (the Fed's cut at 02:30 IST on Monday 16 March 2020 was; RBI's 14:00 hike was not); it always flags the session after. |

The event history is the seed file `SeedData/market_events.json` (every row
with the official page it was copied from; see "Event history" below) plus
whatever admins add on the Market factors page. An event added afterwards in
one of these six categories that was *not* announced in advance must say
"unscheduled" or "off-cycle" in its title, or the backtest would learn from a
"day before" nobody could have known.

Every v2 forecast records all of these in `inputs`, raw (the models'
transforms are not what a reader needs), the ones its model does not use
included. A v2 input that is missing on a morning — a series stopped
arriving, the FII file was not fetched — stops that v2 forecast and names the
table (`no fiiNetLong (market_participant_oi FII)`); v1's forecasts go out
regardless.

### Event history

The backtest can only learn an event effect from a calendar that goes back
as far as the candles, so `SeedData/market_events.json` (version 2, 370
events) now runs from January 2020 to the last published date. Every row
carries the official page it was read from; the file's `sources` block says
how each category was read. The API adds rows it does not have yet when it
starts with a higher file version, so an admin's deletion survives.

| Category | Events | From | To | Official source | Time (IST) |
| --- | --- | --- | --- | --- | --- |
| RBI policy | 44, 3 of them off-cycle | Feb 2020 | Feb 2027 (schedule) | each MPC resolution on rbi.org.in; the year's MPC schedule for future ones | when RBI's own live stream began (10:00; 12:00 on 6 Aug 2020, 14:00 on 4 May 2022); empty for 6 Feb 2020, 6 Apr 2023 and future meetings |
| Fed policy | 66, 3 of them unscheduled | Jan 2020 | Dec 2027 (schedule) | each FOMC statement's press release on federalreserve.gov; the FOMC calendar for future ones | the statement's "For release at" time: 2:00 pm US Eastern, 23:30 or 00:30 IST by US daylight saving |
| US CPI | 83 | Jan 2020 | Dec 2026 (schedule) | the BLS release archive and schedule, bls.gov | 8:30 am US Eastern, 18:00 or 19:00 IST |
| US jobs | 83 | Jan 2020 | Dec 2026 (schedule) | the BLS Employment Situation archive and schedule | as US CPI |
| Budget | 8 | Feb 2020 | Feb 2026 | the Budget speeches on indiabudget.gov.in (2026-27 on pib.gov.in) | 11:00, when PIB's live stream began |
| India CPI | 86 | Jan 2020 | Mar 2027 (calendar) | MoSPI's release on pib.gov.in each month, checked against the embargo line of MoSPI's own press-release PDF; MoSPI's advance release calendar for future ones | 17:30 until Oct 2024, 16:00 from 12 Nov 2024 |

Actual dates, not planned ones: the US shutdown of October-November 2025
cancelled October 2025's CPI and jobs reports and delayed the others around
them, and a second, short one moved January 2026's; RBI moved four meetings and brought
two forward in 2020. Left out on purpose: the RBI MPC's 3 November 2022
meeting (a report to the government, no decision), Fed notation votes
without a policy statement, and anything not yet published officially — the
2027 BLS schedule, the 2027-28 Budget date, India's CPI for November 2026
(its calendar date is a Saturday). The web page's category list for adding
an event by hand does not have "US jobs" yet; the seeded rows show under it
regardless.

### Live-only inputs

Three things have no history, so they cannot be backtested and **no model
reads them**. Every forecast, v1's included, records them under
`inputs.liveOnly` (`"usedByModels": false`), so that once there are enough
live sessions their value can be tested on forecasts written before the open:

- `giftNiftyGapPct`: the latest GIFT Nifty snapshot of the morning (the 08:45
  one, from `market_quote_snapshots`) against NIFTY's previous close, and the
  vendor's own `giftNiftyChangePct`. GIFT Nifty is a future: the gap includes
  its basis to spot, a few tenths of a percent.
- `news`: per `news_items` category, the headlines first seen between the
  previous session's 15:30 close and the cutoff — how many, how many scored,
  their mean sentiment and highest importance — and the same for NIFTY-50
  companies' NSE announcements. A score counts only if its `ScoredUtc` is
  before the cutoff too.
- `earningsToday`, `earningsSincePrev`: NIFTY-50 companies with results in
  `corporate_calendar` on the session, and from the previous session to it
  (weekend results included), as known at the cutoff (`FirstSeenUtc`).

The cutoff is the issue time, never later than the 09:15 open. A table that
is not there, or a part that fails, says `unavailable (…)` in its place: the
morning's forecasts never wait on them.

The Forecasts page shows this once per session, under the index cards, as
"Pre-open context", marked not used by the models: the GIFT Nifty gap and
snapshot time in IST, the earnings counts, and the news per category (India
and Global first, then the sectors, then the NIFTY-50 filings) with the
mean sentiment as a signed bar. A part recorded as a sentence is shown as
that sentence. Each card's "What the models saw" lists the other inputs
once, under the models that record them, with training sessions and
trained-through per model in a small table; a value the page has no layout
for is drawn as nested labels, never as JSON.

### Selection, and what counts as better

`python -m analysis backtest-v2` walks v1 and v2 forward together, with v1's
walk-forward and splits (design to 2024, validation 2025, holdout 2026), so
all seven models are scored on the same sessions, and compares each v2 model
with its v1 model session by session: the 95% interval of mean(v1 loss − v2
loss), resampling session dates as everywhere else.

- **A v2 model is registered only when that interval on validation lies
  wholly above zero.** Otherwise nothing is registered for that target and
  the report says version 2 added nothing there — a legitimate result, and
  the likely one for direction.
- **The holdout was already used.** v1's run looked at 2026 once. v2's
  holdout number is reported, marked "second look", and chooses nothing.
- **Nothing was tuned.** The inputs, their transforms, the windows and the
  penalty were written down before the tables they read existed, so before
  any v2 result could be seen. `configurationsTried`, counted cumulatively
  with v1: range 3, trend 2, direction 2 (`CONFIGURATIONS_TRIED_V2`). Raise it
  with any change made after looking.
- A registered v2 model's `backtest` carries its own periods against the
  baseline, as v1's do, plus `versusV1`: the comparison it was chosen on.
- More inputs fit noise more easily; the walk-forward refits on the past
  only, so an input that fits noise shows up as a worse v2 loss, not a better
  one. On synthetic data the tests plant a real overnight effect v1 cannot see
  (found and registered) and check that pure noise is not.
- Registration is still not proof. A v2 model becomes Proven the way v1's do:
  60 live forecasts with the interval above zero, against its baseline.

`issue` issues the v2 models the API has registered at `MODEL_VERSION_V2`
(`GET /api/Forecasts/models`). If none passed, the morning is v1 as before;
if the registry cannot be read, v1 still goes out and the run fails loudly.

### The news scorer (Phase 3)

`python -m analysis news-score [--limit N] [--dry-run]` scores the unscored
rows of `news_items` and `corporate_announcements`, oldest first seen first,
and writes six columns: `Sentiment`, `Importance`, `Symbols`, `Topics`,
`ScoredUtc`, `ScoreModel`. It is free and local: no API key, no paid service,
nothing leaves the server.

| Column | How |
| --- | --- |
| `Sentiment` (−1..1) | [FinBERT](https://huggingface.co/ProsusAI/finbert) (`ProsusAI/finbert`, pinned to revision `4556d130…`) on the CPU: P(positive) − P(negative). |
| `Symbols` | Rules (`analysis/symbols.py`), NSE cash equities from the instrument master: a short curated alias list for the NIFTY-50 names headlines use ("L&T", "Airtel", "SBI"), the master's company names of two or more words, and the ticker itself only as a capitalised token that is not a common abbreviation ("BSE", "IT", "GST"), and not in a headline written in capitals. Longer names win ("Reliance Power" is RPOWER), and "Reserve Bank of India" is blanked before anything else. An announcement adds its own company. |
| `Importance` (0–3), `Topics` | Rules (`analysis/newsrules.py`): keywords (results, RBI/Fed policy, Budget, M&A, block/bulk deals, SEBI orders, rating changes, guidance, defaults, FII flows, …), the announcement's subject line (a trading-window closure is 0, whatever it mentions) and the source; +1 when a NIFTY-50 company is named. |
| `ScoreModel` | `finbert@4556d1301521+rules-v1`, or `failed:<reason>` |

- **A malformed output** (probabilities that are not three numbers adding to
  1, a tag outside the vocabulary) is retried once on its own, then written
  as `failed:<reason>` with `ScoredUtc` set, so it is never retried again. A
  batch the model fails on is scored item by item.
- **Headlines are untrusted text.** They are only ever model input and SQL
  parameters, cut to 2,000 characters before the tokenizer; what is written
  back is validated against fixed ranges and vocabularies. Nothing in a
  headline can change what the scorer does.
- **Point in time.** Score close to arrival: a feature may use a score only
  if `ScoredUtc` is before its own cutoff, and a score written after 08:50
  does not count for that morning. FinBERT's training ended years before
  these headlines, so a late score cannot know what happened next — but the
  symbol dictionary is the instrument master as of scoring time.
- **FinBERT's limits.** It was trained on English financial news (the
  Financial PhraseBank), not on Indian markets. It reads "Rupee falls to
  record low" as strongly negative and "HDFC Bank Q2 profit beats estimates"
  as strongly positive, but on the test set it scored "Sensex tanks 800
  points as FIIs sell heavily" +0.04 — neutral — and it calls most
  exchange filings neutral. Treat its sentiment as one noisy input to be
  tested, not a reading of the market.
- **Cost guard.** `--limit` defaults to 200 and is capped at 1,000 a run; a
  backlog is worked off over runs. A Postgres advisory lock keeps two runs
  from scoring the same rows.
- **Weights.** Downloaded once (~440 MB) into `data/models/huggingface/` in
  the repository, which git ignores, so the API's service user needs no home
  directory and a redeploy keeps them; after that a run is fully offline.
- **Size**, for the 2-vCPU, 8 GB server that also trades: one model loaded
  once per run, one CPU thread, batches of 16 cut at 128 tokens, a niced
  process. Measured on an Apple M1 (torch 2.12.1): loading takes 6.5–7.7 s,
  64 headlines score in 1.3–1.5 s, and the process peaks at 630–760 MB
  resident. The server's CPU will be slower; the memory will not be larger.
- Exit status: 0 when the run completed (items given up on are recorded, not
  fatal); 1 when the tables, the model or the write failed, or when every
  item in a run failed, which is the model and not the headlines.

## Running it

From `src/AlgoTrading.PythonEngine`, with the repository's virtualenv
(numpy, which pandas already brings, and psycopg2; the news scorer alone
needs torch and transformers, in `requirements.txt` from the PyTorch CPU
index). The API's scheduler runs `issue`, `score` and `news-score` the same way.

```bash
cd src/AlgoTrading.PythonEngine
../../.venv/bin/python -m analysis backtest --dry-run            # report only: logs/analysis/backtest-<date>.md
../../.venv/bin/python -m analysis backtest                      # report + register the four model versions
../../.venv/bin/python -m analysis issue --session 2026-09-28 --dry-run   # print the payloads
../../.venv/bin/python -m analysis issue --session 2026-09-28    # before 09:15 IST
../../.venv/bin/python -m analysis score --dry-run               # print the scores
../../.venv/bin/python -m analysis score [--from 2026-09-01 --to 2026-09-28]
../../.venv/bin/python -m analysis backtest-v2 --dry-run         # v2 against v1: logs/analysis/backtest-v2-<date>.md
../../.venv/bin/python -m analysis backtest-v2                   # the same, and register the v2 models that pass
../../.venv/bin/python -m analysis news-score --dry-run --limit 20   # print scores, write nothing
../../.venv/bin/python -m analysis news-score                    # score up to 200 items
```

- It reads the repo-root `.env`: `POSTGRES_*` (the connection is opened
  read-only, except by `news-score`, which writes only its six columns),
  `API_BASE_URL`, and `ENGINE_SERVICE_USERNAME` /
  `ENGINE_SERVICE_PASSWORD` — it signs in as the engine service account, like
  the strategy runners. The service account needs the Forecasts writes and
  `GET /api/Forecasts` (the scorer reads the unscored forecasts).
- Register first: `issue` gets **400** for a model version the API does not
  know. Run `backtest` once after deploying a new `MODEL_VERSION`.
- `backtest --models` / `--underlyings` narrow a run for a look; a narrowed
  run registers nothing, because the sessions a model is scored on depend on
  which models run together.
- Exit status: 0 when everything was done or refused by the API as already
  done / too late (409); 1 when anything failed — stale inputs (the last
  complete session is not the previous trading day, or India VIX lacks it), an
  API error, a session still unscored after four days; 2 for bad arguments.
  The log line names what and why.
- It is light: one aggregate query per symbol and source, then seconds of numpy
  (~5 s for the full backtest on a laptop). Safe on the production server.
- `backtest-v2` needs the MarketIntelligence tables and their backfill, and
  the heavyweights' candles (ten more session queries). A v2 model with no
  validation forecast means the context does not reach 2025 yet: nothing is
  registered and the run exits 1, rather than reporting that v2 added nothing.
  Its report's "Context inputs" table shows how many sessions each input covers.
