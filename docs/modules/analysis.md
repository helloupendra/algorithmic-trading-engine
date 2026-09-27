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
| `range` | The session's high − low, as % of the previous close | `range.har` (log-range on 1-, 5- and 22-day mean log ranges), `range.har-vix` (the same plus India VIX's previous close, an expiry-day and a Monday flag) | mean range of the last 20 sessions | \|ln(actual) − ln(predicted median)\| |
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
  "description": "Log-range on 1-, 5- and 22-day mean log ranges, plus India VIX's previous close, expiry-day and Monday flags.",
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
