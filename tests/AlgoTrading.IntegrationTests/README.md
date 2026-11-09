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
