# Connectors module

Data vendors and brokers, and the routing between them. Console page: **`/admin/broker`**
(the route name is kept because the OAuth callback redirects there).

## What it is for

Until this module the platform had exactly one source of everything: FYERS, wired in at compile time.
Now every vendor is an adapter behind two contracts — `IMarketDataProvider` (where prices come from) and
`IBrokerProvider` (who takes the orders) — and this page is where an operator configures them.

**A broker is not a data vendor.** They are separate registries; a vendor that does both, like FYERS,
registers in both. Someone can take data from one vendor and execute at another.

## How it is laid out

Two screens, because one long page hid the thing an operator actually wants — the list.

**`/admin/broker` — the directory.** Compact cards in three groups:

1. **Active** — usable right now: either it needs no login, or its credentials are saved. Each card
   shows the vendor mark, name, status, what it can deliver in one line, and what it is currently
   serving. Ordered by the platform's own preference (see *FallbackRank* below), so the list reads in
   the same order as the routing chain underneath it.
2. **Available to add** — the adapter ships in this build but still needs credentials.
3. **Planned** — a vendor on the roadmap with no adapter, marked *adapter not installed* so nobody
   hunts for a form that cannot exist. The entries come from
   `Infrastructure/Providers/PlannedConnectors.cs`; an entry leaves that list by being implemented.

Below the groups, the routing table (see below).

**`/admin/broker/{key}` — one connector in full.** Reached by clicking a card, and by the OAuth callback,
which redirects to this page so the operator lands where they pressed Connect.

## What the detail page shows

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

**Routing table** (on the directory) — which connector serves each capability, and whether that decision
is *configured* (a row in `provider_bindings`) or *automatic* (nothing configured, so the platform uses
whichever connector claims the capability). The detail page shows the same thing narrowed to one
connector, with its position in each chain. Order routing never fails over on its own: a broker that
timed out may already have accepted the order.

## The connectors that ship

| Key | Kind | Login | Serves | Rank |
| --- | --- | --- | --- | --- |
| `fyers` | Data + Broker | daily OAuth | history, live ticks, quotes, option chain, depth, greeks | 0 |
| *(your vendors)* | Data vendor | none | history from files on disk | 50 |
| `replay` | Data vendor | none | history from this platform's own `candles` table | 100 |

## Adding a data vendor without writing code

The Connectors page has **Add data vendor**: give it a name, a permanent key and a folder on the API
host, and it becomes a connector like any other — listed, testable, routable, and stamping its key
into the `SourceKey` of every candle it produces.

This is the honest half of "add a vendor from the console". A vendor's **live API** cannot be
configured into existence: every one has its own auth, paging, rate limits and symbol grammar, and a
form that pretended otherwise would produce a connector that fails in ways nobody can diagnose.
Files are genuinely uniform, so files are what this supports, and the page says so.

Rows live in `data_vendors`. The key may not collide with a shipped adapter — two sources sharing a
key would make lineage meaningless — and it cannot be changed afterwards, because candles already
carry it. Removing a vendor also drops its routing bindings; the candles it wrote keep its key.

**`replay`** exists so the platform can run with no vendor at all — a backtest or a coverage check works
when the broker token has expired, which is the failure that costs the most time on a trading morning.
It is also the only real proof the provider seam works, since it is a second implementation with nothing
in common with FYERS.

**File-based vendors** read `timestamp,open,high,low,close,volume` (header required, column order free; timestamps as
ISO-8601 or epoch seconds, naive values read as UTC) from
`<Providers:Csv:Directory>/<symbol>__<resolution>.csv` — e.g. `NSE_NIFTYBANK-INDEX__15.csv`. A missing
file is reported as a rejected symbol, with the **absolute** path it looked at, because the API's working
directory is not the repository root. Unparseable rows are counted and logged, never silently dropped.

### Two rules that keep this safe

**`FallbackRank`** decides the order when nothing is configured in `provider_bindings`: lower wins. Without
it the automatic chain would be alphabetical, and merely installing `csv` would silently take history away
from the live vendor.

**`ServesFromPlatformStore`** marks a connector that reads what the platform already stores. The sync path
reads from such a connector but never writes its bars back — they came out of the very table an upsert
would write them into, so persisting them would re-stamp genuine rows with the wrong source and destroy
the lineage those rows exist to record. `replay` sets it; `csv` does not, because its bars genuinely come
from outside.

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

Dhan, as the first real second vendor. Health monitoring, automatic data failover and shadow-mode
comparison follow, per
[`docs/roadmap/broker-and-data-provider-module.md`](../roadmap/broker-and-data-provider-module.md).
