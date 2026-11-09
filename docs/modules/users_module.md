# Users module

Accounts, what each may do, and how much they may put at risk. Console: **System → People**
(`/system/people`, with Packages and Invites as its other tabs).

## The idea that matters

**Grants are deny-by-default and enforced on the server.** A new account can sign in and do nothing.
Unticking a module is not cosmetic: the matching endpoints answer 403 for that trader whether they
use the console or `curl`. Hiding a menu entry is not access control — anyone can type a URL.

This is also what makes any future signup flow safe. The gate is not registration; it is the fact
that a brand-new account holds nothing.

## Roles

| Role | What it is |
| --- | --- |
| `Admin` | Everything, including user management and the kill switch. Holds every module by definition. |
| `Trader` | A person who trades. Sees only their own runs. Holds exactly the modules granted. |
| `Service` | A machine account — the Python engine signs in as one. No capital, no grants, never in the traders list, and it cannot reach anything behind the admin policy. |

`Service` exists because the engine's account used to sit in the `Trader` role, where it was
indistinguishable from a person with a trader's rights.

## Grantable modules

Defined server-side in `Domain/Constants/PlatformModules.cs`; the console renders whatever that list
contains.

| Key | Allows | Enforced on |
| --- | --- | --- |
| `strategies` | Deploy, monitor and stop their own live runs | `StrategyController`, `SimulatorController` |
| `backtesting` | Run backtests and read results | `BacktestController` |
| `market-data` | Charts, option chain, watchlist, movers, news | `InstrumentsController`, `MarketIntelController` |

Enforcement is the `[RequireModule(key)]` filter: admins and service accounts pass by role, a trader
passes only with a grant, and a **disabled account never passes**.

## What an admin can do

* **Role** — with two guards that cannot be overridden: you cannot remove your own admin role, and
  the last active admin cannot be demoted or disabled. Both are unrecoverable without editing the
  database by hand. A change of role signs the account out everywhere: `AdminOnly` reads the role from
  the token, so a demoted admin must not keep the one they hold.
* **Enable / disable** — disabling revokes every refresh token immediately, so the account stops
  being signed in rather than merely being unable to sign in again.
* **Capital** and **max concurrent runs** — per trader; a blank run cap falls back to the
  platform-wide limit in Risk.
* **Module grants** — ticked per account.
* **Password reset** — sets a new password and signs that account out everywhere, so an old password
  cannot keep a live session. A person (an admin or a trader, not the Service account) changes their
  own via `POST /api/Users/me/password`, which signs them out everywhere too, the session that asked
  included: they sign in again with the new password. Five attempts per account in 15 minutes.
* **Sessions** — how many refresh tokens are live, and a button to revoke them.

Accounts are **disabled, not deleted**: the runs and orders they made keep their owner.

## Ending a session actually ends it

Each login creates a persisted `UserSession` and a refresh-token family. Its JWT
carries `sid`. Every HTTP authentication checks the session's owner, expiry,
revocation and account status directly in the database; session revocation is
not cached. Ordinary logout revokes only that login, including every rotated
refresh token in the family. Other devices remain signed in.

`POST /api/UserAuth/logout` accepts the current or a previously rotated refresh
credential, including after the access token expires. An authenticated caller
must supply its own family's credential, or omit it to revoke its JWT session.
A refresh credential authorizes revocation of its family only. Repeated logout
is harmless. The endpoint uses the sign-in rate limit.

Changing role/password, disabling an account or choosing **Revoke sessions**
sets `AppUser.TokensValidFromUtc` and revokes all persisted sessions and refresh
tokens. JWT `iat` is compared at whole-second resolution; a token issued in the
cutoff second is refused and may require another sign-in. The legacy account-only
check retains a 30-second cache, but JWT authentication does not use that cache.

Logout closes the matching local live-feed sockets immediately. Account-wide
actions close that account's sockets. Each API also checks connected sessions
every five seconds, covering revocation by another process while the database
is reachable. A session is checked again after socket registration to close the
handshake/logout race. Pending shared-broker OAuth callbacks validate their
initiating session before exchanging the authorization code. Revocation cannot
undo a request already accepted before logout.

## Refresh rotation

Login, refresh, logout and account-wide revocation take the same PostgreSQL user
row lock. Parent consumption and child insertion commit together. A partial
unique index permits at most one active refresh token per session. Failed or
cancelled writes roll back both changes.

A second use of the parent within ten seconds returns `409 refresh_already_used`
without issuing credentials or ending the session. Reuse after that grace period
revokes the family and returns `401 refresh_reused`. The grace period tolerates
concurrent tabs but does not let a replay retrieve the child token. A lost
rotation response with no surviving tab holding the child requires fresh login.

Console tabs share a Web Lock for their browser login and re-read stored tokens
before refreshing. Browsers without Web Locks wait briefly for a winning tab's
storage update on a 409. Conflicts, network failures, 429s and server errors
preserve credentials and report a retryable error; a definitive rejection ends
the browser login.

## Session migration rollout

`20261109112208_UserLoginSessions` invalidates existing access/refresh credentials
and adds the login-session schema. Stop all API instances, apply the migration,
then start the matching API and console build. Reload open tabs and sign in
again, including unattended service clients. Do not mix old and new API versions
during this change: the old version does not enforce `sid`. Rolling this migration
back also invalidates credentials; do not expect old sessions to resume.

