#!/usr/bin/env bash
# shellcheck disable=SC2319  # "$(cond; echo $?)" is how check() takes a result
# scripts/aws/migrate-db.sh (2 Nov review, F5), against a stubbed docker and sudo
# and a stubbed desk-common.sh, so no database is touched:
#   - restore refuses a database with data, before it stops anything, unless
#     --wipe is given with the database's name; a fresh one needs no flag;
#   - a failed pg_restore or hypertable load exits non-zero and never says "done";
#   - dump carries the hypertables' rows, and restore loads and counts them.
# This proves the script's decisions, not PostgreSQL's behaviour: its header says
# what has and has not been run against a real TimescaleDB.
# Run: bash scripts/tests/migrate-db.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
REPO="$PWD"

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
CALLS="$WORK/calls"
mkdir -p "$WORK/bin" "$WORK/repo/scripts/lib" "$WORK/home/.aspnet/DataProtection-Keys"
echo key >"$WORK/home/.aspnet/DataProtection-Keys/key-1.xml"

cat >"$WORK/repo/scripts/lib/desk-common.sh" <<'EOF'
say() { echo "say: $1"; }
api_stop() { echo "api_stop" >>"$CALLS"; }
api_start() { echo "api_start" >>"$CALLS"; }
EOF

cat >"$WORK/bin/sudo" <<'EOF'
#!/usr/bin/env bash
echo "sudo $*" >>"$CALLS"
EOF

# The container: answers the script's SQL by what it asks for, from the FAKE_ settings.
cat >"$WORK/bin/docker" <<'EOF'
#!/usr/bin/env bash
if [ "$1" = compose ]; then echo "docker $*" >>"$CALLS"; exit 0; fi
shift 3   # exec -i algotrading_db
tool="$1"; shift
case "$tool" in
  pg_isready) exit "${FAKE_DB_DOWN:-0}" ;;
  pg_dump) echo "pg_dump $*" >>"$CALLS"; echo dump-bytes; exit 0 ;;
  pg_restore)
    echo "pg_restore" >>"$CALLS"
    if [ "${FAKE_RESTORE_RC:-0}" != 0 ]; then echo "pg_restore: error: could not execute query" >&2; fi
    exit "${FAKE_RESTORE_RC:-0}" ;;
esac
query="${*: -1}"
case "$query" in
  *query_to_xml*)
    if [ -n "${FAKE_GUARD_FAILS:-}" ]; then echo "ERROR: boom" >&2; exit 3; fi
    printf '%s' "${FAKE_HOLDING:-}"; [ -z "${FAKE_HOLDING:-}" ] || echo ;;
  *timescaledb_information.hypertables*)
    if [ -n "${FAKE_HYPERTABLES_FAIL:-}" ]; then echo "ERROR: boom" >&2; exit 3; fi
    printf '%s\n' option_history_bars mf_nav ;;
  *information_schema.columns*) echo '"BarStartUtc", "Close"' ;;
  *string_agg*) echo 'app_user, candles, option_history_bars' ;;
  TRUNCATE*) echo "TRUNCATE" >>"$CALLS" ;;
  COPY\ \(*) echo "copy-out $query" >>"$CALLS"; printf '2026-01-01\t1\n2026-01-02\t2\n' ;;
  *FROM\ STDIN*)
    n="$(wc -l | tr -d ' ')"
    echo "copy-in $query rows=$n" >>"$CALLS"
    if [ -n "${FAKE_COPY_FAILS:-}" ]; then echo "ERROR: boom" >&2; exit 3; fi
    echo "COPY $((n - ${FAKE_COPY_SHORT:-0}))" ;;
  *count*) echo 7 ;;
  *) echo "unexpected sql: $query" >>"$CALLS"; exit 9 ;;
esac
EOF
chmod +x "$WORK/bin/sudo" "$WORK/bin/docker"

