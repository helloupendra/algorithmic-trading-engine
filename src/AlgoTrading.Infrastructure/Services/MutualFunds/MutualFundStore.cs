using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>What a batch of AMFI rows says about one scheme, ready to merge into <c>mf_schemes</c>.</summary>
public sealed record SchemeUpsert(
    int SchemeCode,
    string? IsinGrowth,
    string? IsinReinvest,
    string Amc,
    string SchemeName,
    string Plan,
    string Option,
    string? OptionLabel,
    string Category,
    DateOnly? FirstNavDate,
    DateOnly? LastNavDate,
    decimal? LastNav);

/// <summary>
/// The writes to <c>mf_schemes</c> and <c>mf_nav</c>: set-based upserts, a few
/// thousand rows a statement, because a history window is 0.4–0.6 million NAVs
/// and the production box is small. PostgreSQL only.
/// </summary>
public sealed class MutualFundStore
{
    /// <summary>Rows per statement: large enough to be quick, small enough to keep each one short.</summary>
    public const int BatchSize = 5000;

    private const string InsertSchemesSql = """
        INSERT INTO mf_schemes AS s
            ("SchemeCode", "IsinGrowth", "IsinReinvest", "Amc", "SchemeName", "Plan", "Option", "OptionLabel",
             "Category", "FirstNavDate", "LastNavDate", "LastNav", "IsActive", "FirstSeenUtc", "LastSeenUtc")
        SELECT code, isin_growth, isin_reinvest, amc, name, plan, option, label, category, first_date, last_date, last_nav,
               FALSE, @now, @now
        FROM unnest(@code, @isinGrowth, @isinReinvest, @amc, @name, @plan, @option, @label, @category,
                    @firstDate, @lastDate, @lastNav)
             AS u(code, isin_growth, isin_reinvest, amc, name, plan, option, label, category, first_date, last_date, last_nav)
        ON CONFLICT ("SchemeCode") DO UPDATE SET
            "FirstNavDate" = LEAST(s."FirstNavDate", EXCLUDED."FirstNavDate"),
            "LastNav" = CASE WHEN EXCLUDED."LastNavDate" IS NOT NULL
                               AND (s."LastNavDate" IS NULL OR EXCLUDED."LastNavDate" >= s."LastNavDate")
                             THEN EXCLUDED."LastNav" ELSE s."LastNav" END,
            "LastNavDate" = GREATEST(s."LastNavDate", EXCLUDED."LastNavDate"),
        """;

    // The daily file: what it says replaces what is stored, except an unknown plan or option.
    private const string MergeFromDailySql = InsertSchemesSql + """
            "IsinGrowth" = COALESCE(EXCLUDED."IsinGrowth", s."IsinGrowth"),
            "IsinReinvest" = COALESCE(EXCLUDED."IsinReinvest", s."IsinReinvest"),
            "Amc" = EXCLUDED."Amc",
            "SchemeName" = EXCLUDED."SchemeName",
            "Category" = EXCLUDED."Category",
            "Plan" = CASE WHEN EXCLUDED."Plan" <> 'unknown' THEN EXCLUDED."Plan" ELSE s."Plan" END,
            "Option" = CASE WHEN EXCLUDED."Option" <> 'unknown' THEN EXCLUDED."Option" ELSE s."Option" END,
            "OptionLabel" = COALESCE(EXCLUDED."OptionLabel", s."OptionLabel"),
            "LastSeenUtc" = EXCLUDED."LastSeenUtc"
        """;

    // The history: fills only what is unknown or empty.
    private const string MergeFromHistorySql = InsertSchemesSql + """
            "IsinGrowth" = COALESCE(s."IsinGrowth", EXCLUDED."IsinGrowth"),
            "IsinReinvest" = COALESCE(s."IsinReinvest", EXCLUDED."IsinReinvest"),
            "Plan" = CASE WHEN s."Plan" = 'unknown' THEN EXCLUDED."Plan" ELSE s."Plan" END,
            "Option" = CASE WHEN s."Option" = 'unknown' THEN EXCLUDED."Option" ELSE s."Option" END,
            "OptionLabel" = COALESCE(s."OptionLabel", EXCLUDED."OptionLabel"),
            "Category" = CASE WHEN s."Category" = '' THEN EXCLUDED."Category" ELSE s."Category" END,
            "Amc" = CASE WHEN s."Amc" = '' THEN EXCLUDED."Amc" ELSE s."Amc" END
        """;

    private readonly TradingDbContext _db;

    public MutualFundStore(TradingDbContext db) => _db = db;

