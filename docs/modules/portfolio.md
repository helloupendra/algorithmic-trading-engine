# Portfolio

A signed-in user links their own FYERS, Dhan or Angel One account, **read only**, and OpenFNO shows them a factual
report on what it holds. Console: **/portfolio**. API: `/api/Portfolio`.

## Facts only, never advice

The page describes a user's holdings. It never suggests what to do with them: no buy, sell, hold, exit, target or
stop-loss, in any wording. Advice on securities is regulated work in India: SEBI reserves it for registered
investment advisers and research analysts, and OpenFNO is neither.

The rule holds in three places:

* **The figures.** Everything is computed by code (`PortfolioReportBuilder`, `PortfolioRiskMath`) and describes the
  past or the present. Nothing grades, ranks, forecasts or suggests.
* **The written summary.** A model writes one paragraph from the report, and code decides whether anyone sees it
  (`PortfolioSummaryWriter`). Every number, date and time in it must be one the report holds (`AiFactCheck`, the same
  check the desk analysts pass), and advice wording is refused (`PortfolioAdviceGuard`): should, consider, recommend,
  buy, sell, hold, exit, book profit, stop-loss, target, rebalance, accumulate, avoid, trim and their phrasings, and
  in Hindi and Hinglish kharid, bech, nikal, rakho, chahiye, book karo, in Latin or Devanagari script. A failed
  paragraph is sent back once with what failed; a second failure leaves the report without a summary and a data note
  saying so. The page works the same either way.
* **The disclaimer** on every report: "Information about your own holdings, not investment advice."

## Linking each broker

No password, PIN or TOTP secret is ever asked for, stored or logged. OpenFNO keeps only the token the broker issues to
its app, or a token the user generated and pasted.

| Broker | How the user links | What is kept | Lasts |
|---|---|---|---|
| FYERS | Signs in at FYERS (OAuth authorization code) | The access token | The day: FYERS tokens end the next morning. Refreshing one needs the user's PIN, so the user connects again instead |
| Angel One | Signs in at Angel One (SmartAPI publisher login) | The session JWT (`auth_token`); the feed and refresh tokens are not kept | Until the JWT's own end |
| Dhan | Pastes their client id and an access token from web.dhan.co | The access token | 24 hours |

