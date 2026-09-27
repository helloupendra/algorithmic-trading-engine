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
the models on history and registers them (run by hand after a model change).

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

## Running it

From `src/AlgoTrading.PythonEngine`, with the repository's virtualenv
(numpy, which pandas already brings, and psycopg2; nothing new). The API's
scheduler runs `issue` and `score` the same way.

```bash
cd src/AlgoTrading.PythonEngine
../../.venv/bin/python -m analysis backtest --dry-run            # report only: logs/analysis/backtest-<date>.md
../../.venv/bin/python -m analysis backtest                      # report + register the four model versions
../../.venv/bin/python -m analysis issue --session 2026-09-28 --dry-run   # print the payloads
../../.venv/bin/python -m analysis issue --session 2026-09-28    # before 09:15 IST
../../.venv/bin/python -m analysis score --dry-run               # print the scores
../../.venv/bin/python -m analysis score [--from 2026-09-01 --to 2026-09-28]
```

- It reads the repo-root `.env`: `POSTGRES_*` (the connection is opened
  read-only), `API_BASE_URL`, and `ENGINE_SERVICE_USERNAME` /
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