script() {  # arguments for migrate-db.sh; prints stdout and stderr, then rc=
  rc=0
  CALLS="$CALLS" PATH="$WORK/bin:$PATH" HOME="$WORK/home" REPO_ROOT="$WORK/repo" \
    bash "$REPO/scripts/aws/migrate-db.sh" "$@" 2>&1 || rc=$?
  echo "rc=$rc"
}
called() { grep -q "$1" "$CALLS"; }
fresh() { : >"$CALLS"; }
says() { grep -q "$2" <<<"$1"; }

echo "dump"
: >"$CALLS"
out="$(script dump)"
TAR="$(ls "$WORK"/repo/algotrading-*.tar 2>/dev/null | head -1)"
check "it finishes and writes a tar" "$(says "$out" '^rc=0' && test -f "$TAR"; echo $?)"
mkdir "$WORK/unpacked" && tar -xf "$TAR" -C "$WORK/unpacked"
check "the tar holds the dump and the keys" "$(test -f "$WORK/unpacked/algotrading.dump" && test -f "$WORK/unpacked/DataProtection-Keys/key-1.xml"; echo $?)"
check "each hypertable's rows, through a query that reads the chunks" \
  "$(gzip -dc "$WORK/unpacked/hypertables/option_history_bars.copy.gz" | grep -q '2026-01-02' && called 'copy-out COPY (SELECT "BarStartUtc", "Close" FROM public."mf_nav") TO STDOUT'; echo $?)"
check "and how many left, for the restore to compare" "$(test "$(cat "$WORK/unpacked/hypertables/mf_nav.rows")" = 2; echo $?)"
check "raw ticks and the hypertables' own empty tables are left out of pg_dump's data" \
  "$(grep -q 'pg_dump .*--exclude-table-data=live_ticks --exclude-table-data=market_ticks --exclude-table-data=option_history_bars --exclude-table-data=mf_nav' "$CALLS"; echo $?)"
rm -f "$WORK"/repo/algotrading-*.tar
out="$(FAKE_HYPERTABLES_FAIL=1 script dump)"
check "a hypertable list it cannot read fails the dump, rather than leaving their rows out" \
  "$(! says "$out" '^rc=0' && ! ls "$WORK"/repo/algotrading-*.tar >/dev/null 2>&1; echo $?)"

( cd "$WORK/unpacked" && tar -cf "$WORK/good.tar" . )
mkdir "$WORK/oldstyle" && cp "$WORK/unpacked/algotrading.dump" "$WORK/oldstyle/"
( cd "$WORK/oldstyle" && tar -cf "$WORK/old.tar" . )

echo "restore refuses a database with data"
HOLDING=$'app_user  6\ncandles  1001'
fresh; out="$(FAKE_HOLDING="$HOLDING" script restore "$WORK/good.tar")"
check "exit 3, naming what it holds" "$(says "$out" '^rc=3' && says "$out" 'REFUSED' && says "$out" 'candles  1001'; echo $?)"
check "it tells the flag to use" "$(says "$out" 'restore .*good.tar --wipe algotrading'; echo $?)"
check "nothing was stopped or changed" "$(! called 'systemctl stop' && ! called api_stop && ! called TRUNCATE && ! called pg_restore; echo $?)"
fresh; out="$(FAKE_HOLDING="$HOLDING" script restore "$WORK/good.tar" --wipe otherdb)"
check "the flag with another name is refused too" "$(says "$out" '^rc=3' && says "$out" 'names otherdb' && ! called 'systemctl stop' && ! called TRUNCATE; echo $?)"
fresh; out="$(script restore "$WORK/good.tar" --wipe otherdb)"
check "even on an empty database: the name is the confirmation" "$(says "$out" '^rc=3' && ! called TRUNCATE; echo $?)"
fresh; out="$(FAKE_HOLDING="$HOLDING" script restore "$WORK/good.tar" --wipe)"
check "the flag with no name is a usage error" "$(says "$out" '^rc=2' && ! called TRUNCATE; echo $?)"
fresh; out="$(FAKE_HOLDING="$HOLDING" script restore "$WORK/good.tar" --force)"
check "an unknown flag is refused, not ignored" "$(says "$out" '^rc=2' && ! called TRUNCATE; echo $?)"
fresh; out="$(FAKE_GUARD_FAILS=1 script restore "$WORK/good.tar" --wipe algotrading)"
check "a database it cannot read is refused, whatever the flag" "$(says "$out" '^rc=3' && says "$out" 'could not read' && ! called 'systemctl stop' && ! called TRUNCATE; echo $?)"

