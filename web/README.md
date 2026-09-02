# AlgoTrading Web Client

React 19 + TypeScript console hosting the **admin modules** and the **trader
screens**. One app, one build, routes gated by the signed-in user's role.

The app runs on the **v2 design system**: a single token vocabulary in
`styles.css` (dark, dense, operator-grade), an SVG icon set, a grouped sidebar
built from the module registry, and a topbar that keeps the three live health
signals (market session, broker session, feed heartbeat) on every screen.
Modules are rebuilt one at a time on this system — **Data is complete**; the
screens still tagged `v1` in the sidebar run on the old markup but inherit the
new look through the shared class vocabulary.

## Running it

The API must be running first — see the [repository README](../README.md).

```bash
cd web
npm install
cp .env.example .env     # Windows: Copy-Item .env.example .env
npm run dev
```

Then open <http://localhost:5173>. The root `/` goes straight to sign-in
(there is no public landing page); `/trader` and `/admin` require a session.

### Signing in as admin

Set the password you want in the repo-root `.env`, then restart the API:

```ini
ADMIN_USERNAME=admin
ADMIN_PASSWORD=YourStrongPassword
```

```bash
python3 scripts/_gen_local_settings.py
dotnet run --project src/AlgoTrading.Api
```

`AdminBootstrapper` brings the stored password in line with that value on every
start, so this is also how you recover from a lost password — no database
surgery needed.

Leave `ADMIN_PASSWORD` empty instead and the API generates a strong one and
prints it to its console **once**:

```
==============================================================
  ADMIN ACCOUNT CREATED
    username : admin
    password : ...
==============================================================
```

An existing account is never modified while `ADMIN_PASSWORD` is empty.

| Script | Does |
|---|---|
| `npm run dev` | Vite dev server on :5173 with HMR |
| `npm run build` | Type-check and produce `dist/` |
| `npm run preview` | Serve the production build locally |

## Configuration

`web/.env` holds one value:

```ini
VITE_API_BASE_URL=http://localhost:5025
```

The API only accepts browser requests from origins listed in its
`Cors:AllowedOrigins`, which comes from `CORS_ALLOWED_ORIGINS` in the repo-root
`.env`. If you change the dev port or deploy, add the new origin there and
re-run `python3 scripts/_gen_local_settings.py`.

## Structure

```
src/
├── lib/
│   ├── api.ts             Typed fetch client: bearer tokens, 401 refresh, ApiError
│   ├── auth.tsx           AuthProvider / useAuth — session state
│   ├── queries.ts         One TanStack Query hook per endpoint; polling intervals live here
│   ├── types.ts           DTO shapes mirroring src/AlgoTrading.Contracts
│   ├── modules.ts         Module registry — sidebar, admin home grid, future per-trader grants
│   ├── symbols.ts         Symbol → category classification, resolution helpers
│   └── format.ts          INR/number/date/age formatting
├── components/
│   ├── AppLayout.tsx      v2 shell: grouped sidebar + topbar health pills
│   ├── icons.tsx          Inline SVG icon set (no icon library)
│   ├── ui.tsx             Panel, StatTile, Badge, QueryBoundary primitives
│   ├── CandleChart.tsx    Persistent lightweight-charts candlestick + volume
│   ├── LiveQuotesMonitor.tsx  Flashing quote table (used by the v1 System page)
│   ├── charts.tsx         v1 chart wrappers (legacy pages)
│   └── RouteGuards.tsx    RequireAuth, RequireRole, RedirectIfAuthenticated
├── pages/
│   ├── LoginPage.tsx
│   ├── data/              THE DATA MODULE (v2)
│   │   ├── DataOverviewPage.tsx     Coverage matrix, pipeline health, needs-attention
│   │   ├── LiveFeedsPage.tsx        Feed start/stop, index tickers, merged live watchlist,
│   │   │                            diagnostics (+ /api/Ingestor/logs), tick/bar inspector
│   │   ├── HistoricalDataPage.tsx   Coverage-first browser, chart, FYERS backfill,
│   │   │                            ATM±N option-chain backfill
│   │   └── InstrumentsFnoPage.tsx   Master search, expiries, CE/PE chain ladder
│   ├── admin/             AdminHomePage (module grid) + v1 modules awaiting rebuild
│   └── trader/            Trader screens (v1, rebuild queued)
├── App.tsx                Route table (old /admin/ingestion and /admin/instruments
│                          redirect into the Data module)
└── styles.css             v2 design tokens + the shared class vocabulary
```

Design conventions worth knowing:

- **Coverage before pickers.** Any screen that asks for a symbol/date shows
  what data actually exists first (`/api/MarketData/coverage`, expiry lists).
- `QueryBoundary` keeps showing the last good data when a background poll
  fails, with a small stale hint — a dropped poll must never blank a live
  table.
- `CandleChart` keeps one chart instance alive and refreshes via `setData`,
  so polling never resets the user's zoom.
- New endpoints can be missing from an older running API build (e.g.
  `/api/Ingestor/logs`); the SPA fallback answers such requests with HTML, so
  guard with `Array.isArray` before mapping.

## How authorization works here

Route guards and conditional navigation decide **what is rendered**. They are a
usability layer, not a security boundary — the API independently enforces the
same rules against the token's role claim on every request.

Concretely: a trader who types `/admin/risk` is redirected to `/forbidden`, and
if they defeated that redirect the underlying endpoints still answer `403`.
Never move an authorization decision into this app.

## Tokens

Access and refresh tokens are kept in `localStorage`, so a reload keeps you
signed in. `api.ts` attaches the access token to every request; on a `401` it
refreshes once and replays the request, with concurrent refreshes de-duplicated
so a polling dashboard cannot invalidate its own rotated token.

That storage choice trades some XSS exposure for usability, which is acceptable
while the app is first-party and served from its own origin. Moving to httpOnly
cookies would require matching cookie authentication on the API.

## Adding a screen

1. Add the route in `App.tsx`, inside `RequireAuth` and — for admin screens —
   inside `RequireRole role="Admin"`.
2. Add the nav entry: for a new **module**, register it in
   `lib/modules.ts` (the sidebar and admin home grid read from there); for a
   section inside the Data module, extend `DATA_SECTIONS`; for a trader
   screen, extend `TRADER_NAV` in `AppLayout.tsx`.
3. Add a hook in `lib/queries.ts` (one hook per endpoint; put the
   `refetchInterval` there, not in the component) and render through the
   primitives in `components/ui.tsx`. The platform polls today; a
   SignalR/SSE push channel is planned.
4. Build UI from the v2 vocabulary in `styles.css`
   (`.panel/.stat/.table/.badge/.btn/.field/.seg/.pill/.console` …) and icons
   from `components/icons.tsx` — no new CSS dialects, no emoji icons.
