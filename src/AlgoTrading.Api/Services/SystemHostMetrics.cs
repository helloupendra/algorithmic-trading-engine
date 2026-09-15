using System.Globalization;
using System.Text.Json;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Parsers for the Linux /proc files the System page reads. Pure: text in,
/// numbers out, null for anything that is not what Linux writes.
/// </summary>
/// <remarks>
/// Kept apart from the reading so they can be tested with the text a real
/// server produced. A parser that guesses would put an invented number on the
/// one screen that exists to say how the machine is doing; null is the honest
/// answer, and the page says "not available on this host" for it.
/// </remarks>
public static class ProcText
{
    public sealed record MemorySnapshot(
        long TotalBytes,
        long AvailableBytes,
        long SwapTotalBytes,
        long SwapFreeBytes)
    {
        public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);
        public double UsedPercent => TotalBytes > 0 ? 100.0 * UsedBytes / TotalBytes : 0;
        public long SwapUsedBytes => Math.Max(0, SwapTotalBytes - SwapFreeBytes);
        public double? SwapUsedPercent => SwapTotalBytes > 0 ? 100.0 * SwapUsedBytes / SwapTotalBytes : null;
    }

    /// <summary>
    /// /proc/meminfo. "Used" is total minus MemAvailable - the kernel's own
    /// estimate of what a new process could get - not total minus MemFree,
    /// which counts the page cache as used and reads 95% on any healthy box.
    /// </summary>
    public static MemorySnapshot? ParseMemInfo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var kb = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            var parts = raw[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                kb[raw[..colon].Trim()] = value;
            }
        }

        if (!kb.TryGetValue("MemTotal", out var total) || total <= 0) return null;

        // MemAvailable exists since Linux 3.14; the fallback is the old
        // approximation, never MemFree alone.
        long available = kb.TryGetValue("MemAvailable", out var a)
            ? a
            : kb.GetValueOrDefault("MemFree") + kb.GetValueOrDefault("Buffers") + kb.GetValueOrDefault("Cached");

        return new MemorySnapshot(
            total * 1024,
            Math.Min(available, total) * 1024,
            kb.GetValueOrDefault("SwapTotal") * 1024,
            kb.GetValueOrDefault("SwapFree") * 1024);
    }

    /// <summary>Cumulative CPU time from the aggregate "cpu" line of /proc/stat, in jiffies.</summary>
    public readonly record struct CpuTimes(ulong Idle, ulong Total);

    /// <summary>
    /// The first line of /proc/stat: user nice system idle iowait irq softirq
    /// steal (guest and guest_nice are already inside user and nice, so adding
    /// them would count that time twice). iowait counts as idle: a CPU waiting
    /// on the disk is not busy, and the disk shows up in the load average.
    /// </summary>
    public static CpuTimes? ParseCpuTimes(string? procStat)
    {
        if (string.IsNullOrWhiteSpace(procStat)) return null;

        var line = procStat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return null;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).ToArray();
        if (fields.Length < 4) return null;

        var values = new ulong[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            if (!ulong.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return null;
        }

        ulong idle = values[3] + (values.Length > 4 ? values[4] : 0);
        ulong total = 0;
        foreach (var v in values) total += v;
        return new CpuTimes(idle, total);
    }

    /// <summary>Share of the time between two samples the CPUs were busy; null when no time passed.</summary>
    public static double? CpuBusyPercent(CpuTimes earlier, CpuTimes later)
    {
        if (later.Total <= earlier.Total || later.Idle < earlier.Idle) return null;
        double total = later.Total - earlier.Total;
        double idle = later.Idle - earlier.Idle;
        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    public sealed record LoadAverage(double One, double Five, double Fifteen);

    /// <summary>/proc/loadavg: "0.52 0.41 0.38 2/345 12345".</summary>
    public static LoadAverage? ParseLoadAverage(string? text)
    {
        var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length < 3) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var one) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var five) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fifteen))
        {
            return null;
        }
        return new LoadAverage(one, five, fifteen);
    }

    /// <summary>/proc/uptime: "350735.47 234388.90" - seconds since boot, then idle seconds.</summary>
    public static double? ParseUptimeSeconds(string? text)
    {
        var first = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : null;
    }

    /// <summary>
    /// Disk use the way df reports it: used / (used + available). Root's
    /// reserved blocks are in neither, so this is what an ordinary process -
    /// Postgres, the API - actually has left, not a friendlier number.
    /// </summary>
    public static double? DiskUsedPercent(long totalBytes, long totalFreeBytes, long availableBytes)
    {
        long used = totalBytes - totalFreeBytes;
        if (totalBytes <= 0 || used < 0 || used + availableBytes <= 0) return null;
        return 100.0 * used / (used + availableBytes);
    }
}

