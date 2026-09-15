#!/usr/bin/env python3
"""
Copies each finished trading day of market data to Google Drive, proves the
copy, and only then (when asked) frees the server's disk.

Why: the owner wants every tick kept for analysis, and the server's 40 GB disk
took about 6 GB a day once Dhan's full feed and chain were recorded
(2026-09-15). Drive is cheap cold storage; the database keeps the recent days
that live screens and backtests read.

For every (table, day) not yet archived:
  1. count the day's rows in the database;
  2. stream them as CSV through gzip straight to Drive (`rclone rcat`), with
     no copy on the server's disk, hashing the bytes on the way;
  3. prove the upload: Drive's MD5 must equal ours, and reading the file back
     must give the same number of rows;
  4. record the result in a manifest (on the server and on Drive).

Nothing is deleted unless `--drop-older-than N` is given, and then only days
whose every table is in the manifest as verified.

A day is the UTC date, which is also the IST trading date: NSE (03:45-10:00
UTC) and MCX (03:30-18:25 UTC) both sit inside one UTC day. A day is archived
only once it is over in UTC (05:30 IST the next morning), so nothing still
being written is ever copied.

Usage (on the server, from the repository root):
  scripts/archive_to_drive.py                         # archive every closed day
  scripts/archive_to_drive.py --day 2026-09-15        # one day
  scripts/archive_to_drive.py --dry-run               # what would be done
  scripts/archive_to_drive.py --drop-older-than 14    # also free disk: days older than 14 days
  scripts/archive_to_drive.py --restore live_ticks 2026-09-15
"""

from __future__ import annotations

import argparse
import csv
import gzip
import hashlib
import io
import json
import os
import subprocess
import sys
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Dict, Iterable, List, Optional

REPO = Path(__file__).resolve().parent.parent
STATE = Path(os.environ.get("DESK_STATE_DIR", Path.home() / ".local" / "state" / "algotrading"))
MANIFEST = STATE / "archive-manifest.jsonl"

DB_CONTAINER = os.environ.get("ARCHIVE_DB_CONTAINER", "algotrading_db")
DB_USER = os.environ.get("ARCHIVE_DB_USER", "postgres")
DB_NAME = os.environ.get("ARCHIVE_DB_NAME", "algotrading")

#: rclone remote and folder. A remote made with scope drive.file can see only
#: the files it created, never the rest of the owner's Drive.
REMOTE = os.environ.get("ARCHIVE_REMOTE", "openfno-drive:openfno-archive")

#: What is archived, and the column that places a row in a day.
#: market_ticks is not here: it is a second copy of every live tick (same rows,
#: same columns, written by MarketTickBatchWriterService), and one copy on
#: Drive is enough.
TABLES: Dict[str, str] = {
    "live_ticks": "ReceivedUtc",
    "option_chain_snapshots": "CapturedUtc",
    "live_bars": "BarStartUtc",
}

#: Tables that are TimescaleDB hypertables, freed with drop_chunks; the rest
#: with a DELETE of the day.
HYPERTABLES = {"live_ticks", "market_ticks"}


# ------------------------------------------------------------------- pure --

def closed_days(first: date, now_utc: datetime) -> List[date]:
    """Every day from `first` whose UTC day has ended."""
    last = now_utc.astimezone(timezone.utc).date() - timedelta(days=1)
    days, d = [], first
    while d <= last:
        days.append(d)
        d += timedelta(days=1)
    return days


def remote_path(remote: str, table: str, day: date) -> str:
    """openfno-archive/2026/09/15/live_ticks.csv.gz: a folder per year, month and day, one file per table."""
    return f"{remote.rstrip('/')}/{day:%Y}/{day:%m}/{day:%d}/{table}.csv.gz"


def verified_days(entries: Iterable[dict]) -> Dict[str, set]:
    """table -> the days the manifest records as verified."""
    result: Dict[str, set] = {}
    for e in entries:
        if e.get("verified") is True:
            result.setdefault(e["table"], set()).add(e["day"])
    return result


