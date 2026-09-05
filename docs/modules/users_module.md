# Users module

Accounts, what each may do, and how much they may put at risk. Console page: **`/admin/users`**.

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
  database by hand.
* **Enable / disable** — disabling revokes every refresh token immediately, so the account stops
  being signed in rather than merely being unable to sign in again.
* **Capital** and **max concurrent runs** — per trader; a blank run cap falls back to the
  platform-wide limit in Risk.
* **Module grants** — ticked per account.
* **Password reset** — sets a new password and signs that account out everywhere, so an old password
  cannot keep a live session. Anyone can change their own via `POST /api/Users/me/password`.
* **Sessions** — how many refresh tokens are live, and a button to revoke them.

Accounts are **disabled, not deleted**: the runs and orders they made keep their owner.

## Ending a session actually ends it

`AppUser.TokensValidFromUtc` is a per-account cutoff: **access tokens issued before it are refused**,
whatever their expiry says. Disabling an account, resetting its password or signing it out sets it to
now, so an existing token stops working immediately rather than lasting out its hour.

A cutoff rather than a denylist of token ids: one nullable column instead of a table that grows
forever, and "everything before now" is exactly what those three actions mean. Tokens carry an `iat`
claim, and the JWT bearer handler compares it on every request through
`ITokenValidityService`.

The comparison is at second resolution — `iat` is written to whole seconds — and a token issued *in*
the cutoff second is refused. The cost is one extra sign-in; the alternative, a second of slack, is a
window an automated caller could sit inside.

The answer is cached for 30 seconds and the entry is dropped the moment this process sets a cutoff,
so a single API sees the change at once. The TTL only bounds staleness if the platform is ever run as
more than one instance.

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

Console: the **Invitations** panel on `/admin/users`. The public page is `/invite/{token}`.

## API

Admin-only under `api/Users`, except `me/password`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/Users` | every account with role, status, capital, grants, sessions |
| GET | `/api/Users/modules` | the grantable module catalog |
| GET | `/api/Users/roles` | the roles that exist |
| PATCH | `/api/Users/{id}` | role, isActive, capital, run cap (partial) |
| PUT | `/api/Users/{id}/grants` | replace the account's grants |
| POST | `/api/Users/{id}/password` | admin reset (also signs out everywhere) |
| POST | `/api/Users/{id}/revoke-sessions` | sign the account out |
| POST | `/api/Users/me/password` | change your own, knowing the current one |

Account creation is still `POST /api/UserAuth/register`, admin-only.

## Migration note

`20260905184852_UserModuleGrants` creates `user_module_grants`, adds `app_user.MaxConcurrentRuns`,
and **grants all three modules to every existing active trader**. Those accounts had full access
before the migration; taking it away silently would have locked working traders out mid-session. The
engine's account is moved to the `Service` role at the same time.

## Next

Invite links, so an admin invites someone and that person sets their own password without it ever
passing through the admin. Safe to add precisely because a new account holds nothing until granted.
