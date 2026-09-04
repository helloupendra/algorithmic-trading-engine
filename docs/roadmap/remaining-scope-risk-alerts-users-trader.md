# Remaining scope — Risk v2, Alerts v2, Users v2 + per-trader grants, Trader console

Written 2026-09-04. This is a **self-contained handover**: everything an implementer needs without reading the whole
repository first. It covers the two batches that were specified but **not** implemented. Everything else described in
`docs/modules/strategies_module.md` and `docs/modules/backtesting_module.md` is already built and running.

---

## 0. How to work in this repository

**Stack.** .NET 10 API (`src/AlgoTrading.Api`, `.Application`, `.Infrastructure`, `.Contracts`, `.Domain`),
Python 3.12 engine (`src/AlgoTrading.PythonEngine`, venv at repo-root `.venv`), React 19 + Vite + TanStack Query
(`web/`), PostgreSQL/TimescaleDB + Redis in Docker. The API serves the built SPA from `wwwroot` in production and
applies EF migrations on startup.

**Gates — every change must keep all three green.**
```bash
dotnet build src/AlgoTrading.Api -nologo -v q            # 0 errors
.venv/bin/python -m unittest discover -s src/AlgoTrading.PythonEngine/tests   # currently 146 tests, OK
cd web && npx tsc -b && npx vite build && npx oxlint src # tsc clean, 0 lint errors
```

**Conventions that matter.**
- Auth is **deny-by-default**: `Program.cs` sets a fallback policy requiring an authenticated user, plus one named
  policy `AuthorizationPolicies.AdminOnly` (role `Admin`). A controller with **no** `[Authorize]` attribute is
  therefore "any authenticated user", not public. Only `[AllowAnonymous]` opts out.
