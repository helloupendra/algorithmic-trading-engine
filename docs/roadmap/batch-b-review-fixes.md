# Batch B review — fix list

> **Round 2 is now DONE (2026-09-04 19:20) — fixed in the working tree, do not re-do it.**
> R2.1, R2.2 and R2.3 below were implemented directly in `strategies/logic_engine.py` (plus two new read helpers in
> `core/api_client.py`: `get_all_latest_quotes()` and `get_recent_ticks()`), and `tests/test_logic_engine.py` was
> rewritten to cover all three rules including their silent/no-data paths. Python suite: 160 tests OK.
> What each rule does now:
> * **VWAP** — `_session_vwap(symbol)` builds the session VWAP from stored 1m bars
>   (`Σ((h+l+c)/3 × volumeDelta) / Σ volumeDelta`, newest IST session only) and returns `(value, "vwap"|"ltp")`.
>   The equity breakout only fires when the source is `"vwap"`, so `spot > spot` can no longer trigger it.
>   Verified live: HDFCBANK → `(714.56, 'vwap')`, NIFTYBANK index → `(57480.25, 'ltp')` + one-time warning.
> * **Order-book imbalance** — `_top_of_book(symbol)` reads `bidSize`/`askSize` from `/api/LiveData/ticks` for the
>   **ATM CE contract** (index ticks carry no depth) and returns `None` when either is null; the rule is skipped in
>   that case and the alert text says top-of-book, not total depth. Verified live: index → `None`,
>   `NSE:BANKNIFTY26SEP57500CE` → `(30.0, 90.0)`.
> * **OI shift** — `_highest_oi_strikes(underlying, atm)` resolves the real chain (`get_expiries` + `get_option_chain`,
>   cached per expiry), reads `openInterest` from `/api/LiveData/latest/all`, and picks the highest-OI CE and PE strike
>   within `strike_window` strikes of the ATM. `atm_strike + 200` is gone. Returns `(None, None, step)` when the feed
>   has no OI — which is the case today, so the rule stays silent and warns once instead of firing on every ATM tick.
> The remaining "minor" note (icon dependency) is still open. Everything above the checklist is historical context.
>
> **Round 2 (2026-09-04 18:00), after commit `093b054` "Fixes for Batch B Review".**
> Almost everything below is now done — see the checklist at the end of this file. Three defects remain, all in
> `strategies/logic_engine.py`, and they share one root cause: **the rules read fields that the platform's quote
> endpoint does not return.** Verified live against the running API:
>
> ```
> GET /api/LiveData/latest?symbol=NSE:NIFTYBANK-INDEX
> keys → close, dataType, delta, gamma, high, impliedVolatility, lastTradedPrice, low, open,
>        openInterest, symbol, theta, updatedUtc, vega, volume
> ```
>
> **R2.1 — the VWAP rule still compares spot to itself.** `_get_vwap_or_ltp` (logic_engine.py:180-190) reads
> `quote["volumeWeightedAveragePrice"]`, which does not exist in that response (nor anywhere in the C# contracts), so
> `vwap` is always 0 and the function falls back to the LTP. Rule 1-equity therefore still tests `spot > spot`.
> **Fix:** compute the session VWAP from stored 1-minute bars —
> `GET /api/LiveData/bars?symbol=<sym>&resolution=1m&take=500`, keep today's session bars, then
> `Σ((high+low+close)/3 × volumeDelta) / Σ volumeDelta`; fall back to LTP only when the volume sum is zero and say which
> one was used in the alert text.
>
> **R2.2 — the order-book imbalance rule can never fire.** `_get_level2_depth` (logic_engine.py:192-198) reads
> `totalBuyQuantity` / `totalSellQuantity`, falling back to `bidSize` / `askSize`. **None of those four fields exist in
> the quote response**, so it always returns `(0.0, 0.0)` and `ask > ratio * bid` is `0 > 0` → false, forever.
> **Fix:** either read the depth from the tick store (`GET /api/LiveData/ticks?symbol=&take=1` returns `bidSize` /
> `askSize` — verify before relying on it) and skip the rule when they are null, or drop the rule until the ingestor
> persists depth. Do not leave a rule that silently never fires.
>
> **R2.3 — the OI-shift rule is a dummy that will emit false alerts.** logic_engine.py:293-294:
> ```python
> # Dummy logic implemented since Fyers API client lacks get_option_chain
> current_highest_ce_oi_strike = inp.atm_strike + 200
> ```
> The comment is wrong — `PlatformApiClient.get_option_chain(...)` exists (the backtest `ContractResolver` uses it) and
> `GET /api/LiveData/latest/all` returns `openInterest` per symbol. Because the "highest OI strike" is just
> `atm + 200`, the stored value moves with the ATM, so **every downward ATM tick fires a bogus "call writing shifted"
> alert**. That is worse than the rule being absent.
> **Fix:** resolve the chain for the current expiry (`get_option_chain`), read `openInterest` for those symbols from
> `/api/LiveData/latest/all`, take the CE and PE strikes with the highest OI within `strike_window` strikes of the ATM,
> and alert only when either shifts by ≥ `oi_shift_steps` strikes. If the OI data is missing (all zeros), skip the rule
> and log it once — never fabricate a strike.
>
> **Also:** the Python suite was red after `093b054` — `tests/test_logic_engine.py:48` built a `StrategyInput` without
> the required `mode` argument. I fixed that one line locally (uncommitted) and the suite is green again (149 tests).
> Commit it or re-apply it.
>
> **Minor, unchanged:** the icon dependency was swapped (`lucide-react` → `@radix-ui/react-icons`) rather than removed;
> the repo's convention is still inline SVGs in `web/src/components/icons.tsx`.
>
> ### Round-1 checklist — verified fixed in `093b054`
> 1 ✅ alerter launch (`--strategy LogicEngine` + integer `--strategy-id` from `StrategyCatalogService.StableId`, now in
> the new `AlertsSupervisor`) · 2 ✅ Telegram config wired (`appsettings.json` + `_gen_local_settings.py` maps
> `TELEGRAM_BOT_TOKEN` / `TELEGRAM_CHAT_ID`) · 3 ✅ `GET /api/Risk/status` and `killswitch/status` are `[Authorize]`,
> writes stay AdminOnly · 4 ✅ Alerts start/stop/test-e2e/logs are AdminOnly, status/events any authenticated ·
> 5.1 ✅ `GET /api/Risk/exposure` · 5.2 ✅ `KillSwitchActivated` / `KillSwitchDeactivated` / `LimitsChanged` audit rows ·
> 5.3 ✅ `MaxConcurrentRuns` enforced on start and deploy · 5.4 ✅ alerter pid persistence (`alerts.pid.<underlying>`) and
> adoption via `ProcessProbe` · 5.6 ✅ per-underlying log buffers · 5.7 ✅ test-e2e writes an `alert_events` row ·
> 6 ✅ event endpoints return DTOs with `AsNoTracking()` and `Math.Clamp(limit, 1, 500)`; limits validated server-side;
> `SimulationRunId` is `long?`; the exception handler shows details in Development; `RejectOrderAsync` uses its own
> scope; the dead v1 pages are deleted and `modules.ts` no longer says `legacy`.
> Cooldown (`cooldown_seconds`) and configurable `default_params` also landed.

---


Review of commit `fee4af3` "Complete Phase 2 Batch B (Risk & Alerts v2)" (and the phase-1 hotfixes in `8d43dbf`),
done on 2026-09-04 against the scope in `docs/roadmap/remaining-scope-risk-alerts-users-trader.md`.

**State of the build:** all three gates pass — `dotnet build src/AlgoTrading.Api` 0 errors, 146 Python tests OK,
`npx tsc -b && npx vite build` clean. The migration is additive (two `CreateTable`s) and is already applied to the live
database (`risk_events`, `alert_events` exist). `RiskLimitsStore` is correctly built on `IServiceScopeFactory` (no
captive `DbContext`), the `RiskViolationException` → 409 handler is registered first in the pipeline, the Redis channel
matches on both sides (`alerts:new`), and the V2 pages are routed at the existing URLs so the sidebar keeps working.

What follows is everything that still has to be done, in priority order. Items 1–4 are blocking: with them, the Alerts
module cannot work at all and two authorization defects remain.

---

## 1. BLOCKER — the alerter process still dies on launch

**Where:** `src/AlgoTrading.Api/Controllers/AlertsController.cs:112-122`

The launch passes `--strategy-id LogicEngine`, but `strategies/execution_runner.py:348` declares
`--strategy-id` as `type=int`; and `--strategy` (declared `required=True` at line 347) is **not passed at all** any more.

Reproduced from the repo root:
```
$ cd src/AlgoTrading.PythonEngine
$ PYTHONPATH=. ../../.venv/bin/python strategies/execution_runner.py --strategy-id LogicEngine --user-id 2 \
    --underlying BANKNIFTY --spot-symbol NSE:NIFTYBANK-INDEX --metrics-port 0
execution_runner.py: error: argument --strategy-id: invalid int value: 'LogicEngine'

$ PYTHONPATH=. ../../.venv/bin/python strategies/execution_runner.py --strategy-id 123 --user-id 2
execution_runner.py: error: the following arguments are required: --strategy
```

**Fix:** pass both arguments, with the id as the integer the catalog uses:
```csharp
processInfo.ArgumentList.Add("--strategy");
processInfo.ArgumentList.Add("LogicEngine");
processInfo.ArgumentList.Add("--strategy-id");
processInfo.ArgumentList.Add(StrategyCatalogService.StableId("LogicEngine").ToString());
```
(`StableId` is the same deterministic hash the rest of the API uses; `LogicEngine` is discoverable from the registry —
verified: `load_strategy_factories()` contains it even though `listed = False`.)

**Also fix here:** `Process.Start` failures still return `200 OK` with a success message. Collect per-target results and
answer 207/400 naming the target that failed.

**Acceptance:** `POST /api/Alerts/start` → `GET /api/Alerts/status` still reports the processes as running 30 seconds
later, and `GET /api/Alerts/logs` shows the runner's `[CONFIG]` line instead of an argparse error.

---

## 2. BLOCKER — Telegram delivery is dead end to end

- `src/AlgoTrading.PythonEngine/strategies/logic_engine.py:333` — the direct `send_alert_async(...)` call is commented
  out, so the engine no longer sends anything itself (it only publishes to Redis `alerts:new`).
- `src/AlgoTrading.Api/Services/AlertSubscriberService.cs:26-27` — the API-side sender reads
  `configuration["Telegram:BotToken"]` / `["Telegram:ChatId"]`. **Nothing populates those keys**: the repo keeps
  `TELEGRAM_BOT_TOKEN` / `TELEGRAM_CHAT_ID` in the root `.env`, and `scripts/_gen_local_settings.py` does not write a
  `Telegram` section into `appsettings.Local.json`.

Result: no alert reaches Telegram, and every stored row has `deliveredToTelegram = false`.

**Fix:** add the two keys to the generated local settings (`scripts/_gen_local_settings.py`, next to the existing
`RISK_*` mapping) and document them in `appsettings.json` as empty defaults; keep the dry-run behaviour when they are
blank. Decide explicitly who sends — API only (current design) is fine, but then say so in a comment in
`logic_engine.py` where the old call was removed, rather than leaving commented-out code.

**Acceptance:** with the keys set, an E2E test alert arrives in Telegram and its `alert_events` row has
`deliveredToTelegram = true`; with the keys blank, the row is still written with `false` and nothing throws.

---

## 3. BLOCKER — traders still get 403 on the kill-switch status

`src/AlgoTrading.Api/Controllers/RiskController.cs:13` is still class-level `[Authorize(Policy = AdminOnly)]`, and no
per-action override was added. Verified live with the `demo-trader` account:

```
GET /api/Risk/killswitch/status   →  403
```

`useKillSwitch` is called by `web/src/pages/trader/OverviewPage.tsx` and `web/src/pages/trader/DeployPage.tsx`, so both
trader pages still render a broken tile.

**Fix:** add a read-only status endpoint that any authenticated user may call — `GET /api/Risk/status` returning
`{isActive, updatedBy, reason, updatedUtc}` — either as a separate action decorated with
`[Authorize]` (overriding the class policy) or by moving the read to a small non-admin controller. Point
`useKillSwitch` at it. Every write (`activate`, `deactivate`, `limits`) stays AdminOnly.

**Acceptance:** the trader token gets 200 on the new status route and 403 on `activate`/`deactivate`/`limits`.

---

## 4. BLOCKER — the whole Alerts controller is still unauthenticated-by-role

`src/AlgoTrading.Api/Controllers/AlertsController.cs:15-16` has **no `[Authorize]` attribute**, so the deny-by-default
fallback only requires *any* signed-in user. Verified live with `demo-trader`:

```
GET /api/Alerts/status → 200
```

`start`, `stop` and `test-e2e` are equally open, so any trader can spawn Python processes on the host and fire Telegram
messages.

**Fix:** put `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]` on the class and mark only
`GET /api/Alerts/events` (and, if you want it, `GET /api/Alerts/status`) with a plain `[Authorize]` override.

**Acceptance:** the trader token gets 403 on `start`/`stop`/`test-e2e`/`logs` and 200 on `events`.

---

## 5. Scope items from the doc that were not implemented

These are not bugs in what was written, they are parts of batch B that are still missing:

1. **`GET /api/Risk/exposure`** — the live risk picture (active runs with their rules and P&L, totals). Without it the
   Risk page has no exposure section. Build it on `LiveRunHistoryBuilder` + `StrategyProcessRegistry`; do not write new
   P&L maths.
2. **Kill-switch audit** — `activate` / `deactivate` do not write a `risk_events` row (only `OrderRejected` does, in
   `RiskManagementService.RejectOrderAsync`). Add `KillSwitchActivated` / `KillSwitchDeactivated` rows with the actor
   and reason, and `LimitsChanged` rows (with before/after in `DetailsJson`) in `RiskLimitsStore.UpdateLimitsAsync`.
3. **`MaxConcurrentRuns` and `MaxRunsPerUser` are stored and editable but enforced nowhere.** Wire `MaxConcurrentRuns`
   into the existing concurrency check in `StrategyController` (effective cap = min(this, `StrategyRunnerOptions.MaxConcurrentProcesses`));
   `MaxRunsPerUser` belongs to batch C (per-user cap for non-admins).
4. **Alerts supervision** — no pid persistence and no adoption, so an API restart orphans the three processes and the
   status flips to "not running" while they keep going. Copy the ingestor pattern exactly:
   `IProcessSettingsStore` keys `alerts.pid.<underlying>`, liveness + command-line check via `ProcessProbe`,
   `status` reporting `{isRunning, managed, processes:[{underlying, processId, source, startedUtc}]}`, and a stop that
   kills adopted processes too.
5. **Alerts targets are still hardcoded** (three underlyings + fixed metrics ports). Move them to a `system_settings`
   key `alerts.targets`, seeded with BANKNIFTY / NIFTY / SENSEX and their correct spot symbols
   (`NSE:NIFTYBANK-INDEX`, `NSE:NIFTY50-INDEX`, `BSE:SENSEX-INDEX` — note the page's old `NSE:BANKNIFTY-INDEX` entry
   never resolved), and let the start request override them.
6. **Per-underlying logs** — still one shared 100-line ring buffer; the spec asked for per-underlying buffers plus
   retention for the last stopped set (`GET /api/Alerts/logs?underlying=`).
7. **`POST /api/Alerts/test-e2e` does not write an `alert_events` row.**
8. **The Python rule work was not done.** `_get_vwap_or_ltp` (logic_engine.py:195-205) still returns the LTP — the
   "VWAP breakout" rule therefore compares spot to itself; the OI-shift rule is still commented out; `_get_level2_depth`
   still reads top-of-book `bidSize`/`askSize`; `default_params` is still `{}` (no configurable thresholds); the
   `last_alert_time` cooldown is still unused; and there is no `tests/test_logic_engine.py`.
   (One real improvement did land: the alert now carries the real option premium via `_get_contract_ltp`.)

---

## 6. Smaller issues worth fixing while you are in there

- **`GET /api/Risk/events` and `GET /api/Alerts/events` return EF entities directly** (`RiskController.cs:84-95`,
  `AlertsController.cs:247-258`) — no DTO, no `AsNoTracking()`, and `limit` is unbounded, so `?limit=1000000` scans and
  serializes the whole table. Add response DTOs, `AsNoTracking()`, and clamp `limit` to 1..500.
- **No server-side validation on limits** (`RiskLimitsStore.UpdateLimitsAsync`): an admin can save
  `maxOrdersPerMinute: 0` or a positive `maxDailyLoss` and silently block or disable every order. Validate
  (orders 1..10000, daily loss < 0, concurrent 1..50, per-user 1..50) and answer 400 with the offending field.
- **Type mismatch:** `AlertEventPayload.SimulationRunId` is `int?` (`AlertSubscriberService.cs:117`) while
  `AlertEvent.SimulationRunId` and the column are `long?`.
- **`RejectOrderAsync` calls `SaveChangesAsync` on the caller's scoped `DbContext`** mid-fill
  (`RiskManagementService.cs:84-95`), which also flushes any other pending tracked changes of that request. Prefer a
  separate scope/`DbContext` for the audit write, or write it after the throw is handled.
- **The global exception handler flattens every non-risk error** to `"An unexpected error occurred."`
  (`Program.cs:170-188`), including in Development — the console's `InlineError` now hides real API messages. Keep the
  409 mapping, but re-throw / show details when `app.Environment.IsDevelopment()`.
- **New npm dependency `lucide-react`** — the repo's convention is inline SVGs in `web/src/components/icons.tsx` (no
  icon library). It works, but either adopt it everywhere or replace the six imported icons with local ones.
- **Dead files:** `web/src/pages/admin/RiskPage.tsx` and `web/src/pages/admin/LiveAlertsPage.tsx` are no longer routed.
  Delete them (or route them behind a flag) so the next reader does not edit the wrong file.
- **`modules.ts` was not updated** — the `risk` and `alerts` modules still carry `status: 'legacy'` and show the `v1`
  badge in the sidebar even though both pages are now v2.

---

## 7. Suggested order

1. Items 1, 3, 4 (three small edits, all in two controllers) — they unblock the module and close two authorization holes.
2. Item 2 (Telegram config wiring).
3. Item 5.2 + 5.3 (audit rows and enforcing the stored limits) — small, and they make the Risk page honest.
4. Item 5.4/5.5/5.6 (alerts supervision, targets, logs), then 5.1 (exposure endpoint).
5. Item 5.8 (the Python rules) last — it is the largest piece and nothing else depends on it.

Keep all three gates green after each step:
```bash
dotnet build src/AlgoTrading.Api -nologo -v q
.venv/bin/python -m unittest discover -s src/AlgoTrading.PythonEngine/tests
cd web && npx tsc -b && npx vite build && npx oxlint src
```
