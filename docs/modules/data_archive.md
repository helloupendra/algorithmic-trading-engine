# Data archive (Google Drive)

The server keeps recent market data in its database; every finished trading
day is also copied to Google Drive, verified, so the database can later let
old days go without losing them.

## Why

On 2026-09-15, the first full day of Dhan's feed and option chain, the server's
40 GB disk went from 16 GB free to 8.5 GB. Three things added up:
- every tick was stored twice, in `live_ticks` and `market_ticks`;
- each tick carries its five-level depth;
- `market_ticks` was compressed only after seven days.

The owner's rule is that no data is thrown away. Drive is the cold store that
makes that possible on a small disk.

## What was done to the database

| Change | Effect |
|---|---|
| `live_ticks` and `market_ticks` compress chunks 2 hours after their UTC day ends (was 2 days / 7 days) | Each day's ticks are compressed by about 07:30 IST the next morning. On 2026-09-15 compressing the closed days took the disk from 9.3 GB to 14 GB free, with no data removed. |

Compressed chunks are still queried the same way, and can be decompressed with
`decompress_chunk`.

## What is archived

| Table | Day column | Notes |
|---|---|---|
| `live_ticks` | `ReceivedUtc` | Every tick, with its raw payload |
| `option_chain_snapshots` | `CapturedUtc` | The per-minute chain: OI, IV, greeks. It cannot be fetched again from any vendor. |
| `live_bars` | `BarStartUtc` | 1-minute bars |

`market_ticks` is not archived: it is a second copy of the same ticks (same
rows, same columns). Whether to keep writing it is an open decision.

A **day** is the UTC date, which is also the IST trading date: NSE
(03:45–10:00 UTC) and MCX (03:30–18:25 UTC) both fall inside one UTC day. A day
is archived only after it has ended in UTC, 05:30 IST the next morning.

Files on Drive:

```
openfno-archive/
  manifest.jsonl
  2026/
    09/
      15/
        live_ticks.csv.gz
        option_chain_snapshots.csv.gz
        live_bars.csv.gz
```

One folder per year, month and day (the owner's layout), one file per table.

Each file is gzipped CSV with a header row: the table's own columns, readable
by any tool.

## How a day is proved

`scripts/archive_to_drive.py`, for every table and day not yet in the manifest:
1. Counts the day's rows in the database.
2. Streams them through gzip straight to Drive (`rclone rcat`). Nothing is
   written to the server's disk on the way, and the bytes are hashed as they go.
3. Checks that Drive's MD5 of the file equals the hash of what was sent.
4. Reads the file back from Drive and counts its rows, quoted newlines
   included, against the database count.
5. Records the result (rows, bytes, MD5, verified) in the manifest, on the
   server and on Drive.

A day already verified is skipped, so the job can run any number of times.

Tested on 2026-09-15 against a local rclone remote:
- 168,327 ticks became an 8.0 MB file.
- The MD5 matched and every row read back.
- Restored into a scratch database, the rows' content hash was identical to
  the source.
- A second restore of the same day was refused.

## Freeing the disk

Nothing is deleted unless `--drop-older-than N` is given (or
`ARCHIVE_DROP_OLDER_THAN_DAYS=N` in `.env` for the nightly run). Even then:
- only days older than N are considered;
- a day is freed only when **every** archived table is verified on Drive for
  it; any other day is kept and named in the log;
- freeing a day drops its chunks from `live_ticks` and `market_ticks`, and
  deletes its rows from `option_chain_snapshots` and `live_bars`.

Candles (`candles`) are never freed: they are small and backtests read them.

`live_ticks` also has a 90-day retention policy that drops ticks whether or not
they were archived. With the nightly archive running, every day is on Drive
long before then.

## Restoring a day

```sh
scripts/archive_to_drive.py --restore live_ticks 2026-09-15
```

Loads the file back into the table. It refuses when the table already holds
rows for that day, so a day is never loaded twice.

## Setting it up (once)

1. **On the Mac,** create the Drive remote. A browser opens; sign in with the
   Google account whose Drive should hold the archive:

   ```sh
   brew install rclone
   rclone config create openfno-drive drive scope=drive.file
   ```

   This prints the new token in the terminal. Do not paste that output
   anywhere; if it has been shared, remove "rclone" at
   https://myaccount.google.com/permissions and run
   `rclone config reconnect openfno-drive:` for a fresh one.

   `scope=drive.file` lets the server see only the files it creates, never the
   rest of that Drive.
2. **Copy the remote to the server.** The file holds the Drive token, so it is
   copied and never printed:

   ```sh
   ssh -i <key> ubuntu@<server> 'mkdir -p ~/.config/rclone && chmod 700 ~/.config/rclone'
   scp -i <key> ~/.config/rclone/rclone.conf ubuntu@<server>:.config/rclone/rclone.conf
   ssh -i <key> ubuntu@<server> 'chmod 600 ~/.config/rclone/rclone.conf && rclone lsd openfno-drive:'
   ```
3. **First run by hand,** on the server:

   ```sh
   scripts/archive_to_drive.py --dry-run
   scripts/archive-to-drive.sh
   ```
4. **Nightly at 06:00 IST** (`crontab -e` on the server):

   ```
   0 6 * * * /home/ubuntu/algorithmic-trading-engine/scripts/archive-to-drive.sh
   ```

The nightly run writes `logs/archive-YYYY-MM-DD.log` and sends a Telegram
message when it copies files or fails.

## Code map

| File | Role |
|---|---|
| `scripts/archive_to_drive.py` | Archive, verify, manifest, free, restore |
| `scripts/archive-to-drive.sh` | Nightly wrapper: log, Telegram, optional freeing |
| `src/AlgoTrading.PythonEngine/tests/test_archive_to_drive.py` | Closed days, freeing only verified days, CSV row count with quoted newlines |