/// <summary>
/// The arithmetic behind "at this rate the disk fills in N days". Pure, so the
/// estimate can be checked by hand against the numbers it was given.
/// </summary>
public static class StorageGrowth
{
    /// <summary>A stored piece of a table covering [StartUtc, EndUtc) - a TimescaleDB chunk.</summary>
    public sealed record ChunkSpan(DateTime StartUtc, DateTime EndUtc, long Bytes);

    /// <summary>
    /// Bytes per UTC day from chunks. A one-day chunk lands on its day; a
    /// longer one is spread evenly over the days it covers, which is an
    /// approximation and the only one available without scanning rows.
    /// </summary>
    public static Dictionary<DateOnly, long> BytesPerDay(IEnumerable<ChunkSpan> chunks)
    {
        var result = new Dictionary<DateOnly, long>();
        foreach (var chunk in chunks)
        {
            if (chunk.EndUtc <= chunk.StartUtc || chunk.Bytes <= 0) continue;

            double span = (chunk.EndUtc - chunk.StartUtc).TotalSeconds;
            for (var day = chunk.StartUtc.Date; day < chunk.EndUtc; day = day.AddDays(1))
            {
                var from = day < chunk.StartUtc ? chunk.StartUtc : day;
                var to = day.AddDays(1) > chunk.EndUtc ? chunk.EndUtc : day.AddDays(1);
                long share = (long)Math.Round(chunk.Bytes * (to - from).TotalSeconds / span);
                var key = DateOnly.FromDateTime(day);
                result[key] = result.GetValueOrDefault(key) + share;
            }
        }
        return result;
    }

    public sealed record DailyEstimate(long BytesPerDay, IReadOnlyList<DateOnly> Days);

    /// <summary>
    /// Average bytes written per recording day: the most recent
    /// <paramref name="sampleDays"/> closed UTC days inside the lookback that
    /// wrote at least <paramref name="minBytesPerDay"/>.
    /// </summary>
    /// <remarks>
    /// Weekends and holidays are skipped rather than averaged in: the server
    /// writes on trading days, and averaging a Saturday's zero into Monday's
    /// 6 GB would halve the rate on exactly the morning it matters. The result
    /// is therefore per trading day, and the page says so. Null when no day in
    /// the window wrote anything - no estimate, rather than "never fills".
    /// </remarks>
    public static DailyEstimate? EstimateDaily(
        IReadOnlyDictionary<DateOnly, long> bytesByDay,
        DateOnly todayUtc,
        int sampleDays = 3,
        int lookbackDays = 10,
        long minBytesPerDay = 1024 * 1024)
    {
        var days = new List<DateOnly>();
        for (int back = 1; back <= lookbackDays && days.Count < sampleDays; back++)
        {
            var day = todayUtc.AddDays(-back);
            if (bytesByDay.TryGetValue(day, out var bytes) && bytes >= minBytesPerDay) days.Add(day);
        }

        if (days.Count == 0) return null;
        long sum = 0;
        foreach (var d in days) sum += bytesByDay[d];
        return new DailyEstimate(sum / days.Count, days);
    }

    /// <summary>Free space divided by the daily rate; null when either is unknown or the rate is zero.</summary>
    public static double? DaysUntilFull(long? freeBytes, long? bytesPerDay)
    {
        if (freeBytes is null || bytesPerDay is null || bytesPerDay <= 0 || freeBytes < 0) return null;
        return (double)freeBytes.Value / bytesPerDay.Value;
    }

