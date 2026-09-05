# Activity log

Who did what, across every module. Console: **`/admin/system/logs`** (admin only —
it records admins too, so it must not be readable by the people it watches).

## What is recorded, and what is not

**Recorded:** every request that changes something — `POST`, `PUT`, `PATCH`,
`DELETE` — by whoever made it: admin, trader, or the engine's own service
account. Refusals are recorded too; a `403` is often the more interesting row.

Sign-ins are included, and they are the one case where "who" cannot come from
the token — there isn't one yet. The endpoint names the actor itself:

* a **successful** sign-in is attributed to the account, so clicking that person
  shows when they got in alongside everything else they did;
* a **failed** one stays anonymous but records the username that was tried
  (*Failed sign-in for "mallory"*). Attributing a failure to the real account
  would let anyone pollute someone else's trail by guessing at their password.

**Not recorded, deliberately:**

* **Reads.** They change nothing and are the overwhelming majority of traffic.
  Recording them would bury everything that matters.
* **Request and response bodies.** They carry passwords, broker secrets and
  tokens. An audit log that leaks credentials is worse than no audit log. The
  envelope — who, what path, what status, how long — plus the endpoint's own
  summary is enough to answer "who did this".
* **The engine's high-frequency posts** (`ticks/upsert`, `latest/upsert`,
  `heartbeat`). The ingestor writes a tick per symbol per second; logging those
  would produce millions of rows and teach everyone to ignore the log.
* **SignalR's handshake** (`/hubs/…/negotiate`). Every open console page posts
  one, and another on every reconnect. A browser left open for a session writes
  hundreds of rows that record nothing anyone did.

## Why it is automatic

The middleware records every mutating request rather than each endpoint
remembering to log itself. An audit trail that depends on someone remembering is
an audit trail with holes, and the holes are exactly where a mistake hides.

Where the path and status do not say enough, an endpoint adds a sentence:

```csharp
HttpContext.Describe(
    $"Deployed {strategy.Name} on {underlying} — run #{run.Id}, {lots} lot(s).",
    "run", run.Id.ToString());
```

The row then reads *"Deployed ShortStraddle on BANKNIFTY — run #46, 1 lot(s)."*
with `POST /api/Strategy/650824872/deploy` underneath it.

## The view

Two panes. **Who** lists every account that has done anything, with its action
count, how many were refused, and when it was last seen. Clicking one narrows the
whole page to that person and replaces the module list with *where they have
been* — a per-module rollup. **Actions** is the stream itself, filterable by
module, by free text over path/summary/username, and by "refused only".

The username and role are copied onto each row rather than joined: an account can
be deleted, and the record of what it did must still read.

## Where it sits among the other logs

| Log | Records | Where |
| --- | --- | --- |
| **Activity log** | what a *person* asked for | `activity_log` · this page |
| Alert events | what the *platform* announced (run starts and stops, kill switch, signals) | `alert_events` · System → Alerts |
| Risk events | risk decisions and limit changes | `risk_events` · System → Risk |
| Process logs | stdout of the ingestor and alerter processes | in-memory buffers · Data → Live feeds, System → Alerts |

## API

Admin-only under `api/ActivityLog`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/ActivityLog` | entries newest-first; filters `userId`, `module`, `action`, `succeeded`, `search`, `fromUtc`, `toUtc`, `limit`, `offset` |
| GET | `/api/ActivityLog/facets` | the filter options that actually exist in the data, so the console never offers a choice that returns nothing |
| GET | `/api/ActivityLog/users/{id}/summary` | one account's rollup: totals, failures, first and last seen, per module |