echo "restore goes ahead when it is safe, or asked for by name"
fresh; out="$(script restore "$WORK/good.tar")"
check "a fresh database needs no flag" "$(says "$out" '^rc=0' && says "$out" 'say: done' && called TRUNCATE; echo $?)"
order="$(grep -nE '^(sudo systemctl stop|api_start|TRUNCATE|pg_restore|copy-in)' "$CALLS" | cut -d: -f2 | cut -d' ' -f1 | uniq | tr '\n' ' ')"
check "in order: stop, schema, empty, load, hypertables" "$(test "$order" = 'sudo api_start TRUNCATE pg_restore copy-in '; echo $?)"
check "each hypertable is loaded with its named columns" \
  "$(called 'copy-in COPY public."option_history_bars" ("BarStartUtc", "Close") FROM STDIN rows=2' && called 'copy-in COPY public."mf_nav" ("BarStartUtc", "Close") FROM STDIN rows=2'; echo $?)"
check "and said, with its count" "$(says "$out" 'say:   hypertable mf_nav: 2 row(s), as many as the dump holds'; echo $?)"
fresh; out="$(FAKE_HOLDING="$HOLDING" script restore "$WORK/good.tar" --wipe algotrading)"
check "a database with data, with --wipe and its name" "$(says "$out" '^rc=0' && says "$out" 'say: done' && called TRUNCATE; echo $?)"
check "says what it is about to empty" "$(says "$out" 'about to be emptied' && says "$out" 'candles  1001'; echo $?)"
fresh; out="$(FAKE_DB_DOWN=1 script restore "$WORK/good.tar")"
check "a stopped database is started first, to be read" "$(called 'docker compose up -d --wait timescaledb'; echo $?)"

echo "A failed load is loud"
fresh; out="$(FAKE_RESTORE_RC=1 script restore "$WORK/good.tar" --wipe algotrading)"
check "pg_restore fails: non-zero, and no 'done'" "$(says "$out" '^rc=1' && says "$out" 'RESTORE FAILED' && ! says "$out" 'say: done'; echo $?)"
check "pg_restore's own words are shown" "$(says "$out" 'pg_restore: error'; echo $?)"
check "it says not to start the desk, and how to run it again" "$(says "$out" 'Do not start the desk' && says "$out" 'restore <file.tar> --wipe algotrading'; echo $?)"
check "and stops there: no hypertables, no keys" "$(! called copy-in && ! says "$out" 'keys installed'; echo $?)"
fresh; out="$(FAKE_COPY_FAILS=1 script restore "$WORK/good.tar")"
check "a hypertable that will not load: non-zero, and no 'done'" "$(says "$out" '^rc=1' && says "$out" 'could not load the hypertable' && ! says "$out" 'say: done'; echo $?)"
fresh; out="$(FAKE_COPY_SHORT=1 script restore "$WORK/good.tar")"
check "a hypertable that took fewer rows than the dump: non-zero, and no 'done'" "$(says "$out" '^rc=1' && says "$out" "took '1' row(s) of the dump's 2" && ! says "$out" 'say: done'; echo $?)"

echo "A dump from before hypertables were carried"
fresh; out="$(script restore "$WORK/old.tar")"
check "loads, and says plainly their rows are not in it" "$(says "$out" '^rc=0' && says "$out" 'WARNING: this dump has no hypertables folder' && ! called copy-in; echo $?)"

echo "Arguments"
out="$(script restore /no/such/file.tar)"
check "no such file: usage" "$(says "$out" '^rc=2' && says "$out" 'usage:'; echo $?)"
out="$(script nonsense)"
check "unknown command: usage" "$(says "$out" '^rc=2'; echo $?)"

echo
if [ "$FAILS" -gt 0 ]; then echo "$FAILS check(s) FAILED"; exit 1; fi
echo "all passed"
