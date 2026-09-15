using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// How the machine the API runs on is doing: disk, memory, CPU, the database
/// on it, how fast that database grows, and whether old days reached Drive.
/// </summary>
/// <remarks>
/// On 2026-09-15 the server's disk went from 16 GB free to 8.5 GB in one day
/// (Dhan ticks stored twice, plus the option chain) and the owner found out
/// only by asking. This is the answer to "how would I have known".
/// <para>
/// Everything here is either a cheap file read (/proc, the manifest) or a
/// catalog query, and the whole report is cached for a few seconds, so a page
/// polling it costs almost nothing. The one read that touches table data - the
/// option chain's day boundaries - walks the primary key a few dozen times
/// and is cached for minutes, since a day's size does not change once the day
/// is over. Every section fails on its own: a database that is down still
/// leaves the disk and memory readable, which is when they matter most.
/// </para>
/// </remarks>
public sealed class SystemHostService
{
    public static readonly TimeSpan ReportCacheFor = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan GrowthCacheFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MetadataBudget = TimeSpan.FromMilliseconds(500);
    private const int CommandTimeoutSeconds = 5;

    /// <summary>The tables that grow by the day and so decide when the disk fills.</summary>
    private static readonly string[] GrowthTables = ["live_ticks", "market_ticks", "option_chain_snapshots"];