## Browser session boundaries

Each console login has a fresh browser session identity. The initial token pair and
identity are stored in one localStorage record. Rotations store an atomic token
pair under that identity without overwriting the active-login record. Logout or another login aborts the old session's pending requests,
rejects late responses, clears the query cache and stops its live connection.
Storage events synchronize login/logout across tabs, and requests also check the
current identity before retrying or returning data. An old action cannot retry
with the next account's credentials, including when both logins use the same user.

The browser boundary complements the persisted server-session checks above; it
also prevents delayed responses from a previous login appearing in a new login.

Existing token pairs migrate on the first load of the updated console. Reload all
open tabs after deployment so every tab uses the same storage format; older
bundles do not understand the new record. A rollback to an older bundle requires
signing in again.

## Invitations

Signing up is not open. An admin creates an invitation; the person sets **their own password**, so it
never passes through the admin or a chat message.

* `POST /api/Invites` — admin only. Returns the link **once**; only the token's SHA-256 is stored, so
  a database dump cannot hand anyone a working invite.
* `GET /api/Invites/{token}` and `POST /api/Invites/{token}/accept` — anonymous, because the person
  has no account yet. Both answer the same message for expired, used, revoked and never-existed, so
  the endpoint cannot be used to probe for valid tokens.
* An invite works **once** and expires (7 days by default, 90 maximum).
* `POST /api/Invites/{id}/revoke` cancels an unused one.

The admin chooses what the account starts with — module grants and a strategy package, both optional.
**What makes this safe is not the invite but what it creates:** leave both empty and the account can
sign in and do nothing. Verified: a new account with the `strategies` module but no package sees
zero strategies.

Console: the **Invites** tab of System → People (`/system/people/invites`). The public page is `/invite/{token}`.

## API

Admin-only under `api/Users`, except `me/password`, which is `OwnPasswordController`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/Users` | every account with role, status, capital, grants, sessions |
| GET | `/api/Users/modules` | the grantable module catalog |
| GET | `/api/Users/roles` | the roles that exist |
| PATCH | `/api/Users/{id}` | role, isActive, capital, run cap (partial) |
| PUT | `/api/Users/{id}/grants` | replace the account's grants |
| POST | `/api/Users/{id}/password` | admin reset (also signs out everywhere) |
| POST | `/api/Users/{id}/revoke-sessions` | sign the account out |
| POST | `/api/Users/me/password` | change your own, knowing the current one (also signs out everywhere) |

Account creation is still `POST /api/UserAuth/register`, admin-only.

## Migration note

`20260905184852_UserModuleGrants` creates `user_module_grants`, adds `app_user.MaxConcurrentRuns`,
and **grants all three modules to every existing active trader**. Those accounts had full access
before the migration; taking it away silently would have locked working traders out mid-session. The
engine's account is moved to the `Service` role at the same time.

## Next

Invite links, so an admin invites someone and that person sets their own password without it ever
passing through the admin. Safe to add precisely because a new account holds nothing until granted.

## A trader's broker account (Account page)

Traders trade at the **simulated broker** ([OpenFNO Broker](https://broker.openfno.com)), and an
administrator opens their account for them. There is nothing on the trader's Account page (`/account`) to link: the page
shows the account the platform issued — client id, money, positions, the day's orders, and whether the
account is stopped — all read from the broker on each request, never cached here.

**Show API credentials** hands the trader the four things an API client needs: client id, app id, app
secret and the TOTP secret. The broker shows the app secret and the TOTP secret once each, so the
platform stores them encrypted (ASP.NET Data Protection, `sim_broker_accounts`); without that, a trader
who lost them would have to have the whole account reopened. It is a POST, not part of the page's own
data, so secrets travel only when someone asks for them.

**From the admin side**, every user's row on System → People (`/system/people`) has a **Broker account** section:

| Action | What it does |
| --- | --- |
| Open an account | Opens it at the broker, issues the app whitelisted to this server's address, and pays the opening balance in |
| Pay in / out | Moves money on the broker's ledger (a negative amount pays out) |
| Show credentials | The trader's client id, app id, app secret and TOTP secret |
| Stop this account | The broker's kill switch: working orders cancelled, open positions closed |

The app is whitelisted to the address **the broker says it sees** for this server, asked of it at the
moment the app is issued — the static-IP check compares that address, so anything else would issue an
app that cannot place an order. `SIMBROKER_STATIC_IP` adds more addresses for a second machine.

Two deliberate limits. Disabling a trader's link does **not** touch their account at the broker: money
and positions are not something a checkbox should destroy, so the account stays and simply stops being
offered. And issuing is three calls to a separate system — open, issue the app, pay in — which cannot be
one transaction; if the app fails, the account exists at the broker with no app, the platform saves
nothing, and the failure is logged in the broker's own words rather than hidden behind a retry.

Per-trader **FYERS** linking was removed on 2026-09-21: a trader's orders belong at the simulated
broker, and real money stays with the platform's own shared FYERS account, which is the operator's.
The `broker_accounts` rows an earlier version wrote are left where they are.

Traders are never told about the platform broker or the feed — those are the operator's; a
trader's Strategies page shows only the kill switch and their concurrent-run ceiling.