    /// <summary>
    /// The smallest id whose timestamp is at or after <paramref name="boundaryUtc"/>,
    /// by binary search over the primary key; <paramref name="maxId"/> + 1 when none is.
    /// </summary>
    /// <remarks>
    /// For a plain (non-hypertable) table with no index on its time column:
    /// counting a day's rows would read the whole table, while ids that grow
    /// with time turn it into ~log2(n) single-row index lookups. The probe
    /// returns the first row with an id at or above the one asked for, so gaps
    /// left by deleted days do not break the search. It relies on ids and
    /// timestamps rising together - true for rows stamped with the clock at
    /// insert, which is why callers must not use it for backfilled tables.
    /// </remarks>
    public static async Task<long> FirstIdAtOrAfterAsync(
        long minId,
        long maxId,
        DateTime boundaryUtc,
        Func<long, Task<(long Id, DateTime AtUtc)?>> firstRowFrom)
    {
        long lo = minId, hi = maxId + 1, best = maxId + 1;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            var row = await firstRowFrom(mid);
            if (row is null)
            {
                hi = mid;
            }
            else if (row.Value.AtUtc >= boundaryUtc)
            {
                // No row lies between mid and this one, so the answer is this
                // row or something below mid.
                best = Math.Min(best, row.Value.Id);
                hi = mid;
            }
            else
            {
                lo = row.Value.Id + 1;
            }
        }
        return best;
    }
}

/// <summary>
/// Reads the manifest scripts/archive_to_drive.py appends to: one JSON line
/// per (table, day) attempt.
/// </summary>
public static class ArchiveManifest
{
    public sealed record TableArchive(
        string Table,
        string? LastVerifiedDay,
        int VerifiedDays,
        int UnverifiedDays,
        DateTime? LastArchivedUtc);

    public sealed record Summary(
        IReadOnlyList<TableArchive> Tables,
        int UnverifiedDays,
        int UnreadableLines);

    /// <summary>
    /// Per table: the newest day proven on Drive, and the days whose every
    /// attempt failed verification.
    /// </summary>
    /// <remarks>
    /// A day that failed once and was verified on a retry is not outstanding,
    /// so "unverified" counts days, not lines - a manifest that records its
    /// own retries honestly must not read as a growing failure. A line that
    /// is not JSON (a write cut short) is counted and skipped, never guessed at.
    /// </remarks>
    public static Summary Summarize(IEnumerable<string> lines)
    {
        var verified = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var attempted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var lastArchived = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        int unreadable = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("table", out var t) || t.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("day", out var d) || d.ValueKind != JsonValueKind.String)
                {
                    unreadable++;
                    continue;
                }

                string table = t.GetString()!;
                string day = d.GetString()!;
                attempted.TryAdd(table, new HashSet<string>(StringComparer.Ordinal));
                attempted[table].Add(day);

                if (root.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True)
                {
                    verified.TryAdd(table, new SortedSet<string>(StringComparer.Ordinal));
                    verified[table].Add(day);
                }

                if (root.TryGetProperty("archived_utc", out var at) && at.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                {
                    if (!lastArchived.TryGetValue(table, out var prev) || when > prev) lastArchived[table] = when;
                }
            }
            catch (JsonException)
            {
                unreadable++;
            }
        }

        var tables = attempted.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(table =>
            {
                var ok = verified.GetValueOrDefault(table);
                int unverified = attempted[table].Count(day => ok is null || !ok.Contains(day));
                return new TableArchive(
                    table,
                    ok?.Max,
                    ok?.Count ?? 0,
                    unverified,
                    lastArchived.TryGetValue(table, out var w) ? w : null);
            })
            .ToList();

        return new Summary(tables, tables.Sum(x => x.UnverifiedDays), unreadable);
    }
}

/// <summary>TimescaleDB policy config, as timescaledb_information.jobs stores it.</summary>
public static class TimescalePolicy
{
    /// <summary>
    /// The interval under <paramref name="key"/> ("compress_after", "drop_after")
    /// as written - "7 days" - or a number for integer-time hypertables.
    /// </summary>
    public static string? ReadInterval(string? configJson, string key)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty(key, out var value))
            {
                return null;
            }
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
