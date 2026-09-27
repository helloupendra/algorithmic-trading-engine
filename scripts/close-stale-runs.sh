#!/usr/bin/env bash
# Closes live paper runs that were left "Pending": runs whose status was never
# written, some still holding open legs.
#
# Why it exists: before the Live Runner rebuild (3 Sep 2026) a live run was
# created Pending and was supposed to be moved on as it started and stopped.
# Twenty-four were never moved on. Seven of them still held 18 "open" legs on
# contracts that had long expired, and every page that lists runs or open
# positions kept showing them. Today a live run is created Running, so a new
# Pending live run means something has gone wrong again; the desk checkup
# reports one and points here.
#
# What "closed" means here, on purpose:
#   - each open leg becomes Closed with no P&L booked (realized stays what it
#     was, unrealized goes to 0) and ClosedUtc = when it was last updated;
#   - each run becomes Stopped, with CompletedUtc = its last activity, and a
#     RUN_STOPPED signal saying why, so the run history explains itself.
# Nothing is deleted. Closing the legs at a months-old mark would have booked
# a profit or loss no one ever made; closing them with none keeps every
# account's booked total exactly as it was.
#
# Usage: ./scripts/close-stale-runs.sh [--apply] [--older-than-days N]
#   (default)            list what would be closed, change nothing
#   --apply              back the rows up, then close them in one transaction
#   --older-than-days N  only runs created more than N days ago (default 1)
#
# The backup (CSV of the runs and their open legs) goes to
# $BACKUP_DIR/stale-runs-<time>/, $BACKUP_DIR defaulting to ~/backups.

set -euo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"

APPLY=0
DAYS=1
while [ $# -gt 0 ]; do
  case "$1" in
    --apply) APPLY=1 ;;
    --older-than-days) DAYS="${2:?--older-than-days needs a number}"; shift ;;
    -h|--help) sed -n '2,31p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
case "$DAYS" in ''|*[!0-9]*) echo "--older-than-days must be a whole number" >&2; exit 2 ;; esac

[ -f "$REPO_ROOT/.env" ] && { set -a; . "$REPO_ROOT/.env"; set +a; }
DB_CONTAINER="${DB_CONTAINER:-algotrading_db}"
psql_db() {
  docker exec -i "$DB_CONTAINER" psql -v ON_ERROR_STOP=1 -X -q \
    -U "${POSTGRES_USER:-postgres}" -d "${POSTGRES_DB:-algotrading}" "$@"
}

# The one definition of "stale", used by the listing, the backup and the
# update alike, so what is shown is exactly what is changed.
STALE="r.\"Mode\" = 'LivePaper' AND r.\"Status\" = 'Pending' AND r.\"StrategyName\" <> 'Manual' AND r.\"CreatedUtc\" < now() - interval '$DAYS days'"

echo "Live paper runs left Pending for more than $DAYS day(s):"
psql_db -P pager=off <<SQL
SELECT r."Id" AS run, r."StrategyName" AS strategy, r."UserId" AS account,
       to_char(r."CreatedUtc" AT TIME ZONE 'Asia/Kolkata', 'YYYY-MM-DD HH24:MI') AS created_ist,
       count(p."Id") FILTER (WHERE p."Status" = 'Open') AS open_legs,
       coalesce(sum(p."UnrealizedPnl") FILTER (WHERE p."Status" = 'Open'), 0) AS unrealized_dropped
FROM simulation_runs r
LEFT JOIN paper_positions p ON p."SimulationRunId" = r."Id"
WHERE $STALE
GROUP BY r."Id" ORDER BY r."Id";
SQL

COUNT="$(psql_db -At -c "SELECT count(*) FROM simulation_runs r WHERE $STALE")"
if [ "$COUNT" = "0" ]; then
  echo "Nothing to close."
  exit 0
fi
if [ "$APPLY" -ne 1 ]; then
  echo "Dry run: $COUNT run(s) would be closed. Run again with --apply to close them."
  exit 0
fi

BACKUP="${BACKUP_DIR:-$HOME/backups}/stale-runs-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$BACKUP"; chmod 700 "$BACKUP"
psql_db -c "COPY (SELECT r.* FROM simulation_runs r WHERE $STALE ORDER BY r.\"Id\") TO STDOUT WITH CSV HEADER" > "$BACKUP/simulation_runs.csv"
psql_db -c "COPY (SELECT p.* FROM paper_positions p JOIN simulation_runs r ON r.\"Id\" = p.\"SimulationRunId\" WHERE $STALE AND p.\"Status\" = 'Open' ORDER BY p.\"Id\") TO STDOUT WITH CSV HEADER" > "$BACKUP/paper_positions_open.csv"
echo "Backed up to $BACKUP"

REASON="Closed in clean-up on $(TZ=Asia/Kolkata date '+%-d %b %Y'): its status was never written (Pending since it was created). Open legs were closed with no P&L booked."

# One statement per table, all in one transaction, every one re-checking the
# same condition. The ids are fixed first so the three steps agree on the set.
psql_db <<SQL
BEGIN;
CREATE TEMP TABLE stale ON COMMIT DROP AS
  SELECT r."Id", r."StrategyName",
         greatest(r."CreatedUtc",
                  (SELECT max(p."UpdatedUtc") FROM paper_positions p WHERE p."SimulationRunId" = r."Id"),
                  (SELECT max(s."TimestampUtc") FROM simulation_signals s WHERE s."SimulationRunId" = r."Id")) AS last_activity
  FROM simulation_runs r WHERE $STALE;

INSERT INTO simulation_signals ("SimulationRunId", "StrategyName", "SignalType", "TimestampUtc", "Symbol", "GroupId", "MetadataJson", "CreatedUtc")
SELECT "Id", "StrategyName", 'RUN_STOPPED', last_activity, '', '',
       json_build_object('reason', \$reason\$$REASON\$reason\$, 'by', 'clean-up')::text, now()
FROM stale;

UPDATE paper_positions p
SET "Status" = 'Closed', "UnrealizedPnl" = 0, "ClosedUtc" = p."UpdatedUtc", "UpdatedUtc" = now()
FROM stale WHERE p."SimulationRunId" = stale."Id" AND p."Status" = 'Open';

UPDATE simulation_runs r
SET "Status" = 'Stopped', "CompletedUtc" = coalesce(r."CompletedUtc", stale.last_activity)
FROM stale WHERE r."Id" = stale."Id" AND r."Status" = 'Pending';
COMMIT;
SQL

echo "Closed $COUNT stale run(s)."