    /// <summary>
    /// Merges schemes into the master. <paramref name="authoritative"/> (the
    /// daily file): its name, AMC, category and any plan or option it knows
    /// replace what is stored. Otherwise (the history): it adds schemes the
    /// master lacks and fills only what is unknown or empty, since the daily
    /// file's base name and fields are the better ones. Either way NAV dates
    /// only widen: the first can move earlier, the last later.
    /// </summary>
    public async Task UpsertSchemesAsync(IReadOnlyList<SchemeUpsert> rows, bool authoritative, DateTime nowUtc, CancellationToken ct)
    {
        // Ordered by code: two writers locking the same rows in the same order cannot deadlock.
        foreach (var batch in rows.OrderBy(r => r.SchemeCode).Chunk(BatchSize))
        {
            await _db.Database.ExecuteSqlRawAsync(
                authoritative ? MergeFromDailySql : MergeFromHistorySql,
                [
                    new NpgsqlParameter("now", DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)),
                    Array("code", NpgsqlDbType.Integer, batch.Select(r => r.SchemeCode).ToArray()),
                    Array("isinGrowth", NpgsqlDbType.Text, batch.Select(r => r.IsinGrowth).ToArray()),
                    Array("isinReinvest", NpgsqlDbType.Text, batch.Select(r => r.IsinReinvest).ToArray()),
                    Array("amc", NpgsqlDbType.Text, batch.Select(r => Clip(r.Amc, 120)).ToArray()),
                    Array("name", NpgsqlDbType.Text, batch.Select(r => Clip(r.SchemeName, 300)).ToArray()),
                    Array("plan", NpgsqlDbType.Text, batch.Select(r => r.Plan).ToArray()),
                    Array("option", NpgsqlDbType.Text, batch.Select(r => r.Option).ToArray()),
                    Array("label", NpgsqlDbType.Text, batch.Select(r => r.OptionLabel is null ? null : Clip(r.OptionLabel, 120)).ToArray()),
                    Array("category", NpgsqlDbType.Text, batch.Select(r => Clip(r.Category, 200)).ToArray()),
                    Array("firstDate", NpgsqlDbType.Date, batch.Select(r => r.FirstNavDate).ToArray()),
                    Array("lastDate", NpgsqlDbType.Date, batch.Select(r => r.LastNavDate).ToArray()),
                    Array("lastNav", NpgsqlDbType.Numeric, batch.Select(r => r.LastNav).ToArray()),
                ],
                ct);
        }
    }

    /// <summary>
    /// Writes NAVs: new days are inserted, a day already stored is changed only
    /// when AMFI's value differs (a correction), so a repeat costs no writes.
    /// Returns the rows inserted or changed.
    /// </summary>
    public async Task<int> UpsertNavsAsync(IReadOnlyList<MutualFundNav> rows, CancellationToken ct)
    {
        int written = 0;
        // One row per scheme and day, or ON CONFLICT would meet the same row twice in one statement.
        var unique = rows.GroupBy(r => (r.SchemeCode, r.Date)).Select(g => g.Last()).OrderBy(r => r.SchemeCode).ThenBy(r => r.Date).ToList();
        foreach (var batch in unique.Chunk(BatchSize))
        {
            written += await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO mf_nav AS n ("SchemeCode", "Date", "Nav", "Source")
                SELECT * FROM unnest(@code, @date, @nav, @source)
                ON CONFLICT ("SchemeCode", "Date") DO UPDATE SET "Nav" = EXCLUDED."Nav", "Source" = EXCLUDED."Source"
                WHERE n."Nav" <> EXCLUDED."Nav"
                """,
                [
                    Array("code", NpgsqlDbType.Integer, batch.Select(r => r.SchemeCode).ToArray()),
                    Array("date", NpgsqlDbType.Date, batch.Select(r => r.Date).ToArray()),
                    Array("nav", NpgsqlDbType.Numeric, batch.Select(r => r.Nav).ToArray()),
                    Array("source", NpgsqlDbType.Text, batch.Select(r => r.Source).ToArray()),
                ],
                ct);
        }

        return written;
    }

    /// <summary>
    /// Marks a scheme active when its newest NAV is within <paramref name="days"/>
    /// of <paramref name="asOf"/>, the newest NAV day of AMFI's latest file (not
    /// today: a sync that has failed for a week must not retire every scheme).
    /// Returns the schemes whose flag changed.
    /// </summary>
    public Task<int> RefreshActiveAsync(DateOnly asOf, int days, CancellationToken ct) =>
        _db.Database.ExecuteSqlRawAsync(
            """
            UPDATE mf_schemes
            SET "IsActive" = COALESCE("LastNavDate" >= @cutoff, FALSE)
            WHERE "IsActive" IS DISTINCT FROM COALESCE("LastNavDate" >= @cutoff, FALSE)
            """,
            [new NpgsqlParameter("cutoff", NpgsqlDbType.Date) { Value = asOf.AddDays(-days) }],
            ct);

    /// <summary>
    /// Compresses the <c>mf_nav</c> chunks the backfill has finished with: older
    /// than the 30-day policy's cut-off and wholly on or after <paramref name="cursor"/>,
    /// which no later (older) window writes to. Done window by window, outside
    /// market hours, so a backfill never leaves gigabytes for the 12-hourly
    /// policy job to compress at once, perhaps in the middle of a session.
    /// Returns the chunks compressed.
    /// </summary>
    public Task<int> CompressFinishedChunksAsync(DateOnly cursor, CancellationToken ct) =>
        _db.Database.SqlQueryRaw<int>(
                """
                SELECT count(compress_chunk(format('%I.%I', chunk_schema, chunk_name)::regclass, true))::int AS "Value"
                FROM timescaledb_information.chunks
                WHERE hypertable_name = 'mf_nav'
                  AND NOT is_compressed
                  AND range_start >= @cursor::timestamptz
                  AND range_end < now() - INTERVAL '30 days'
                """,
                new NpgsqlParameter("cursor", NpgsqlDbType.Date) { Value = cursor })
            .SingleAsync(ct);

    /// <summary>Every scheme's stored newest NAV day and value, for the daily sync to write only what is new.</summary>
    public async Task<Dictionary<int, (DateOnly? Date, decimal? Nav)>> LastNavsAsync(CancellationToken ct) =>
        (await _db.MutualFundSchemes.AsNoTracking()
            .Select(s => new { s.SchemeCode, s.LastNavDate, s.LastNav })
            .ToListAsync(ct))
        .ToDictionary(s => s.SchemeCode, s => (s.LastNavDate, s.LastNav));

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    private static NpgsqlParameter Array<T>(string name, NpgsqlDbType type, T[] values) =>
        new(name, NpgsqlDbType.Array | type) { Value = values };
}