**FYERS** ([third-party app authentication](https://fyers.in/api-docs/v3/api/authentication/third-party-app/)).
Connect sends the browser to `api-t1.fyers.in/api/v3/generate-authcode` with the app id, the app's registered redirect
URL and a one-time state. FYERS sends the browser back with `auth_code` and the same state; the code is exchanged at
`/api/v3/validate-authcode` with the SHA-256 of `app_id:app_secret`, and one read of `/api/v3/profile` names the
account. Reads: `/api/v3/holdings`, `/api/v3/positions`, `/api/v3/funds`, with the header `app_id:access_token`.
FYERS lets only the creator sign in to an *individual* app, so other users need a *third-party* app (owner action 1).
Until there is one, the page uses the platform's own app from the Connectors page, whose registered redirect is
`/api/Auth/callback`: that callback passes a portfolio state (`pf-…`) on to `/api/Portfolio/callback/fyers` and never
touches the desk's own FYERS session.

**Angel One** (SmartAPI publisher login: `smartapi.angelone.in/publisher-login?api_key=…&state=…`). After the user
signs in on Angel's page, Angel redirects to the redirect URL registered on the app under My Apps with `auth_token`
(and `feed_token`). One read of `getProfile` names the account. Reads: `portfolio/v1/getAllHolding`,
`order/v1/getPosition` (a read, despite its path) and `user/v1/getRMS`, with `Authorization: Bearer` and the app key
as `X-PrivateKey`. SmartAPI's own documentation pages could not be read when this was built (they render in the
browser); the flow rests on Angel's SDK (`login_url()` in smartapi-python) and on SmartAPI staff answers on its
forum, which say a custom redirect URL works for older API keys and is still being added for new ones, and that the
state may not come back. So for Angel One a missing state is accepted on the browser's cookie alone (below), never a
different state. Verify with one real sign-in before announcing it (owner action 2).

**Dhan** ([authentication](https://dhanhq.co/docs/v2/authentication/)). Dhan's API key and secret are "user
specific": the platform's own key cannot sign anyone else in, and the flow Dhan offers platforms is its partner
login, which needs Dhan to onboard OpenFNO as a partner first (owner action 3). What every individual can do is
generate an access token under web.dhan.co → My Profile → Access DhanHQ APIs, valid 24 hours. The user pastes it with
their client id; one read of `/v2/profile` checks it before anything is saved, and the account it opens must be the
client id typed, or nothing is saved. Reads: `/v2/holdings`, `/v2/positions`, `/v2/fundlimit` (Dhan spells its field
`availabelBalance`). Dhan sends no price with holdings: those are valued at the desk's stored close (below).

**The redirect is bound twice.** The callback (`GET /api/Portfolio/callback/{broker}`) is anonymous by necessity, so
the state is all that says whose account it is. Connect issues a random state for the signed-in user and one broker,
good once and for ten minutes, and sets the same value as an HttpOnly, SameSite=Lax cookie scoped to
`/api/Portfolio/callback`. The callback needs both, equal. Without the cookie, someone could start a link on their own
account and send another person the broker's sign-in page, and that person's account would be linked to the sender.
A refused callback links nothing, and a restart forgets pending links.

**Read only.** The clients in `src/AlgoTrading.Infrastructure/Portfolio/` can call only the paths listed above.
`PortfolioReadOnlyTests` reads their source and their declared paths and fails on any order, modify, cancel, convert
or exit call, and on any method named for one.

## What is stored, and for how long

* **`portfolio_connections`**: one row per user per broker. The token is encrypted with ASP.NET Data Protection under
  its own purpose (`PortfolioConnection.Token.v1`), never returned by any endpoint and never logged; the HTTP clients
  are registered with no request logging. The account is shown by its last four characters (`••••4821`). Also the
  status (connected, expired, error), when it was connected, when the token ends, the last read and its error.
* **`portfolio_snapshots`**: each successful read: holdings, positions and funds in one shape for every broker, as
  JSON, with the price each holding was valued at and where that price came from. Kept **90 days**
  (`Portfolio:RetentionDays`): each sync deletes the user's older rows, and `PortfolioRetentionService` everyone's
  every six hours. They are not archived anywhere.
* **Disconnect** deletes the row and the token. `?forget=true` also deletes that broker's snapshots, and any cached
  summary that may name its holdings.
* The written summary is cached on the newest snapshot it was written from.

## The report

All amounts in rupees. Every weight and every "Pct" is a percentage (0 to 100) of current value; `hhi` is the sum of
the squared percentage weights (0 to 10,000). A null is a figure that is not known, never a zero.

* **Holdings, merged by ISIN.** A security held at two brokers is one holding: the quantities and the amounts paid add
  up, the average price is the total paid over the total quantity, and the price is the freshest broker price. A
  holding with no price is listed but left out of the value, the P&L and the weights, never valued at its cost.
* **Prices.** The broker's own last price; for Dhan, which sends none, the desk's stored daily close of the session
  (`PortfolioPrices`). The day's change is the price against the previous session's close from the desk's stored
  daily closes, by the NSE calendar: on a Saturday, or before 09:15, it is the last session's change.
* **Sector and market-cap band** are `Unknown` for every holding, and a data note says so: the desk holds no sector or
  market-cap classification of listed stocks yet, and the page does not guess one. The allocation is still shown by
  broker.
* **Concentration**: the largest holding's weight, the top five's, the HHI, and the five largest.
* **Risk**: today's holdings at today's weights over past daily closes, against NIFTY 50: annual volatility (sample
  standard deviation × √252), beta (cov/var, n − 1) and the largest fall from a running peak, over up to 250
  completed sessions and never fewer than 20. A holding counts only with a stored close on every session of the
  window; `coveredWeight` is the share of value that does. A one-day move beyond 35% is read as a split or bonus the
  stored prices are not adjusted for, and that holding is left out by name rather than shown as a crash. `note` says
  what was used. It is how today's portfolio would have moved, not how the account moved, and not a forecast.
  Daily closes come from the desk's candles table (resolution `D`, under the instrument master's symbol for the
  ISIN); a sync fetches what is missing through the desk's history connector, completed sessions only, at most
  `Portfolio:HistoryFetchesPerSync` (30) symbols, largest holding first.
* **Positions** as each broker reports them; **derivatives**: the open futures and options and their gross exposure,
  the market value at their own last price (notional for a future, premium for an option).
* **Funds** per broker: available, used and total, in the broker's own terms (FYERS's Available Balance, Utilized
  Amount and Total Balance; Dhan's available balance, utilised amount and start-of-day limit; Angel's available cash,
  utilised debits and net).
* **Data notes**: everything missing, said plainly: an unpriced holding, a holding valued at the desk's close, a day
  change not known, a broker not read yet or whose last read failed, figures over a day old, history gaps.

## The summary's model

A pseudo-agent, **Portfolio Analyst** (`portfolio-analyst`), named on its calls but not one of the AI page's agents.
It walks the analyst tier's chain with only its Amazon Bedrock models: NVIDIA's free tier is for development and
research, and serving a platform user is production use. With no Bedrock model switched on there is no summary, and
the report says so. Every call is private (`AiAskInput.Private`): the Calls tab keeps the call's model, timing,
tokens and cost, but not the prompt, the report or the answer, so an admin cannot read a user's holdings there.

The summary is written at each sync. Each view checks it again against the report shown: if a figure has changed
since, it is left out with a note, and the next sync writes a new one.

## API

The user is always the signed-in user, from the token. Any role. camelCase JSON.

| Route | What it does |
|---|---|
| `GET connections` | Every broker with its status (`not_connected`, `connected`, `expired`, `error`), masked account, times, last error, `connectMode` (`redirect` or `token`) and the fields to paste |
| `POST connections/{broker}/start` | `{ mode: "redirect", url }` (and the link cookie), or `{ mode: "token", fields }`; 503 when the broker's app is not set up |
| `GET callback/{broker}` | The broker's redirect target: 302 to `/portfolio?connected={broker}` or `/portfolio?error={message}` |
| `POST connections/{broker}/token` | `{ fields: { clientId, accessToken } }`: checked by one read, then the connection row; 400 `{ message }` |
| `DELETE connections/{broker}?forget=` | 204 |
| `POST sync` | `{ takenUtc, results: [{ broker, ok, error }] }`; 429 with Retry-After within a minute of the last |
| `GET report` | The report; 404 `{ message }` before the first sync |
| `GET history?days=90` | `[{ date, invested, current, pnl }]`: each day's last snapshot per broker read that day, nothing carried forward |
| `GET admin/connections` | Admin only: users per broker by status. No amounts, no holdings, no names |

The page should call `POST sync` after a `?connected=` return: linking reads the profile only.

## Privacy

Each user sees only their own links and snapshots: every query filters by the user id from the sign-in token, and no
route takes another user's id. Admins have no route to anyone's holdings, only the counts above, and the summary's
model calls keep no words. Connect, disconnect and sync are in the activity log (module `portfolio`), with the broker
and the masked account, never an amount or a token.

## Owner actions

1. **FYERS third-party app.** Only the creator can sign in to an individual app, so other users cannot link FYERS
   until there is a third-party app (FYERS creates these on request: see "Third-party apps" in its API docs). Register
   its redirect URL as **`https://openfno.com/api/Portfolio/callback/fyers`** and put it in the server's .env:
   `PORTFOLIO_FYERS_APP_ID`, `PORTFOLIO_FYERS_SECRET_KEY`,
   `PORTFOLIO_FYERS_REDIRECT_URI=https://openfno.com/api/Portfolio/callback/fyers`. Until then the platform's app is
   used, through `https://openfno.com/api/Auth/callback`, which works for the owner's own account only.
2. **Angel One redirect URL.** On the SmartAPI app (My Apps), set the redirect URL to
   **`https://openfno.com/api/Portfolio/callback/angel`**. If the platform's key is a new one whose redirect URL Angel
   does not honour yet, create a publisher app for the Portfolio page and set `PORTFOLIO_ANGEL_API_KEY`. Then link one
   account end to end and confirm what the redirect carries.
3. **Dhan partner login (optional).** To replace the pasted token with a sign-in at Dhan, apply to Dhan as a partner;
   Dhan then issues a `partner_id` and `partner_secret` for its consent flow (`auth.dhan.co/partner/…`). Not built
   until then.
4. **Sector and market-cap data (optional).** The report says Unknown until the desk holds a classification by ISIN
   (for example AMFI's half-yearly market-cap list and an industry master). `PortfolioReportInputs.Classify` is where
   it plugs in.
5. **Bedrock on** for the summary: the open models on Bedrock or Claude. With neither, the report has no summary.
