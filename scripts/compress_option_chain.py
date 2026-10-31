#!/usr/bin/env python3
"""
Turn option_chain_snapshots into a compressed TimescaleDB hypertable, keeping every row.

By 31 Oct 2026 the chain recorder had written 50.7 million rows since 6 Sep:
15.7 GB in a plain table (9 GB of rows, 6.8 GB of indexes), the server's
largest after live_ticks and growing fastest once later expiries were recorded
too. Compressed by underlying and expiry, a day of minute captures shrinks to a
small fraction, and the readers (the chain page, OI history, straddle and IV,
ChainFlowBuy, the Drive archive) read it the same way.

The conversion never holds the live table for long, and can stop and resume:
  1. prepare   a new hypertable, option_chain_snapshots_ht: the same columns,
               one-day chunks, key ("Id", "CapturedUtc") as a hypertable needs,
               and the same indexes under temporary names;
  2. copy      one UTC day at a time from the live table, each day's chunk
               compressed as soon as it is copied (except the last two days,
               which the recorder may still be writing), so the disk never
               holds a second uncompressed copy;
  3. swap      in one short transaction: copy anything newer, check that both
               tables hold the same number of rows, then exchange the names of
               the tables, their keys, indexes and identity sequences, set the
               sequence past the highest id, and add the compression policy;
  4. drop-old  only when asked, after checking the counts again: drop the old
               table, which frees its 15.7 GB.

Every day is also on Drive (scripts/archive_to_drive.py), verified by MD5 and
row count, before any of this runs.

Usage (on the server, from the repository root):
  scripts/compress_option_chain.py plan        # what each step would do
  scripts/compress_option_chain.py prepare
  scripts/compress_option_chain.py copy
  scripts/compress_option_chain.py swap
  scripts/compress_option_chain.py drop-old
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
from datetime import date, datetime, timedelta, timezone
from typing import Dict, List

DB_CONTAINER = os.environ.get("ARCHIVE_DB_CONTAINER", "algotrading_db")
DB_USER = os.environ.get("ARCHIVE_DB_USER", "postgres")
DB_NAME = os.environ.get("ARCHIVE_DB_NAME", "algotrading")

TABLE = "option_chain_snapshots"
NEW = f"{TABLE}_ht"
OLD = f"{TABLE}_old"

#: Days the recorder may still write to: left uncompressed for the policy to compress later.
KEEP_OPEN_DAYS = 2

#: The live table's key, indexes and identity sequence, which the app and its
#: migrations know by these names.
INDEXES = ("ix_chain_captured_utc", "ix_chain_underlying_expiry_time", "ux_chain_symbol_time")
KEY = f"PK_{TABLE}"
SEQUENCE = f"{TABLE}_Id_seq"


# ------------------------------------------------------------------- pure --

def day_bounds(day: date) -> tuple:
    start = datetime(day.year, day.month, day.day, tzinfo=timezone.utc)
    return start.isoformat(), (start + timedelta(days=1)).isoformat()


def days_between(first: date, last: date) -> List[date]:
    days, d = [], first
    while d <= last:
        days.append(d)
        d += timedelta(days=1)
    return days


def days_to_copy(live: Dict[date, int], copied: Dict[date, int]) -> List[date]:
    """Days whose rows the new table does not hold in full yet."""
    return sorted(d for d, n in live.items() if copied.get(d, 0) != n)


def compressible(day: date, today: date) -> bool:
    """Whether a copied day may be compressed now: not one the recorder may still write to."""
    return day <= today - timedelta(days=KEEP_OPEN_DAYS)


def swap_statements() -> List[str]:
    """The renames that put the new table in the live one's place, under the names the app knows."""
    sql = [f'ALTER TABLE {TABLE} RENAME TO {OLD}',
           f'ALTER TABLE {OLD} RENAME CONSTRAINT "{KEY}" TO "{KEY}_old"']
    sql += [f'ALTER INDEX {name} RENAME TO {name}_old' for name in INDEXES]
    sql += [f'ALTER SEQUENCE "{SEQUENCE}" RENAME TO "{OLD}_Id_seq"',
            f'ALTER TABLE {NEW} RENAME TO {TABLE}',
            f'ALTER TABLE {TABLE} RENAME CONSTRAINT "{KEY}_ht" TO "{KEY}"']
    sql += [f'ALTER INDEX {name}_ht RENAME TO {name}' for name in INDEXES]
    sql += [f'ALTER SEQUENCE "{NEW}_Id_seq" RENAME TO "{SEQUENCE}"']
    return sql