def droppable_days(candidates: Iterable[date], verified: Dict[str, set], tables: Iterable[str]) -> List[date]:
    """Days every archived table has verified on Drive; anything else is kept."""
    tables = list(tables)
    return [d for d in candidates if all(d.isoformat() in verified.get(t, set()) for t in tables)]


def count_csv_rows(stream: io.BufferedIOBase) -> int:
    """Data rows in a gzipped CSV with a header, reading quoted newlines correctly."""
    with gzip.open(stream, mode="rt", newline="", encoding="utf-8") as text:
        reader = csv.reader(text)
        next(reader, None)
        return sum(1 for _ in reader)


# --------------------------------------------------------------- side effects --

def psql(sql: str) -> str:
    out = subprocess.run(
        ["docker", "exec", DB_CONTAINER, "psql", "-U", DB_USER, "-d", DB_NAME, "-At", "-c", sql],
        capture_output=True, text=True,
    )
    if out.returncode != 0:
        raise RuntimeError(f"psql failed: {out.stderr.strip()}")
    return out.stdout.strip()


def day_bounds(day: date) -> tuple:
    start = datetime(day.year, day.month, day.day, tzinfo=timezone.utc)
    return start.isoformat(), (start + timedelta(days=1)).isoformat()


def read_manifest() -> List[dict]:
    if not MANIFEST.exists():
        return []
    return [json.loads(line) for line in MANIFEST.read_text().splitlines() if line.strip()]


def append_manifest(entry: dict) -> None:
    STATE.mkdir(parents=True, exist_ok=True)
    with MANIFEST.open("a") as f:
        f.write(json.dumps(entry, sort_keys=True) + "\n")


def rclone(*args: str, stdin=None, capture=True) -> subprocess.CompletedProcess:
    return subprocess.run(["rclone", *args], input=stdin, capture_output=capture, text=True)


def say(message: str) -> None:
    print(f"{datetime.now():%H:%M:%S}  {message}", flush=True)


def days_with_rows(table: str, column: str, done: set, since: Optional[str], now_utc: datetime) -> List[date]:
    """
    The closed days on which `table` has rows.

    Asked of each table, not derived from one: the option chain recording began
    days before the oldest tick on the server, and a stray bar stamped
    1980-01-01 in live_bars must not turn the job into forty-six years of
    empty days. After the first run only the days since the newest verified
    one (less three, for safety) are looked at, so the nightly scan stays small.
    """
    if since:
        lower = since
    elif done:
        lower = (max(date.fromisoformat(d) for d in done) - timedelta(days=3)).isoformat()
    else:
        lower = None
    if table in HYPERTABLES:
        where = f"and range_start >= '{lower}'::timestamptz - interval '1 day'" if lower else ""
        sql = (f"select distinct (range_start at time zone 'UTC')::date from timescaledb_information.chunks "
               f"where hypertable_name = '{table}' {where} order by 1")
    else:
        where = f"""where "{column}" >= '{lower}'""" if lower else ""
        sql = f"""select distinct ("{column}" at time zone 'UTC')::date from {table} {where} order by 1"""
    today = now_utc.astimezone(timezone.utc).date()
    return [d for d in (date.fromisoformat(x) for x in psql(sql).split()) if d < today]


