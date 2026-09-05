# Strategy packages

Which strategies a trader may run, and the ceilings that come with them.
Console: **`/admin/users`** (assignment) and the packages API below.

## Why a package rather than checkboxes

With 15 strategies and growing, per-trader checkboxes stop scaling. But a package
that only listed strategies would barely beat them. The value is that a package
**also carries limits**: on this platform every trader runs on the same broker
connection and the same capital, so deciding what a trader may run *is* deciding
how much they may risk.

A package holds:

| Field | Meaning |
| --- | --- |
| Strategies | Explicit membership by **name** |
| `IncludesAllStrategies` | Covers the whole catalog, including strategies written later |
| `MaxLotsPerRun` | Ceiling on lots for a single run |
| `MaxConcurrentRuns` | How many live runs a holder may keep open |
| `AllowedUnderlyings` | Empty means whatever the strategy itself supports |
| `AllowLiveMode` | False keeps the holder on paper |

A trader holds **one package**, plus optional **per-trader overrides** — extra
strategies on top, so an admin never has to clone a package to add one strategy
for one person. One package rather than several removes the question of whose
limit wins when two packages disagree.

## Two decisions worth keeping

**Membership is by strategy name, not by catalog id.** That id is a hash of the
name, so renaming a strategy changes it and would silently break every row
pointing at it. Keyed by name, a rename breaks loudly: the strategy simply stops
appearing in the package until someone fixes it. The API also refuses a name the
engine cannot run, so a typo cannot sit there granting nothing.

**`IncludesAllStrategies` is the one place a new strategy reaches a trader
without anyone deciding it should** — exactly the risk explicit membership
exists to avoid. It is there because it is genuinely wanted for a fully trusted
trader, and because accounts that predate packages had precisely this access and
could not be migrated to an explicit list. Wherever it appears, label it.

## Where limits collide

Three caps can disagree — the package's, the account's (`AppUser.MaxConcurrentRuns`)
and the platform's (Risk limits). **The tightest wins**, because each was set to
stop something.

## Enforcement

`GET /api/Strategy` filters the catalog to what the caller may run — a courtesy,
so a trader is not shown buttons that would be refused.

`POST /api/Strategy/{id}/deploy` is what actually stops anything. Immediately
before a runner is launched it checks strategy membership, underlying, lots, mode
and the open-run count, and answers **403 with the reason**:

```
Titli is not in your package (Starter). Ask an admin to add it.
Your package allows BANKNIFTY, not NIFTY.
Your package allows at most 2 lot(s) per run; this run asks for 9.
Your package is paper-trading only. Ask an admin before running with real money.
You already have 1 run(s) open and your limit is 1. Stop one first.
```

Admins and `Service` accounts are unrestricted.

## API

Admin-only under `api/StrategyPackages`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/StrategyPackages/catalog` | strategy names a package can be built from |
| GET | `/api/StrategyPackages` | packages with membership and holder counts |
| POST | `/api/StrategyPackages` | create |
| PUT | `/api/StrategyPackages/{id}` | rename, limits, enable/disable |
| PUT | `/api/StrategyPackages/{id}/strategies` | replace membership |
| DELETE | `/api/StrategyPackages/{id}` | delete (holders fall back to no package) |
| PUT | `/api/Users/{id}/strategy-grants` | per-trader extras |

Assignment is `PATCH /api/Users/{id}` with `strategyPackageId` (`-1` removes it).

## Migration note

`20260905191738_StrategyPackages` creates the tables and seeds one package,
**"Full access (migrated)"** with `IncludesAllStrategies = true`, assigning every
existing active trader to it. Those accounts could run the whole catalog before
packages existed; dropping them to nothing would have locked them out mid-session.
Narrow or replace it deliberately.
