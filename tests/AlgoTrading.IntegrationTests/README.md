# PostgreSQL integration tests

Manual-order transaction tests use the actual trading database model and paper
booking services. They verify that fill events see the committed protective
levels and that a failed or cancelled protection write rolls back the signal,
order and position together.

Set `OPENFNO_TEST_POSTGRES` to a **disposable PostgreSQL test server** using a role
that can create databases, then run:

```sh
dotnet test tests/AlgoTrading.IntegrationTests/AlgoTrading.IntegrationTests.csproj
```

Authentication tests cover concurrent refresh, logout/revoke races, reuse, family
isolation, the unique active-token index, and rollback on failure or cancellation.

Each case creates and drops its own `manual_order_test_<guid>` or
`auth_session_test_<guid>` database. The
application's connection strings and local settings are not read. Do not point
this variable at a production server. Without the variable these cases are
reported as skipped; CI runs them with its own PostgreSQL service.

The tests use `EnsureCreated` to exercise current entity mappings and transaction
behavior. The session migration also runs its actual Up/Down commands against a
minimal predecessor auth schema and verifies credential invalidation. These tests
do not validate the complete migration history or TimescaleDB extensions.

## Expanded CI

`OPENFNO_TEST_TIMESCALE` runs the complete migration history on a fresh TimescaleDB
server (the production PostgreSQL major version is 15), then rehearses the latest
empty-table downgrade/re-upgrade while preserving a pre-existing account. Each
run creates and drops a uniquely named database. This does not claim that every
historical destructive migration can be reversed with production data.

`OPENFNO_TEST_REDIS` runs the actual Redis consumer-group implementation against a
disposable Redis 7 server: failed delivery, replacement-consumer reclaim, successful
acknowledgement and malformed-message dead-letter handling. It owns only random
`test:<guid>` stream keys and removes them afterward.

The Playwright job (`cd web && npx playwright test`) starts the production login and
manual-ticket UI with a test-only HTTP host and fresh PostgreSQL database. Real
auth/session/module policies, manual-order controllers and booking transactions are
used. Prices and risk acceptance are fixture inputs; no broker or live runner is
registered. It exercises lost-response/reload recovery, protected fills, denied
accounts, cross-tab logout and renderer compatibility. Browser traces/screenshots
are retained on failure. This targeted host is never deployed with the API.

The backtest project now books a deterministic replay round trip through the paper
service, including retry deduplication, future/live price isolation and realized
P&L. The Python suite independently exercises full strategy/replay/ledger behavior.
