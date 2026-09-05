# Connectors module

Data vendors and brokers, and the routing between them. Console page: **`/admin/broker`**
(the route name is kept because the OAuth callback redirects there).

## What it is for

Until this module the platform had exactly one source of everything: FYERS, wired in at compile time.
Now every vendor is an adapter behind two contracts — `IMarketDataProvider` (where prices come from) and
`IBrokerProvider` (who takes the orders) — and this page is where an operator configures them.

**A broker is not a data vendor.** They are separate registries; a vendor that does both, like FYERS,
registers in both. Someone can take data from one vendor and execute at another.

## What the page shows

**One card per connector:**

* **Capability matrix** — history, live ticks, quotes, option chain, bid/ask depth, open interest, greeks,
  orders. Declared by the adapter itself, so a strategy needing open interest is told before it runs
  instead of discovering nulls at runtime. FYERS declares `openInterest: false` today because the feed
  genuinely does not deliver it in the current subscription mode.
* **App credentials** — client id, secret, redirect URL, per connector. The secret is encrypted with
  ASP.NET Data Protection and never returned by the API. Saved credentials beat the `appsettings`/`.env`
  fallback, and the card says which of the two is in force.
* **Session** — connected or not, when the token was saved, and whether it is stale. A connector whose
  auth kind is `OAuthDaily` is marked "reconnect" once the IST trading day it was issued on has passed.
* **Test connection** — fetches a real slice of history (BANKNIFTY 15m, last 7 days) and reports what came
  back, with the vendor's own words on failure. This is the difference between "credentials saved" and
  "it works".

**Routing table** — which connector serves each capability, and whether that decision is *configured* (a
row in `provider_bindings`) or *automatic* (nothing configured, so the platform uses whichever connector
claims the capability). Order routing never fails over on its own: a broker that timed out may already
have accepted the order.

## What it cannot do, and why

A connector is **code, not a configuration row**. A new vendor needs an adapter that speaks its API,
declares its capabilities, and maps its symbols to the platform's canonical symbol. What this module makes
self-service is everything after that adapter exists: credentials, connecting, testing, routing.

## API

All admin-only, under `api/Providers`:

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/Providers` | every connector: capabilities, credentials, session, what it is serving |
| PUT | `/api/Providers/{key}/credentials` | save app credentials |
| GET | `/api/Providers/{key}/auth-url` | vendor hosted-login URL |
| POST | `/api/Providers/{key}/disconnect` | drop that connector's session only |
| POST | `/api/Providers/{key}/test` | live probe |
| GET | `/api/Providers/bindings` | effective routing, with `isFallback` |
| PUT | `/api/Providers/bindings` | pin a capability's chain; empty list restores the fallback |

Validation refuses an unknown capability, an unregistered connector, and a connector that does not
provide the capability being bound (`"FYERS does not provide Orders."`).

## Data model

* `broker_accounts` — `UserId` nullable: null is the shared platform account (today's behaviour), a row
  with a user id is that trader's own account.
* `broker_sessions.ProviderKey` — sessions are per connector, so connecting a second broker cannot
  invalidate the first one's token.
* `broker_configs.BrokerAccountId` — credentials per account, for when traders bring their own vendor app.
* `provider_bindings` — capability → connector chain, by priority.
* `instrument_vendor_symbols` — what each vendor calls an instrument the platform knows by its canonical
  symbol. A connector that speaks canonical symbols (FYERS, whose grammar the canonical form came from)
  needs no rows.
* `SourceKey` on `candles`, `live_ticks`, `live_quotes_latest`, `live_bars`, `market_ticks` — which
  connector produced each row.

## Daily routine

Broker tokens expire daily. Each trading morning: open Connectors → the FYERS card reads
*"Token is from a previous day"* → **Reconnect** → **Test connection** to confirm real bars come back →
start the ingestor.

## Next

`replay` (this platform's own stored candles) and `csv` adapters, then Dhan. Health monitoring, automatic
data failover and shadow-mode comparison follow, per
[`docs/roadmap/broker-and-data-provider-module.md`](../roadmap/broker-and-data-provider-module.md).
