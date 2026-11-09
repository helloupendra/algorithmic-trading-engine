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

Each case creates and drops its own `manual_order_test_<guid>` database. The
application's connection strings and local settings are not read. Do not point
this variable at a production server. Without the variable these cases are
reported as skipped; CI runs them with its own PostgreSQL service.

The tests use `EnsureCreated` to exercise current entity mappings and transaction
behavior. They do not validate the migration history or TimescaleDB extensions.