    /// <summary>Time column of the growth tables that are plain tables, for the id search.</summary>
    private static readonly Dictionary<string, string> PlainTimeColumn = new(StringComparer.Ordinal)
    {
        ["option_chain_snapshots"] = "CapturedUtc",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SystemHostService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SystemHostReport? _report;
    private DateTime _reportAtUtc;

    private GrowthReport? _growth;
    private DateTime _growthAtUtc;

    private ProcText.CpuTimes? _lastCpu;
    private DateTime _lastCpuAtUtc;

    private Task<Ec2Identity?>? _ec2;
    private string? _lastWarning;

    public SystemHostService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<SystemHostService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SystemHostReport> GetAsync(CancellationToken cancellationToken)
    {
        // A caller that goes away must not cancel the build other callers are
        // waiting on, so the wait honours the token and the work does not.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_report is not null && DateTime.UtcNow - _reportAtUtc < ReportCacheFor) return _report;

            _report = await BuildAsync();
            _reportAtUtc = DateTime.UtcNow;
            return _report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SystemHostReport> BuildAsync()
    {
        var ec2 = _ec2 ??= ReadEc2IdentityAsync();
        var host = await ReadHostAsync();
        var database = await ReadDatabaseAsync();
        var growth = await ReadGrowthAsync();

        double? daysUntilFull = StorageGrowth.DaysUntilFull(host.Disk?.FreeBytes, growth?.BytesPerDay);

        return new SystemHostReport(
            GeneratedUtc: DateTime.UtcNow,
            CacheSeconds: (int)ReportCacheFor.TotalSeconds,
            Host: host,
            Ec2: await ec2,
            Api: ReadApiProcess(),
            Database: database,
            Growth: growth is null ? null : growth with { DaysUntilFull = daysUntilFull },
            Archive: ReadArchive());
    }

    // ------------------------------------------------------------------ host

    private async Task<HostReport> ReadHostAsync()
    {
        bool proc = Directory.Exists("/proc") && File.Exists("/proc/meminfo");

        DiskReport? disk = null;
        try
        {
            var drive = new DriveInfo("/");
            if (drive.IsReady)
            {
                long used = drive.TotalSize - drive.TotalFreeSpace;
                disk = new DiskReport(
                    "/",
                    drive.TotalSize,
                    used,
                    drive.AvailableFreeSpace,
                    ProcText.DiskUsedPercent(drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace) is { } pct
                        ? Math.Round(pct, 1)
                        : null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Warn("Could not read the root filesystem's size.", ex);
        }

        var memory = proc ? ProcText.ParseMemInfo(TryRead("/proc/meminfo")) : null;
        var load = proc ? ProcText.ParseLoadAverage(TryRead("/proc/loadavg")) : null;
        var uptime = proc ? ProcText.ParseUptimeSeconds(TryRead("/proc/uptime")) : null;
        var cpu = proc ? await ReadCpuAsync() : null;

        return new HostReport(
            MachineName: Environment.MachineName,
            Os: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            ProcAvailable: proc,
            Disk: disk,
            Memory: memory is null
                ? null
                : new MemoryReport(memory.TotalBytes, memory.AvailableBytes, memory.UsedBytes, Math.Round(memory.UsedPercent, 1)),
            Swap: memory is null
                ? null
                : new SwapReport(
                    memory.SwapTotalBytes,
                    memory.SwapUsedBytes,
                    memory.SwapUsedPercent is { } s ? Math.Round(s, 1) : null),
            Cpu: new CpuReport(Environment.ProcessorCount, cpu?.Percent, cpu?.Seconds),
            Load: load,
            UptimeSeconds: uptime is { } u ? (long)u : null);
    }

    /// <summary>
    /// Busy share since the previous report when that was recent, otherwise a
    /// 200 ms sample. The previous reading is the better number when there is
    /// one: it covers the whole polling interval rather than one instant.
    /// </summary>
    private async Task<(double Percent, double Seconds)?> ReadCpuAsync()
    {
        var now = ProcText.ParseCpuTimes(TryRead("/proc/stat"));
        var nowAt = DateTime.UtcNow;
        if (now is null) return null;

        var earlier = _lastCpu;
        var earlierAt = _lastCpuAtUtc;
        if (earlier is null || nowAt - earlierAt > TimeSpan.FromMinutes(2) || nowAt - earlierAt < TimeSpan.FromSeconds(1))
        {
            earlier = now;
            earlierAt = nowAt;
            await Task.Delay(200);
            now = ProcText.ParseCpuTimes(TryRead("/proc/stat"));
            nowAt = DateTime.UtcNow;
            if (now is null) return null;
        }

        _lastCpu = now;
        _lastCpuAtUtc = nowAt;

        var percent = ProcText.CpuBusyPercent(earlier.Value, now.Value);
        return percent is null ? null : (Math.Round(percent.Value, 1), Math.Round((nowAt - earlierAt).TotalSeconds, 1));
    }

    private static ApiProcessReport ReadApiProcess()
    {
        using var process = Process.GetCurrentProcess();
        return new ApiProcessReport(
            StartedUtc: BackendController.StartedUtc,
            UptimeSeconds: (long)(DateTime.UtcNow - BackendController.StartedUtc).TotalSeconds,
            WorkingSetBytes: process.WorkingSet64,
            ManagedHeapBytes: GC.GetTotalMemory(false),
            Threads: process.Threads.Count,
            Version: typeof(BackendController).Assembly.GetName().Version?.ToString());
    }

    // ------------------------------------------------------------------- EC2

    /// <summary>
    /// Instance id, type and zone from the EC2 instance metadata service
    /// (IMDSv2: a session token first, then the identity document).
    /// </summary>
    /// <remarks>
    /// Asked once per process: an instance does not change type under a
    /// running process. Off EC2 the link-local address simply does not answer,
    /// so the budget is short and the answer is null - which the page states
    /// as "not an EC2 host" rather than an error.
    /// </remarks>
    private async Task<Ec2Identity?> ReadEc2IdentityAsync()
    {
        using var budget = new CancellationTokenSource(MetadataBudget);
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(SystemHostService));

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token");
            tokenRequest.Headers.Add("X-aws-ec2-metadata-token-ttl-seconds", "300");
            using var tokenResponse = await client.SendAsync(tokenRequest, budget.Token);
            if (!tokenResponse.IsSuccessStatusCode) return null;
            var token = await tokenResponse.Content.ReadAsStringAsync(budget.Token);

            using var docRequest = new HttpRequestMessage(
                HttpMethod.Get, "http://169.254.169.254/latest/dynamic/instance-identity/document");
            docRequest.Headers.Add("X-aws-ec2-metadata-token", token);
            using var docResponse = await client.SendAsync(docRequest, budget.Token);
            if (!docResponse.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await docResponse.Content.ReadAsStringAsync(budget.Token));
            string? Read(string name) =>
                doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            return new Ec2Identity(Read("instanceId"), Read("instanceType"), Read("availabilityZone"), Read("region"));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "No EC2 instance metadata within {Budget} ms; not reporting an instance.", MetadataBudget.TotalMilliseconds);
            return null;
        }
    }

    // -------------------------------------------------------------- database

    private async Task<DatabaseReport> ReadDatabaseAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            try
            {
                long? size = await ScalarAsync<long?>(connection, "select pg_database_size(current_database())");
                bool timescale = await ScalarAsync<bool>(connection,
                    "select exists(select 1 from pg_extension where extname = 'timescaledb')");

                var compression = new Dictionary<string, CompressionReport>(StringComparer.Ordinal);
                var tables = new List<TableSizeReport>();
                var policies = new List<RetentionPolicyReport>();

                if (timescale)
                {
                    await ReadRowsAsync(connection, """
                        select h.hypertable_name::text, s.total_chunks, s.number_compressed_chunks,
                               s.before_compression_total_bytes, s.after_compression_total_bytes
                        from timescaledb_information.hypertables h
                        cross join lateral hypertable_compression_stats(
                            format('%I.%I', h.hypertable_schema, h.hypertable_name)::regclass) s
                        where h.compression_enabled
                        """,
                        r => compression[r.GetString(0)] = new CompressionReport(
                            NullableLong(r, 1) ?? 0,
                            NullableLong(r, 2) ?? 0,
                            NullableLong(r, 3),
                            NullableLong(r, 4)));

                    await ReadRowsAsync(connection, """
                        select h.hypertable_name::text, h.compression_enabled,
                               cj.config::text, cs.last_run_status, cs.last_successful_finish, cs.total_failures,
                               rj.config::text, rs.last_run_status, rs.last_successful_finish, rs.total_failures
                        from timescaledb_information.hypertables h
                        left join timescaledb_information.jobs cj
                               on cj.hypertable_schema = h.hypertable_schema and cj.hypertable_name = h.hypertable_name
                              and cj.proc_name = 'policy_compression'
                        left join timescaledb_information.job_stats cs on cs.job_id = cj.job_id
                        left join timescaledb_information.jobs rj
                               on rj.hypertable_schema = h.hypertable_schema and rj.hypertable_name = h.hypertable_name
                              and rj.proc_name = 'policy_retention'
                        left join timescaledb_information.job_stats rs on rs.job_id = rj.job_id
                        order by h.hypertable_name
                        """,
                        r => policies.Add(new RetentionPolicyReport(
                            Table: r.GetString(0),
                            CompressionEnabled: r.GetBoolean(1),
                            CompressAfter: TimescalePolicy.ReadInterval(NullableString(r, 2), "compress_after"),
                            CompressionJob: JobReport(r, 2, 3, 4, 5),
                            DropAfter: TimescalePolicy.ReadInterval(NullableString(r, 6), "drop_after"),
                            RetentionJob: JobReport(r, 6, 7, 8, 9))));
                }

                string hyperPart = timescale
                    ? """
                      select h.hypertable_name::text as name,
                             hypertable_size(format('%I.%I', h.hypertable_schema, h.hypertable_name)::regclass) as bytes,
                             true as is_hyper
                      from timescaledb_information.hypertables h
                      union all
                      """
                    : "";
                string notHyper = timescale
                    ? """
                      and not exists (select 1 from timescaledb_information.hypertables h
                                      where h.hypertable_schema = n.nspname and h.hypertable_name = c.relname)
                      """
                    : "";

                await ReadRowsAsync(connection, $"""
                    select name, bytes, is_hyper from (
                        {hyperPart}
                        select c.relname::text, pg_total_relation_size(c.oid), false
                        from pg_class c join pg_namespace n on n.oid = c.relnamespace
                        where n.nspname = 'public' and c.relkind in ('r', 'p', 'm')
                        {notHyper}
                    ) t
                    order by bytes desc nulls last
                    limit 8
                    """,
                    r =>
                    {
                        string name = r.GetString(0);
                        tables.Add(new TableSizeReport(
                            name,
                            NullableLong(r, 1) ?? 0,
                            r.GetBoolean(2),
                            compression.GetValueOrDefault(name)));
                    });

                return new DatabaseReport(size, timescale, tables, policies, null);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            Warn("Could not read the database's size.", ex);
            return new DatabaseReport(null, false, [], [], FirstLine(ex.Message));
        }
    }

