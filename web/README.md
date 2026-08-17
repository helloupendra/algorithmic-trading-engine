# AlgoTrading Web Client

React + TypeScript client hosting both the **admin panel** and the **trader
panel**, plus the public pages. One app, one build, with routes gated by the
signed-in user's role.

## Running it

The API must be running first — see the [repository README](../README.md).

```bash
cd web
npm install
cp .env.example .env     # Windows: Copy-Item .env.example .env
npm run dev
```

Then open <http://localhost:5173>.

Sign in with the admin account. Its password is printed to the API console the
first time the API starts:

```
==============================================================
  ADMIN ACCOUNT CREATED
    username : admin
    password : ...
==============================================================
```

Pin it afterwards by setting `ADMIN_PASSWORD` in the repo-root `.env`.

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
│   ├── api.ts          Typed fetch client: bearer tokens, 401 refresh, ApiError
│   └── auth.tsx        AuthProvider / useAuth — session state
├── components/
│   ├── AppLayout.tsx   Sidebar shell, role-based navigation
│   └── RouteGuards.tsx RequireAuth, RequireRole, RedirectIfAuthenticated
├── pages/
│   ├── LoginPage.tsx
│   └── Placeholders.tsx
├── App.tsx             Route table
└── styles.css          Design tokens and shell styling
```

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
2. Add the nav entry to `TRADER_NAV` or `ADMIN_NAV` in `AppLayout.tsx`.
3. Fetch with `api.get<T>('/api/...')` from `lib/api.ts`, wrapped in a
   `useQuery`. For live data set a `refetchInterval` (1–2s) — the platform
   polls today; a SignalR/SSE push channel is planned.