# --------------------------------------------------------------- side effects --

def psql(sql: str) -> str:
    out = subprocess.run(
        ["docker", "exec", "-i", DB_CONTAINER, "psql", "-v", "ON_ERROR_STOP=1", "-U", DB_USER, "-d", DB_NAME, "-At"],
        input=sql, capture_output=True, text=True,
    )
    if out.returncode != 0:
        raise RuntimeError(f"psql failed: {out.stderr.strip()}")
    return out.stdout.strip()


def say(message: str) -> None:
    print(f"{datetime.now():%H:%M:%S}  {message}", flush=True)


def exists(name: str) -> bool:
    return psql(f"SELECT to_regclass('public.{name}') IS NOT NULL;") == "t"


def is_hypertable(name: str) -> bool:
    return psql(f"SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = '{name}';") == "1"


def counts_by_day(name: str) -> Dict[date, int]:
    rows = psql(f'SELECT ("CapturedUtc" AT TIME ZONE \'UTC\')::date, count(*) FROM {name} GROUP BY 1 ORDER BY 1;')
    result = {}
    for line in rows.splitlines():
        day, n = line.split("|")
        result[date.fromisoformat(day)] = int(n)
    return result


def count(name: str) -> int:
    return int(psql(f"SELECT count(*) FROM {name};") or 0)


def prepare() -> None:
    if exists(NEW):
        say(f"{NEW} is already there")
        return
    if is_hypertable(TABLE):
        raise SystemExit(f"{TABLE} is already a hypertable: nothing to convert.")
    say(f"creating {NEW}")
    psql(f"""
BEGIN;
CREATE TABLE {NEW} (LIKE {TABLE} INCLUDING DEFAULTS INCLUDING IDENTITY);
ALTER TABLE {NEW} ADD CONSTRAINT "{KEY}_ht" PRIMARY KEY ("Id", "CapturedUtc");
SELECT create_hypertable('{NEW}', by_range('CapturedUtc', INTERVAL '1 day'), create_default_indexes => false);
CREATE INDEX ix_chain_captured_utc_ht ON {NEW} ("CapturedUtc");
CREATE INDEX ix_chain_underlying_expiry_time_ht ON {NEW} ("Underlying", "ExpiryDate", "CapturedUtc");
CREATE UNIQUE INDEX ux_chain_symbol_time_ht ON {NEW} ("Symbol", "CapturedUtc");
ALTER TABLE {NEW} SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"Underlying", "ExpiryDate"',
    timescaledb.compress_orderby = '"CapturedUtc" DESC, "Symbol", "Id"');
COMMIT;
""")
    say(f"{NEW} ready: one-day chunks, compressed by underlying and expiry")


def compress_day(day: date) -> None:
    lo, hi = day_bounds(day)
    psql(f"SELECT count(compress_chunk(c, if_not_compressed => true)) "
         f"FROM show_chunks('{NEW}', newer_than => '{lo}'::timestamptz - interval '1 second', older_than => '{hi}'::timestamptz) c;")


