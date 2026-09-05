# Prompt for Google AI Studio — Android companion app

Paste everything below the line into Google AI Studio. It is written to be self-contained: it
describes the platform, the real API surface, the screens, and the rules the app must not break.

**Before you paste, replace `https://YOUR-API-HOST` with wherever your API is reachable from the
phone.** On a local network that is your machine's LAN address (e.g. `http://192.168.1.20:5025`);
over the internet it is your tunnel or server URL. `localhost` will not work from a phone.

**One thing to keep in mind while reviewing what it builds:** this app moves real money. Ask it to
prove every number it shows comes from an API field, and reject anything it invents to fill a
layout — a plausible-looking fake P&L is worse than a blank space.

---

## The prompt

Build a native **Android app in Kotlin with Jetpack Compose** — the mobile companion to an existing
self-hosted algorithmic trading platform for the Indian markets (NSE/BSE). The backend already
exists and is not being changed: the app is purely a client over its REST API.

### What the platform is

A single-operator trading platform. A .NET API owns the data and the control plane; a Python engine
runs strategies and ingests a live market feed; a React console is the desktop UI. The app is for
the moments away from the desk: seeing what is running, what it is making or losing, and stopping it.

Two roles exist and the app must respect them:

* **Admin** — sees and controls everything.
* **Trader** — sees and controls only the runs they started.

### API

Base URL: `https://YOUR-API-HOST`. All JSON. Auth is a bearer JWT.

**Sign in**
```
POST /api/UserAuth/login      { "userNameOrEmail": "...", "password": "..." }
                              → { accessToken, refreshToken, ... }
POST /api/UserAuth/refresh    { "refreshToken": "..." }
POST /api/UserAuth/logout
```
Send `Authorization: Bearer <accessToken>` on everything else. On a 401, refresh once and retry; if
the refresh fails, sign the user out. Store both tokens in **EncryptedSharedPreferences**, never in
plain preferences and never in logs.

**Live runs — the heart of the app**
```
GET   /api/Strategy/runs                  list of runs
GET   /api/Strategy/runs/summary          rollup for the current user
GET   /api/Strategy/runs/{runId}/live     one run: legs, quantities, marks, P&L
GET   /api/Strategy/runs/{runId}/orders   the orders behind it
GET   /api/Strategy/runs/{runId}/signals  what the strategy decided and why
PATCH /api/Strategy/runs/{runId}/risk     change stop-loss / target on a running run
POST  /api/Strategy/runs/{runId}/stop     { "flatten": true }  stop it, square off
```

**Risk**
```
GET  /api/Risk/killswitch/status
POST /api/Risk/killswitch/activate?reason=...     admin only — halts everything, flattens all
POST /api/Risk/killswitch/deactivate?reason=...
GET  /api/Risk/exposure                            what is at risk right now
GET  /api/Risk/events                              audit log
```

**Alerts** — the platform's notification history (run starts and stops, kill switch, strategy signals)
```
GET /api/Alerts/events?limit=100
GET /api/Alerts/status
```

**Market data and health**
```
GET /api/LiveData/latest/all      latest quote per watched symbol
GET /api/LiveData/status/all      ingestor heartbeats
GET /api/MarketSession/check      is the exchange open
GET /api/MarketIntel/movers       day movers
GET /api/Ingestor/status          the live feed process
```

**Backtesting** (read-mostly on mobile)
```
GET /api/Backtest/runs
GET /api/Backtest/runs/{id}
```

Ask the API for its OpenAPI document at `/swagger/v1/swagger.json` and generate the models from it
rather than hand-writing DTOs; treat every field as nullable until the schema says otherwise.

### Screens

1. **Sign in** — username/email and password. Nothing else; there is no self-registration.

2. **Today** (home) — the answer to "is everything fine?" in one screen without scrolling:
   * market session (open/closed, with the next session time),
   * live feed health — healthy / stale, with how long since the last tick,
   * kill-switch state, unmissable when it is active,
   * number of live runs and total unrealised P&L,
   * the three most recent alerts.

3. **Runs** — list of live runs: strategy, underlying, lots, unrealised and realised P&L, age.
   Tapping one opens **Run detail**:
   * every leg with symbol, **lots and lot size shown separately** (quantity is expressed in lots;
     P&L is Δprice × lots × lot size — never show a bare share count),
   * a closed leg shows quantity 0, not a separate opposite-side row,
   * current stop-loss and target, editable while the run is live,
   * signals and orders in tabs,
   * a **Stop run** button behind a confirmation that states whether positions will be squared off.

4. **Alerts** — the event feed, newest first, with severity, source, and whether it reached Telegram.

5. **Risk** — kill-switch card with a deliberately heavy confirmation (type the word, or hold to
   confirm — not a single tap), exposure list, and the recent risk log. Admin only.

6. **Settings** — API host, signed-in user and role, sign out, app version.

### Design

Dark, dense, and calm — an instrument panel, not a consumer fintech app.

* Material 3, dark theme only. Near-black background, one accent colour, generous contrast.
* **Green for gains, red for losses, and nothing else uses those two colours.**
* Tabular figures for every number; right-align money; group in the Indian system (₹1,23,456.78).
* Timestamps in IST, with relative age ("4m ago") next to anything live.
* Big tap targets — this gets used one-handed, on a train, in a hurry.
* Support pull-to-refresh everywhere and poll live screens every 5–10 seconds while foregrounded.
  Stop polling in the background; do not drain the battery watching a closed market.

### Rules the app must not break

1. **Never invent a number.** If a field is null, render "—". No placeholder P&L, no sample rows, no
   "typical" values to make a screen look finished. A wrong number here costs real money.
2. **Never cache money.** Quotes and P&L are shown from the latest response, with the fetch time
   visible. Stale data must look stale.
3. **Every destructive action is confirmed and says exactly what it will do** — how many positions
   will be squared off, whether the kill switch flattens everything.
4. **Respect the role.** A trader must not see admin controls at all; do not render-then-disable.
5. **Show the failure.** When a call fails, show the API's own message. Never a silent empty state
   that looks like "no data".
6. Handle an expired broker token gracefully: the platform's broker session expires daily, so
   history and quote calls can legitimately answer 400 with a reason — surface it as "broker
   reconnect needed", not as a crash.

### Build it in this order

1. Project skeleton, Material 3 dark theme, navigation, Retrofit + kotlinx.serialization, the auth
   interceptor with refresh, encrypted token storage.
2. Sign in → Today.
3. Runs list → Run detail, including stop and risk editing.
4. Alerts.
5. Risk.
6. Settings, then polish: pull-to-refresh, empty and error states, and offline behaviour.

Give me the complete Gradle setup and file tree, then the code file by file. Use a ViewModel per
screen with `StateFlow`, a repository layer over Retrofit, and no business logic in composables.
Where you are unsure about a response shape, say so and generate from the OpenAPI document rather
than guessing.