def archive_one(table: str, column: str, day: date, dry_run: bool) -> Optional[dict]:
    lo, hi = day_bounds(day)
    where = f'"{column}" >= \'{lo}\' and "{column}" < \'{hi}\''
    rows = int(psql(f'select count(*) from {table} where {where}') or 0)
    target = remote_path(REMOTE, table, day)
    if rows == 0:
        say(f"{table} {day}: no rows, nothing to archive")
        entry = {"table": table, "day": day.isoformat(), "rows": 0, "bytes": 0, "md5": None,
                 "remote": None, "verified": True, "archived_utc": datetime.now(timezone.utc).isoformat()}
        if not dry_run:
            append_manifest(entry)
        return entry
    if dry_run:
        say(f"{table} {day}: would archive {rows:,} rows to {target}")
        return None

    say(f"{table} {day}: archiving {rows:,} rows to {target}")
    copy = subprocess.Popen(
        ["docker", "exec", DB_CONTAINER, "psql", "-U", DB_USER, "-d", DB_NAME, "-c",
         f'COPY (select * from {table} where {where} order by "{column}") TO STDOUT WITH (FORMAT csv, HEADER)'],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    upload = subprocess.Popen(
        ["rclone", "rcat", "--drive-chunk-size", "64M", target],
        stdin=subprocess.PIPE, stderr=subprocess.PIPE,
    )

    md5 = hashlib.md5()
    size = 0

    class Tee(io.RawIOBase):
        def writable(self):
            return True

        def write(self, b):
            nonlocal size
            md5.update(b)
            size += len(b)
            upload.stdin.write(b)
            return len(b)

    buffered = io.BufferedWriter(Tee(), buffer_size=1 << 20)
    # GzipFile does not close or flush the file it writes to: without the
    # explicit flush the gzip trailer would sit in the buffer and never upload.
    with gzip.GzipFile(fileobj=buffered, mode="wb", compresslevel=6) as gz:
        for block in iter(lambda: copy.stdout.read(1 << 20), b""):
            gz.write(block)
    buffered.flush()
    upload.stdin.close()
    copy_err = copy.stderr.read().decode(errors="replace")
    if copy.wait() != 0:
        upload.wait()
        raise RuntimeError(f"{table} {day}: export failed: {copy_err.strip()}")
    up_err = upload.stderr.read().decode(errors="replace")
    if upload.wait() != 0:
        raise RuntimeError(f"{table} {day}: upload failed: {up_err.strip()}")

    # Proof 1: the bytes Drive holds are the bytes we sent.
    remote_md5 = (rclone("md5sum", target).stdout.split() or [""])[0]
    # Proof 2: the file reads back as a CSV with every row.
    reader = subprocess.Popen(["rclone", "cat", target], stdout=subprocess.PIPE)
    read_back = count_csv_rows(reader.stdout)
    reader.wait()

    verified = remote_md5 == md5.hexdigest() and read_back == rows
    entry = {"table": table, "day": day.isoformat(), "rows": rows, "rows_read_back": read_back, "bytes": size,
             "md5": md5.hexdigest(), "remote_md5": remote_md5, "remote": target, "verified": verified,
             "archived_utc": datetime.now(timezone.utc).isoformat()}
    append_manifest(entry)
    say(f"{table} {day}: {size / 1e6:,.1f} MB, md5 {'matches' if remote_md5 == md5.hexdigest() else 'DIFFERS'}, "
        f"{read_back:,}/{rows:,} rows read back -> {'VERIFIED' if verified else 'NOT VERIFIED'}")
    return entry


def drop_local(days: List[date], dry_run: bool) -> None:
    for day in days:
        lo, hi = day_bounds(day)
        for table in list(TABLES) + ["market_ticks"]:
            column = TABLES.get(table, "ReceivedUtc")
            if table in HYPERTABLES:
                # A chunk is one UTC day, so dropping the chunks that end by the
                # day's end removes exactly the days up to it, never a later one.
                sql = f"select count(*) from drop_chunks('{table}', older_than => '{hi}'::timestamptz, newer_than => '{lo}'::timestamptz)"
            else:
                sql = f'delete from {table} where "{column}" >= \'{lo}\' and "{column}" < \'{hi}\''
            if dry_run:
                say(f"would free {table} {day}")
            else:
                psql(sql)
                say(f"freed {table} {day}")


def restore(table: str, day: date) -> None:
    column = TABLES[table]
    lo, hi = day_bounds(day)
    present = int(psql(f'select count(*) from {table} where "{column}" >= \'{lo}\' and "{column}" < \'{hi}\'') or 0)
    if present:
        raise SystemExit(f"{table} already holds {present:,} rows for {day}; refusing to load a second copy.")
    target = remote_path(REMOTE, table, day)
    say(f"restoring {target} into {table}")
    cat = subprocess.Popen(["rclone", "cat", target], stdout=subprocess.PIPE)
    load = subprocess.Popen(
        ["docker", "exec", "-i", DB_CONTAINER, "psql", "-U", DB_USER, "-d", DB_NAME, "-c",
         f"COPY {table} FROM STDIN WITH (FORMAT csv, HEADER)"],
        stdin=subprocess.PIPE,
    )
    with gzip.open(cat.stdout, mode="rb") as gz:
        for block in iter(lambda: gz.read(1 << 20), b""):
            load.stdin.write(block)
    load.stdin.close()
    if load.wait() != 0 or cat.wait() != 0:
        raise SystemExit("restore failed")
    say(f"restored {table} {day}")


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--day", help="archive only this UTC/IST date (YYYY-MM-DD)")
    parser.add_argument("--since", help="first day to consider (default: the oldest tick in the database)")
    parser.add_argument("--drop-older-than", type=int, metavar="DAYS",
                        help="after archiving, free days older than DAYS that are verified on Drive")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--restore", nargs=2, metavar=("TABLE", "DAY"))
    args = parser.parse_args(argv)

    if args.restore:
        restore(args.restore[0], date.fromisoformat(args.restore[1]))
        return 0

    reach = rclone("mkdir", REMOTE)
    if reach.returncode != 0:
        say(f"rclone cannot reach {REMOTE}: {reach.stderr.strip()[:200]} (set it up first: docs/modules/data_archive.md)")
        return 2

    now = datetime.now(timezone.utc)
    if args.day:
        days = [date.fromisoformat(args.day)]
        if days[0] >= now.date():
            say(f"{days[0]} is not over yet in UTC; it can be archived after 05:30 IST tomorrow")
            return 2
    else:
        days = None

    done = verified_days(read_manifest())
    work = []  # (day, table, column), oldest day first
    for table, column in TABLES.items():
        table_days = days if days is not None else days_with_rows(table, column, done.get(table, set()), args.since, now)
        work += [(day, table, column) for day in table_days if day.isoformat() not in done.get(table, set())]
    work.sort()

    failures = 0
    for day, table, column in work:
        try:
            entry = archive_one(table, column, day, args.dry_run)
            if entry is not None and not entry["verified"]:
                failures += 1
        except Exception as ex:  # one failed day must not stop the rest
            failures += 1
            say(f"FAILED {ex}")

    if not args.dry_run and MANIFEST.exists():
        rclone("copyto", str(MANIFEST), f"{REMOTE.rstrip('/')}/manifest.jsonl")

    if args.drop_older_than is not None:
        cutoff = now.date() - timedelta(days=args.drop_older_than)
        candidates = sorted({d for t, c in list(TABLES.items()) + [("market_ticks", "ReceivedUtc")]
                             for d in days_with_rows(t, c, set(), None, now) if d < cutoff})
        verified = verified_days(read_manifest())
        # A table with no rows on a day has nothing to lose there: count it as done.
        for d in candidates:
            lo, hi = day_bounds(d)
            for t, c in TABLES.items():
                if d.isoformat() not in verified.get(t, set()) and \
                        int(psql(f'select count(*) from {t} where "{c}" >= \'{lo}\' and "{c}" < \'{hi}\'') or 0) == 0:
                    verified.setdefault(t, set()).add(d.isoformat())
        keep = droppable_days(candidates, verified, TABLES)
        skipped = sorted(set(candidates) - set(keep))
        if skipped:
            say(f"keeping {len(skipped)} day(s) not verified on Drive: {', '.join(map(str, skipped))}")
        drop_local(keep, args.dry_run)

    say(f"done: {failures} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