def copy() -> None:
    if not exists(NEW):
        raise SystemExit(f"run prepare first: {NEW} does not exist.")
    today = datetime.now(timezone.utc).date()
    live, copied = counts_by_day(TABLE), counts_by_day(NEW)
    todo = days_to_copy(live, copied)
    say(f"{len(live)} days in {TABLE}, {len(todo)} to copy")
    for day in todo:
        lo, hi = day_bounds(day)
        started = time.monotonic()
        if copied.get(day):
            # A day left half-done cannot happen (each copy is one statement), but never add a second copy.
            raise SystemExit(f"{day}: {NEW} holds {copied[day]:,} rows and {TABLE} {live[day]:,}; check by hand.")
        psql(f'INSERT INTO {NEW} SELECT * FROM {TABLE} WHERE "CapturedUtc" >= \'{lo}\' AND "CapturedUtc" < \'{hi}\';')
        if compressible(day, today):
            compress_day(day)
        say(f"{day}: {live[day]:,} rows copied{' and compressed' if compressible(day, today) else ''} "
            f"in {time.monotonic() - started:.0f} s")
    sizes = psql(f"SELECT pg_size_pretty(hypertable_size('{NEW}')), pg_size_pretty(pg_total_relation_size('{TABLE}'));")
    say(f"sizes now: {NEW} | {TABLE} = {sizes}")


def swap() -> None:
    if not exists(NEW):
        raise SystemExit(f"nothing to swap: {NEW} does not exist.")
    live, copied = counts_by_day(TABLE), counts_by_day(NEW)
    behind = [d for d in days_to_copy(live, copied) if d < max(live)]
    if behind:
        raise SystemExit(f"run copy first: {len(behind)} earlier days are not copied in full ({behind[0]} first).")
    last = max(live)
    lo, _ = day_bounds(last)
    say("swapping in one transaction")
    psql("\n".join([
        "BEGIN;",
        f"LOCK TABLE {TABLE} IN ACCESS EXCLUSIVE MODE;",
        # Whatever was written since the last day was copied, if anything.
        f'DELETE FROM {NEW} WHERE "CapturedUtc" >= \'{lo}\';',
        f'INSERT INTO {NEW} SELECT * FROM {TABLE} WHERE "CapturedUtc" >= \'{lo}\';',
        "DO $$ BEGIN",
        f"  IF (SELECT count(*) FROM {TABLE}) <> (SELECT count(*) FROM {NEW}) THEN",
        "    RAISE EXCEPTION 'row counts differ: nothing renamed';",
        "  END IF;",
        "END $$;",
        *[s + ";" for s in swap_statements()],
        f"SELECT setval(pg_get_serial_sequence('{TABLE}', 'Id'), (SELECT max(\"Id\") FROM {TABLE}));",
        "COMMIT;",
    ]))
    psql(f"SELECT add_compression_policy('{TABLE}', INTERVAL '{KEEP_OPEN_DAYS} days', if_not_exists => true);")
    say(f"{TABLE} is now the compressed hypertable; the old table is {OLD}, kept until drop-old")


def drop_old() -> None:
    if not exists(OLD):
        say(f"{OLD} is already gone")
        return
    old, new = count(OLD), count(TABLE)
    if new < old:
        raise SystemExit(f"{TABLE} holds {new:,} rows, fewer than {OLD}'s {old:,}: not dropping it.")
    psql(f"DROP TABLE {OLD};")
    say(f"dropped {OLD} ({old:,} rows; {TABLE} holds {new:,})")


def plan() -> None:
    say(f"{TABLE}: {count(TABLE):,} rows, hypertable: {is_hypertable(TABLE)}; {NEW} exists: {exists(NEW)}; "
        f"{OLD} exists: {exists(OLD)}")
    if exists(NEW):
        live, copied = counts_by_day(TABLE), counts_by_day(NEW)
        say(f"days still to copy: {len(days_to_copy(live, copied))} of {len(live)}")


def main(argv: List[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("step", choices=["plan", "prepare", "copy", "swap", "drop-old"])
    step = parser.parse_args(argv).step
    {"plan": plan, "prepare": prepare, "copy": copy, "swap": swap, "drop-old": drop_old}[step]()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