- Ownership pattern (copy it): `User.GetRequiredUserId()`, `User.IsInRole("Admin")`, see `StrategyController`
  (self-scoped list + 403 on another user's run).
- Design system: `web/src/styles.css` class vocabulary (`.panel`, `.table`, `.btn`, `.badge`, `.stat`, `.metric`,
  `.seg`, `.tablewrap`). No inline hex colours, no Tailwind, no new npm dependencies.
- TypeScript is strict: `noUnusedLocals`, `noUnusedParameters`, `erasableSyntaxOnly`, `verbatimModuleSyntax`
  (type-only imports must use `import type`).
- Money rules already in force: leg quantities are **lots**; P&L = Δprice × lots × lot size; lot size comes from
  `ILotSizeResolver` (instrument master, then configured fallback).
- **Never** hand-apply an EF migration against the live database; the API applies it on its next start.
- While the operator is live-testing, do not restart the API/ingestor/runners without asking.

**Where things live (already built, reuse them).**
- Live runs are keyed by `runId`; routes `/api/Strategy/runs/{runId}/{stop,live,logs,signals,risk,runner,orders}`.
- `StrategyProcessRegistry`, `StrategyRunControl`, `StrategyRiskGuardService` (per-run risk rules at leg/group/overall
  level incl. trailing stops), `LiveRunHistoryBuilder` (per-user run history), `PositionViewBuilder`.
- `IngestorSupervisor` + `LiveRunStartupReconciler` + `ProcessProbe` + `IProcessSettingsStore` — process pid
  persistence in `system_settings` and adoption after an API restart. **Copy this pattern for the alerter.**
- `system_settings` table: `Id`, `Key` (unique, varchar 200), `Value` (varchar 2000), `UpdatedBy`, `Reason`,
  `CreatedUtc`, `UpdatedUtc`. Used today for `risk.killswitch.active`, `ingestor.pid`, `strategyrun.<id>.pid`.

---

## 1. Current state — what exists today (verified by reading the code on 2026-09-04)

### 1.1 Risk
- `RiskController` is **class-level AdminOnly** and has only: `POST /api/Risk/killswitch/activate`,
  `POST .../deactivate` (reason passed as a **query parameter**, no body), `GET .../status`
  → `{isActive, updatedBy, reason, updatedUtc}`.
- Kill switch is persisted in `system_settings` key `risk.killswitch.active` ("true"/"false"), read through a
  **static 2-second cache** in `RiskManagementService`.
- Limits `MaxOrdersPerMinute` (default 50) and `MaxDailyLoss` (default −50000) exist **only in `appsettings.json`**
  → `RiskManagementSettings`. There is **no endpoint to read or change them**, and the Risk page just prints prose
  about them.
- `EvaluateOrderAsync` is called from exactly one place (`PaperTradingService`, per paper leg fill, skipped when
  `bypassRiskCheck`). It ignores the symbol/side/quantity arguments and re-queries all `PaperPositions` of the run on
  every order.
- `RiskViolationException` has **no HTTP mapping** — if it escapes a controller it becomes a 500.
- **Bug with user impact:** `useKillSwitch` is called by two *trader* pages (`OverviewPage`, `DeployPage`) against the
  AdminOnly status endpoint → guaranteed 403 for every trader.
- The per-run risk rules (`PATCH /api/Strategy/runs/{runId}/risk`, `StrategyRiskGuardService`) are a **separate,
  working** system. Do not change its behaviour; only surface it.

### 1.2 Alerts
- `AlertsController` has **no `[Authorize]` attribute at all** → any authenticated user (including a trader) can
  start/stop the daemon and fire test alerts.
- It spawns **three hardcoded** `execution_runner.py` processes (BANKNIFTY / NIFTY / SENSEX, metrics ports
  8000/8001/8002) but **never passes `--strategy-id`**, which the runner declares required → **every alerter process
  dies immediately on an argparse error**. This is why the alerter has never worked.
- `ResolvePythonExecutable()` exists but is dead code; the launch hardcodes `FileName = "python"`.
- No pid persistence → an API restart orphans the processes and status reports `isRunning: false` while they live on.
- Logs are one 100-line in-memory ring shared by all three processes.
- `strategies/logic_engine.py` (`listed = False`, so it is hidden from the catalog): Rule 1 "bear trap" uses hardcoded
  heavyweights; Rule 1 equity compares spot to `_get_vwap_or_ltp`, which **returns LTP** (so it compares spot to
  itself); Rule 2 (OI shift) is **entirely commented out**; Rule 3 uses top-of-book `bidSize`/`askSize` as if it were
  level-2 depth and a **hardcoded `premium = 150.0`**. `last_alert_time` is seeded in state and never used → no
  cooldown. Alerts go to Telegram (`messaging/telegram_alerter.py`: queue + worker thread, dry-run when
  `TELEGRAM_BOT_TOKEN`/`TELEGRAM_CHAT_ID` are unset) and to Redis channel `alerts:telegram`; **nothing stores them**.
- The page offers `NSE:BANKNIFTY-INDEX` while the engine's map uses `NSE:NIFTYBANK-INDEX` → that target silently falls
  back to mock data.

### 1.3 Users / auth
- `app_user` columns: `Id`, `UserName` (unique), `Email` (unique), `PasswordHash`, `IsActive`, `CreatedUtc`,
  `UpdatedUtc`, `LastLoginUtc?`, `TotalCapital` (numeric, default 0), `Role` (varchar 50, default `Trader`).
  `user_refresh_tokens.UserId` has an FK with **ON DELETE CASCADE**.
- Endpoints: `register` (AdminOnly, **cannot set a role** — always Trader), `login`/`refresh` (anonymous), `logout`,
  `GET /api/UserAuth` (AdminOnly list, no ordering/paging), `GET /{id}`, `DELETE /{username}` (hard delete), `GET /me`.
- **There is no update path at all**: role, `IsActive`, `TotalCapital` and password cannot be changed after creation.
  `TotalCapital` is therefore always 0 unless edited in SQL.
- JWT carries `sub`, `unique_name`, `email`, the long role-claim URI **and** a short `role` claim. Refresh tokens are
  SHA-256 hashed and rotated with a replacement chain. `/me` is the web client's role authority
  (`web/src/lib/auth.tsx` refetches it), so adding a field to `MeResponse` propagates everywhere for free.
- No per-user settings storage of any kind exists.

### 1.4 Trader area
- 12 pages under `web/src/pages/trader/` (Overview, Watchlist, Charts, Market news, Top movers, Option chain,
  Positions, Orders, Strategies, Deploy, RunDetail, RunPicker) plus two shared pages already usable in trader mode
  (`RunHistoryPage` with `mode="trader"`, `LiveRunDetailPage`).
- `TRADER_NAV` in `AppLayout.tsx` is a hardcoded list of 11 routes with no metadata; `ROUTE_TITLES` covers only 3
  trader routes; `/trader/runs/:id` is reachable only by link.
- `web/src/lib/modules.ts` `MODULES`: 8 entries (`data`, `strategies`, `backtesting`, `risk`, `alerts`, `users`,
  `broker`, `system`), **every one `adminOnly: true`**, every route `/admin/*`; `adminOnly` is never enforced (the
  registry renders only inside `AdminNav`). `ModuleDef.key` is documented as the id future per-trader grants reference.
- `RouteGuards.tsx` has `RequireAuth`, `RequireRole` (exact role equality), `RedirectIfAuthenticated`. There is no
  `RequireModule`.
- **Security hole (highest priority in this document):** `SimulatorController` has **no ownership scoping**.
  `GET /api/Simulator/runs?userId=` passes the query straight through, and every run-scoped GET (`runs/{id}`,
  `/positions`, `/orders`, `/signals`, `/portfolio`, `/equity-curve`, `/performance`) serves **any** user's data to
  **any** authenticated caller. `StrategyController` already does this correctly — copy that pattern.
- `DeployPage.tsx` sends `userId: user!.id` from the client when creating a run; the API must derive it from the token.
- `OptionsHistoryController` and `BackfillController` have their `AdminOnly` attributes **commented out**.

---

## 2. Batch B — Risk module v2 + Alerts module v2

### 2.0 Schema change (one additive EF migration)
Add two tables; change nothing existing. Create with
`dotnet ef migrations add RiskAndAlertEvents --project src/AlgoTrading.Infrastructure --startup-project src/AlgoTrading.Api`
and verify with `dotnet ef migrations script --idempotent` that it only CREATEs them. **Do not apply it** — the running
API applies it on its next restart.
- `risk_events`: `Id` (bigint identity), `OccurredUtc`, `Kind` (varchar 40: `KillSwitchActivated` |
  `KillSwitchDeactivated` | `LimitsChanged` | `OrderRejected`), `ActorUserId` (bigint?), `ActorName` (varchar 100),
  `Reason` (varchar 500), `DetailsJson` (text), `SimulationRunId` (bigint?), `Symbol` (varchar 100?).
  Indexes: `OccurredUtc` desc, `Kind`.
- `alert_events`: `Id`, `OccurredUtc`, `Source` (varchar 60: `logic-engine` | `e2e-test` | `system`),
  `Underlying` (varchar 40), `Symbol` (varchar 100?), `Severity` (varchar 20: `info` | `warning` | `critical`),
  `Title` (varchar 200), `Message` (varchar 1000), `MetadataJson` (text), `DeliveredToTelegram` (bool),
  `SimulationRunId` (bigint?). Indexes: `OccurredUtc` desc, `Underlying`.

### 2.1 Risk — backend
- **Limits move to the database.** `system_settings` keys `risk.limits.maxOrdersPerMinute`,
  `risk.limits.maxDailyLoss`, `risk.limits.maxConcurrentRuns` (and, for batch C, `risk.limits.maxRunsPerUser`),
  seeded from `RiskManagementSettings` on first read. New `IRiskLimitsStore` / `RiskLimitsStore` (Infrastructure) with
  a 2-second cache and immediate publish on write, mirroring the kill-switch pattern.
  `RiskManagementService.EvaluateOrderAsync` reads the store instead of `IOptions` (behaviour otherwise unchanged) and
  writes a `risk_events` row (`Kind=OrderRejected`, run id, symbol, reason) whenever it throws.
- `MaxConcurrentRuns` is enforced where `StrategyController` already checks
  `StrategyRunnerOptions.MaxConcurrentProcesses`; the effective cap is the lower of the two.
- Endpoints (AdminOnly unless stated):
  - `GET /api/Risk/status` — **any authenticated user** → `{isActive, updatedBy, reason, updatedUtc}`. The web client's
    `useKillSwitch` switches to this so the two trader pages stop 403ing.
  - `POST /api/Risk/killswitch/activate|deactivate` — keep the routes; accept a JSON body `{reason}` **and** keep the
    legacy `?reason=` query; write a `risk_events` row with the actor.
  - `GET|PUT /api/Risk/limits` → `{maxOrdersPerMinute, maxDailyLoss, maxConcurrentRuns, maxRunsPerUser, source:
    "database"|"config", updatedBy, updatedUtc}`. PUT validates (orders 1..10000, daily loss < 0, concurrent 1..50,
    per-user 1..50) and writes a `LimitsChanged` risk event with before/after in `DetailsJson`.
  - `GET /api/Risk/events?take=100&kind=&fromDate=&toDate=` — newest-first audit rows.
  - `GET /api/Risk/exposure` → `{killSwitch, limits, guardIntervalSeconds, activeRuns: [{runId, userId, userName,
    strategyName, underlying, lots, risk, realizedPnl, unrealizedPnl, totalPnl, openPositions}], totals: {runs,
    openPositions, totalPnl, capitalUsed}}`. Reuse `LiveRunHistoryBuilder` and the registry; do not write new P&L math.
- Map `RiskViolationException` to **409** with `{message}` via an exception filter/middleware registered in `Program.cs`.

### 2.2 Risk — web (`web/src/pages/admin/RiskPage.tsx`, module status → `ready`)
Four sections: (1) **Kill switch** — state card, typed `HALT` confirmation to activate (keep the existing UX), reason
input, actor/time; (2) **Limits** — editable form with a `database`/`config` source badge; (3) **Live exposure** —
tiles (running runs, open positions, total P&L, capital used) and a table of active runs with their risk rules linking
to `/admin/strategies/runs/{runId}`; (4) **Risk events** — filterable audit table. Poll status/exposure every 5 s,
events every 30 s.

### 2.3 Alerts — backend
- `[Authorize(Policy = AdminOnly)]` on the whole controller **except** `GET /api/Alerts/events` (any authenticated
  user; batch C narrows this to admin-or-`alerts`-grant).
- **Fix the launch** (this is why alerts never worked): pass `--strategy-id` (use
  `StrategyCatalogService.StableId("LogicEngine")`) and `--user-id` (the caller), resolve the interpreter and engine
  directory through `PythonEngineLocator` (delete the dead resolver), and use the runner's auto metrics port
  (`--metrics-port 0`). Targets come from the request body `{targets: [{underlying, spotSymbol}]}`, defaulting to a
  `system_settings` key `alerts.targets` seeded with BANKNIFTY / NIFTY / SENSEX and their **correct** spot symbols
  (`NSE:NIFTYBANK-INDEX`, `NSE:NIFTY50-INDEX`, `BSE:SENSEX-INDEX`). A child that fails to spawn must produce a 207/400
  naming it, never a blind 200.
- **Pid persistence + adoption**: store `alerts.pid.<underlying>` through `IProcessSettingsStore` and adopt live
  processes after an API restart exactly like `IngestorSupervisor` (liveness + command-line check via `ProcessProbe`).
  `GET /api/Alerts/status` → `{isRunning, managed, processes: [{underlying, processId, source, startedUtc}]}`.
  Stop must kill adopted processes too.
- **Logs**: per-underlying ring buffers (300 lines) plus retention for the last stopped set;
  `GET /api/Alerts/logs?underlying=`.
- **Alert history**: `POST /api/Alerts/events` (any authenticated user — the engine's service account posts here) with
  `{occurredUtc, source, underlying, symbol?, severity, title, message, metadataJson?, deliveredToTelegram,
  simulationRunId?}` → inserts an `alert_events` row; `GET /api/Alerts/events?take=100&underlying=&severity=&fromDate=&toDate=`
  returns them newest-first. `POST /api/Alerts/test-e2e` stays AdminOnly and also writes an event row.

### 2.4 Alerts — Python (`strategies/logic_engine.py`, `messaging/telegram_alerter.py`)
- One `emit_alert(...)` helper: sends to Telegram as today **and** posts `/api/Alerts/events` (failures logged, never
  fatal).
- **Cooldown/dedupe**: `alert_cooldown_seconds` param (default 300) keyed by (rule, underlying, symbol), using the
  already-seeded `last_alert_time` state; suppressed alerts are counted and logged, not sent.
- **Real VWAP**: replace `_get_vwap_or_ltp` with a session VWAP computed from the day's stored 1m bars
  (`GET /api/LiveData/bars?symbol=&resolution=1m&take=500`, session bars only, Σ(typical price × volume) / Σ volume);
  fall back to LTP only when volume is zero and say which was used in the alert text.
- **Rule 2 (OI shift)**: implement from the live quotes' `openInterest` across the ATM±N chain
  (`GET /api/LiveData/latest/all` filtered to the underlying's contracts); track the highest-OI call and put strike in
  state (the fields already exist) and alert when either shifts by ≥ `oi_shift_steps` strikes (default 1).
- **Rule 3**: rename honestly to "order-book imbalance", use `bidSize`/`askSize` (documented as top-of-book, not
  level-2) with an `imbalance_ratio` param (default 3.0), and use the **real** option premium from the latest quote.
- Every threshold becomes a documented `default_params` entry: `heavyweights`, `bear_trap_support_offset`,
  `bear_trap_resistance_offset`, `imbalance_ratio`, `oi_shift_steps`, `alert_cooldown_seconds`, `strike_window`.
- Tests (`tests/test_logic_engine.py`): VWAP maths, cooldown suppression, OI-shift detection, the imbalance rule, and
  that `emit_alert` posts to the API and never raises when the API is down.

### 2.5 Alerts — web (`web/src/pages/admin/LiveAlertsPage.tsx`, module status → `ready`)
(1) **Daemon** — per-underlying process cards (running/adopted/stopped, pid, uptime) with Start/Stop and a target
editor saved to `alerts.targets`; (2) **Alert feed** — the `alert_events` table with severity badges, filters
(underlying, severity, date), 10 s auto-refresh and a "Telegram delivered" column; (3) **Console** — per-underlying log
tabs; (4) **Test alert** — the E2E trigger with targets read from the same config (fixes today's symbol mismatch).

### 2.6 Batch B acceptance
- A trader's Overview/Deploy no longer 403s on kill-switch status.
- Limits are readable and editable from the Risk page and actually change `EvaluateOrderAsync`; every change and every
  rejection lands in `risk_events`.
- Starting the alerter spawns processes that **stay alive**, survive an API restart (adoption), and their alerts appear
  in the feed with the real premium and a working cooldown.

---

## 3. Batch C — Users v2, per-trader module grants, trader console (+ the Simulator ownership fix)

### 3.0 Schema change (one additive EF migration)
`user_module_grants`: `Id` (bigint identity), `UserId` (bigint, FK → `app_user`, **ON DELETE CASCADE**),
`ModuleKey` (varchar 60), `GrantedUtc`, `GrantedBy` (varchar 100). Unique index (`UserId`, `ModuleKey`).
`dotnet ef migrations add UserModuleGrants …`; verify with the idempotent script; do not apply.

### 3.1 Module catalog + grants (server-side truth)
- `ModuleCatalog` (Api/Services, static): `{key, name, description, grantable, adminOnly}` for
  `data`, `strategies`, `backtesting`, `alerts`, `risk` (grantable) and `users`, `broker`, `system` (admin-only, not
  grantable). `GET /api/Modules` (any authenticated) → catalog + `granted: string[]` for the caller (admins: all keys).
- `IUserModuleGrantService` / `UserModuleGrantService` (Infrastructure): `GetGrantsAsync(userId)`,
  `GetGrantsForUsersAsync(ids)`, `SetGrantsAsync(userId, keys, grantedBy)` (validated against grantable keys, replaces
  the set in one transaction). Admins implicitly have every module (never stored). Cache per user for 30 s with
  invalidation on write; document that a revoke can lag by that much.
- `MeResponse` gains `modules: string[]` (effective) — the web client's single source of truth.
- **Enforcement, not just navigation**: a `ModuleAccess` policy + `ModuleRequirement(moduleKey)` handler reading the
  grant service, used as `[RequireModule("strategies")]`:
  - `POST /api/Strategy/{id}/start` and `/deploy` → admin **or** `strategies` grant (today start is AdminOnly and
    deploy is any authenticated user). The run is owned by the caller (`GetRequiredUserId`); a non-admin may not exceed
    `risk.limits.maxRunsPerUser` (default 3) → 429 with a clear message.
  - `POST /api/Backtest/runs`, `/backfill`, `/runs/{id}/stop`, `DELETE /runs/{id}` → admin or `backtesting` grant, and
    backtest **reads** become ownership-scoped like live runs (a trader sees only their own).
  - `GET /api/Alerts/events` → admin or `alerts` grant. `GET /api/Risk/status` stays any-authenticated.
  - Everything already AdminOnly stays AdminOnly.

### 3.2 Simulator ownership fix (do this first — it is a live data leak)
In `SimulatorController`: derive the user from the token; `GET runs` self-scopes for non-admins (ignore or reject a
foreign `userId`); every run-scoped GET returns **403** when the run's `UserId` differs and the caller is not an admin;
`POST /api/Simulator/runs` takes `UserId` from the token and ignores the body's value.
**Keep engine-facing writes open to any authenticated caller** (`signals`, `marks`, `progress`, `complete`,
`equity-snapshots`, and `/api/Strategy/runs/{id}/{signals,runner}`) — the Python engine posts them with a service
account that does not own the run; document this in a comment. The existing run-status gates still apply.

### 3.3 Users v2 — backend (`UserAuthController` + `AuthService`), AdminOnly unless noted
- `POST /register` — accept optional `role` (validated by `UserRoles.Normalize`, default Trader), `totalCapital`,
  `modules: string[]`.
- `GET /api/UserAuth?search=&role=&isActive=&take=&skip=` — ordered by user name, filterable; returns
  `MeResponse` + `{modules: string[], liveRuns: int, backtests: int, netPnl: decimal}` with the counts computed in
  **one** grouped query, not per row.
- `PATCH /api/UserAuth/{id}` — `{email?, role?, isActive?, totalCapital?}`; refuses to demote/deactivate the **last
  active admin** and refuses self-demotion (400 with a clear message).
- `POST /api/UserAuth/{id}/password` — `{newPassword}` (min 8); rehashes and revokes that user's refresh tokens.
- `POST /api/UserAuth/{id}/revoke-sessions`.
- `GET|PUT /api/UserAuth/{id}/modules` — `{modules: string[]}`, validated against the catalog.
- `DELETE /api/UserAuth/{id}` (keep the legacy `DELETE /{username}` working); refuses the last admin and self-delete.
- `GET /me` unchanged except the new `modules` field. Keep login's 400-on-bad-credentials contract.

### 3.4 Users v2 — web (`web/src/pages/admin/UsersPage.tsx`, module status → `ready`)
Table: user, email, role badge, status, capital, live runs / backtests / net P&L, last login, granted-module chips;
filters (search, role, status). Row actions: **Edit** (email, role, active, capital), **Modules** (checkbox list from
`GET /api/Modules`; admin rows show "all modules (admin)" and are disabled), **Reset password** (shown once),
**Revoke sessions**, **Delete** (typed username confirmation). Create form: username, email, password, role, capital,
initial modules. Mirror the server guardrails client-side (cannot delete/demote yourself or the last admin).

### 3.5 Trader console — web
- Extend `ModuleDef` in `lib/modules.ts` with `traderRoute?`, `traderSections?`, `requires: 'admin' | 'grant'`; keep
  the existing admin entries working.
- Build the trader nav from the user's `modules`:
  - always: **Overview** (`/trader`), **My runs** (`/trader/strategies/history`)
  - `data` → Watchlist, Charts, Option chain, Market news, Top movers
  - `strategies` → Live runner (`/trader/strategies/live`), Strategies catalog, Positions, Orders, Deploy
  - `backtesting` → New backtest (`/trader/backtesting/new`), Backtest runs (`/trader/backtesting/runs`)
  - `alerts` → Alerts feed (`/trader/alerts`, read-only)
  - `risk` → Risk status (`/trader/risk`, read-only)
- Add a `RequireModule` guard mirroring `RequireRole` (renders `/forbidden` when the module is not granted) and
  complete `ROUTE_TITLES` for every trader route.
- **Reuse, do not fork**: `LiveRunnerPage`, `RunHistoryPage`, `LiveRunDetailPage`, the four backtesting pages,
  `LiveAlertsPage` (feed only) and `RiskPage` (status only) take a `mode: 'admin' | 'trader'` prop and hide admin-only
  controls in trader mode (backfill, ingestor control, limits editing, alerter start/stop, other users' rows).
- Rebuild the trader **Overview** on the v2 vocabulary: granted-module cards, own live runs with P&L, own recent
  backtests, market/feed status pills, kill-switch banner from `GET /api/Risk/status`.
- `DeployPage`: stop sending `userId`.
- A trader with no grants sees Overview + My runs and a card explaining that an admin must grant modules.

### 3.6 Batch C acceptance
- An admin can create a trader, grant `strategies` + `backtesting`, and that trader sees exactly those sections, can
  start a live run and a backtest, and sees only their own runs everywhere.
- Revoking a grant hides the section and makes the API answer 403 within the cache window.
- `GET /api/Simulator/runs/{id}` (and positions/orders/portfolio/…) returns 403 for another user's run, while the
  Python runner keeps posting signals/marks/progress successfully.
- The last active admin cannot be demoted, deactivated or deleted.

---

## 4. Suggested order of work
1. **Simulator ownership fix** (§3.2) — a live data leak, one controller, no schema change.
2. **Alerts launch fix** (§2.3, the `--strategy-id` line) — one line that makes a whole module work.
3. Batch B backend → Batch B web → Batch B Python rules.
4. Batch C backend (grants + users) → Users page → trader console.

Each step must leave all three gates green before the next one starts. After a batch that adds a migration, the
operator restarts the API (running strategy runners and the ingestor are adopted automatically, so nothing needs to be
stopped first).
