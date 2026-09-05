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

## Known gap

Disabling revokes refresh tokens, and every module-gated endpoint refuses the account immediately.
An **access token already issued** still works on endpoints that carry no module gate until it
expires — the JWT lifetime is 60 minutes. Closing that needs a token denylist or a much shorter
access-token lifetime; it is a deliberate open item, not an oversight.

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