    private static JobRunReport? JobReport(DbDataReader r, int configColumn, int status, int success, int failures) =>
        r.IsDBNull(configColumn)
            ? null
            : new JobRunReport(
                NullableString(r, status),
                r.IsDBNull(success) ? null : r.GetFieldValue<DateTime>(success).ToUniversalTime(),
                NullableLong(r, failures));

    // ---------------------------------------------------------------- growth

    private async Task<GrowthReport?> ReadGrowthAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_growth is not null && DateTime.UtcNow - _growthAtUtc < GrowthCacheFor &&
            _growth.ComputedForUtcDay == today)
        {
            return _growth;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            try
            {
                const int lookbackDays = 10;
                var windowEnd = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var windowStart = windowEnd.AddDays(-lookbackDays);

                bool timescale = await ScalarAsync<bool>(connection,
                    "select exists(select 1 from pg_extension where extname = 'timescaledb')");
                var hypertables = new HashSet<string>(StringComparer.Ordinal);
                if (timescale)
                {
                    await ReadRowsAsync(connection,
                        "select hypertable_name::text from timescaledb_information.hypertables where hypertable_schema = 'public'",
                        r => hypertables.Add(r.GetString(0)));
                }

                var perTable = new Dictionary<string, Dictionary<DateOnly, long>>(StringComparer.Ordinal);
                foreach (var table in GrowthTables)
                {
                    if (hypertables.Contains(table))
                    {
                        perTable[table] = await HypertableBytesByDayAsync(connection, table, windowStart, windowEnd);
                    }
                    else if (PlainTimeColumn.TryGetValue(table, out var column))
                    {
                        var byDay = await PlainTableBytesByDayAsync(connection, table, column, windowStart, lookbackDays);
                        if (byDay is not null) perTable[table] = byDay;
                    }
                }

                var totals = new Dictionary<DateOnly, long>();
                foreach (var byDay in perTable.Values)
                {
                    foreach (var (day, bytes) in byDay) totals[day] = totals.GetValueOrDefault(day) + bytes;
                }

                var estimate = StorageGrowth.EstimateDaily(totals, today, lookbackDays: lookbackDays);

                _growth = new GrowthReport(
                    ComputedForUtcDay: today,
                    BytesPerDay: estimate?.BytesPerDay,
                    DaysUntilFull: null,
                    SampleDays: estimate?.Days ?? [],
                    Days: totals.OrderBy(x => x.Key).Select(x => new DayBytes(x.Key, x.Value)).ToList(),
                    Tables: perTable
                        .Select(kv => new TableGrowth(
                            kv.Key,
                            estimate is null || estimate.Days.Count == 0
                                ? null
                                : estimate.Days.Sum(d => kv.Value.GetValueOrDefault(d)) / estimate.Days.Count,
                            hypertables.Contains(kv.Key) ? "chunk sizes" : "row count × average row size"))
                        .OrderByDescending(x => x.BytesPerDay ?? 0)
                        .ToList(),
                    Basis: "Estimate. The average of the last 3 closed UTC days that recorded data, within the last "
                         + $"{lookbackDays}; tick hypertables by their day-chunks as stored now (a chunk not yet compressed "
                         + "counts at full size, so this errs high), the option chain by rows × average row size. "
                         + "Per trading day: weekends and holidays are skipped.",
                    Error: null);
                _growthAtUtc = DateTime.UtcNow;
                return _growth;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            Warn("Could not estimate the database's daily growth.", ex);
            return new GrowthReport(today, null, null, [], [], [], "", FirstLine(ex.Message));
        }
    }

    private static async Task<Dictionary<DateOnly, long>> HypertableBytesByDayAsync(
        DbConnection connection, string table, DateTime fromUtc, DateTime toUtc)
    {
        // Catalog only: chunk boundaries and each chunk's size on disk.
        var chunks = new List<StorageGrowth.ChunkSpan>();
        await ReadRowsAsync(connection, """
            select c.range_start, c.range_end, s.total_bytes
            from timescaledb_information.chunks c
            join chunks_detailed_size(format('%I.%I', 'public', @table)::regclass) s
              on s.chunk_schema = c.chunk_schema and s.chunk_name = c.chunk_name
            where c.hypertable_schema = 'public' and c.hypertable_name = @table
              and c.range_start is not null
              and c.range_end <= @to and c.range_end > @from
            """,
            r => chunks.Add(new StorageGrowth.ChunkSpan(
                r.GetFieldValue<DateTime>(0).ToUniversalTime(),
                r.GetFieldValue<DateTime>(1).ToUniversalTime(),
                NullableLong(r, 2) ?? 0)),
            ("table", table), ("from", fromUtc), ("to", toUtc));

        return StorageGrowth.BytesPerDay(chunks)
            .Where(kv => kv.Key >= DateOnly.FromDateTime(fromUtc))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Bytes per day for a plain table whose ids rise with its timestamp: the
    /// id at each day boundary, found by binary search on the primary key.
    /// </summary>
    private static async Task<Dictionary<DateOnly, long>?> PlainTableBytesByDayAsync(
        DbConnection connection, string table, string timeColumn, DateTime windowStart, int days)
    {
        if (!await ScalarAsync<bool>(connection, $"select to_regclass('public.{table}') is not null")) return null;

        long totalBytes = 0, estimatedRows = 0, minId = 0, maxId = -1;
        await ReadRowsAsync(connection, $"""
            select pg_total_relation_size(c.oid), greatest(c.reltuples, 0)::bigint,
                   (select min("Id") from {table}), (select max("Id") from {table})
            from pg_class c where c.oid = 'public.{table}'::regclass
            """,
            r =>
            {
                totalBytes = NullableLong(r, 0) ?? 0;
                estimatedRows = NullableLong(r, 1) ?? 0;
                minId = NullableLong(r, 2) ?? 0;
                maxId = NullableLong(r, 3) ?? -1;
            });
        if (maxId < minId) return [];

        // reltuples is the planner's count, current to the last analyze; a
        // never-analyzed table falls back to the id span.
        long rows = estimatedRows > 0 ? estimatedRows : maxId - minId + 1;
        double bytesPerRow = rows > 0 ? (double)totalBytes / rows : 0;

        async Task<(long Id, DateTime AtUtc)?> FirstRowFrom(long id)
        {
            (long, DateTime)? found = null;
            await ReadRowsAsync(connection,
                $"""select "Id", "{timeColumn}" from {table} where "Id" >= @id order by "Id" limit 1""",
                r => found = (r.GetInt64(0), r.GetFieldValue<DateTime>(1).ToUniversalTime()),
                ("id", id));
            return found;
        }

        // Newest boundary first, each search capped by the one after it.
        var boundaryIds = new long[days + 1];
        long upper = maxId;
        for (int i = days; i >= 0; i--)
        {
            boundaryIds[i] = await StorageGrowth.FirstIdAtOrAfterAsync(minId, upper, windowStart.AddDays(i), FirstRowFrom);
            upper = Math.Min(maxId, boundaryIds[i]);
        }

        var result = new Dictionary<DateOnly, long>();
        for (int i = 0; i < days; i++)
        {
            long dayRows = Math.Max(0, boundaryIds[i + 1] - boundaryIds[i]);
            if (dayRows > 0) result[DateOnly.FromDateTime(windowStart.AddDays(i))] = (long)(dayRows * bytesPerRow);
        }
        return result;
    }

    // --------------------------------------------------------------- archive

    /// <summary>
    /// The Drive archive's manifest, where scripts/archive_to_drive.py writes it:
    /// DESK_STATE_DIR when set, otherwise the XDG state directory the desk
    /// scripts use (and, on a Mac, their Application Support directory).
    /// </summary>
    private ArchiveReport? ReadArchive()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DESK_STATE_DIR"),
            Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg ? Path.Combine(xdg, "algotrading") : null,
            Path.Combine(home, ".local", "state", "algotrading"),
            OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "algotrading") : null,
        };

        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var path = Path.Combine(dir, "archive-manifest.jsonl");
            if (!File.Exists(path)) continue;

            try
            {
                // Shared read: the archive script may be appending right now.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line) lines.Add(line);

                var summary = ArchiveManifest.Summarize(lines);
                return new ArchiveReport(summary.Tables, summary.UnverifiedDays, summary.UnreadableLines, File.GetLastWriteTimeUtc(path));
            }
            catch (IOException ex)
            {
                Warn("Could not read the archive manifest.", ex);
                return null;
            }
        }
        return null;
    }

    // --------------------------------------------------------------- helpers

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }
        return command;
    }

    private static async Task<T> ScalarAsync<T>(DbConnection connection, string sql)
    {
        await using var command = Command(connection, sql);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull) return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ReadRowsAsync(
        DbConnection connection, string sql, Action<DbDataReader> onRow, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) onRow(reader);
    }

    private static long? NullableLong(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    private static string? NullableString(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
    private static string FirstLine(string message) => message.Split('\n')[0].Trim();

    /// <summary>Logs a failure once per distinct message, not once per poll.</summary>
    private void Warn(string what, Exception ex)
    {
        var key = what + ex.Message;
        if (key == _lastWarning) return;
        _lastWarning = key;
        _logger.LogWarning(ex, "{What}", what);
    }
}

// ---------------------------------------------------------------- the report

public sealed record SystemHostReport(
    DateTime GeneratedUtc,
    int CacheSeconds,
    HostReport Host,
    Ec2Identity? Ec2,
    ApiProcessReport Api,
    DatabaseReport Database,
    GrowthReport? Growth,
    ArchiveReport? Archive);

public sealed record HostReport(
    string MachineName,
    string Os,
    string Architecture,
    bool ProcAvailable,
    DiskReport? Disk,
    MemoryReport? Memory,
    SwapReport? Swap,
    CpuReport Cpu,
    ProcText.LoadAverage? Load,
    long? UptimeSeconds);

public sealed record DiskReport(string Path, long TotalBytes, long UsedBytes, long FreeBytes, double? UsedPercent);
public sealed record MemoryReport(long TotalBytes, long AvailableBytes, long UsedBytes, double UsedPercent);
public sealed record SwapReport(long TotalBytes, long UsedBytes, double? UsedPercent);
public sealed record CpuReport(int Cores, double? UsedPercent, double? SampleSeconds);
public sealed record Ec2Identity(string? InstanceId, string? InstanceType, string? AvailabilityZone, string? Region);

public sealed record ApiProcessReport(
    DateTime StartedUtc,
    long UptimeSeconds,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    int Threads,
    string? Version);

public sealed record DatabaseReport(
    long? SizeBytes,
    bool TimescaleDb,
    IReadOnlyList<TableSizeReport> Tables,
    IReadOnlyList<RetentionPolicyReport> Policies,
    string? Error);

public sealed record TableSizeReport(string Name, long Bytes, bool IsHypertable, CompressionReport? Compression);

/// <summary>Before/after cover only the chunks already compressed, as TimescaleDB reports them.</summary>
public sealed record CompressionReport(long TotalChunks, long CompressedChunks, long? BeforeBytes, long? AfterBytes);

public sealed record RetentionPolicyReport(
    string Table,
    bool CompressionEnabled,
    string? CompressAfter,
    JobRunReport? CompressionJob,
    string? DropAfter,
    JobRunReport? RetentionJob);

public sealed record JobRunReport(string? LastRunStatus, DateTime? LastSuccessUtc, long? TotalFailures);

public sealed record GrowthReport(
    DateOnly ComputedForUtcDay,
    long? BytesPerDay,
    double? DaysUntilFull,
    IReadOnlyList<DateOnly> SampleDays,
    IReadOnlyList<DayBytes> Days,
    IReadOnlyList<TableGrowth> Tables,
    string Basis,
    string? Error);

public sealed record DayBytes(DateOnly Day, long Bytes);
public sealed record TableGrowth(string Table, long? BytesPerDay, string Method);

public sealed record ArchiveReport(
    IReadOnlyList<ArchiveManifest.TableArchive> Tables,
    int UnverifiedDays,
    int UnreadableLines,
    DateTime ManifestUpdatedUtc);
