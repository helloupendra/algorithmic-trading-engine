using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The clock a recap run is timed by.
///
/// A LivePaper run started with <c>{"session":"recap","recap_date":"yyyy-MM-dd"}</c>
/// trades an evening replay of that day's session, in real time. Its runner
/// stamps signals with the replayed exchange time, but everything the API wrote
/// by itself — fills, positions, risk-guard exits, the stop — took the wall
/// clock. On 2026-09-11 a NIFTY entry signalled at 10:07:38 showed as filled at
/// 19:07:39, so no trade could be checked against that day's chart, and the
/// activity list sorted every risk-guard exit (19:21) above the trades it closed
/// (10:07).
///
/// So a recap run is clocked the way a backtest already is: by the market it
/// trades. For rows the API writes itself it reads that clock off the run's spot
/// quote, which a replay moves every second.
/// </summary>
public static class RecapClock
{
    public const string RecapSession = "recap";

    /// <summary>True when the run's parameters say <c>"session": "recap"</c>.</summary>
    public static bool IsRecap(string? parametersJson) => Read(parametersJson).IsRecap;

    /// <summary>The replayed day, when a recap run's parameters name a valid one.</summary>
    public static DateOnly? RecapDate(string? parametersJson) => Read(parametersJson).Date;

    /// <summary>
    /// The replayed session's time now; null for a run that is not a recap. Null
    /// too for a recap whose spot quote is not on the replayed day (the replay
    /// has not started, or has not reached this spot): the caller then keeps the
    /// wall clock rather than stamp a row with another day's time.
    /// </summary>
    public static async Task<DateTime?> NowAsync(TradingDbContext dbContext, SimulationRun run, CancellationToken cancellationToken)
    {
        if (PaperTradingService.IsReplay(run.Mode)) return null;

        var (isRecap, date) = Read(run.ParametersJson);
        if (!isRecap || string.IsNullOrWhiteSpace(run.Symbol)) return null;

        var stamp = await dbContext.LiveQuotesLatest.AsNoTracking()
            .Where(x => x.Symbol == run.Symbol)
            .Select(x => x.ExchangeTimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return OnReplayedDay(stamp, date);
    }

    /// <summary>
    /// The stamp as UTC when it can be the replayed session's time: a real minute
    /// (not a date-only midnight stamp) on the replayed day, when one is named.
    /// </summary>
    internal static DateTime? OnReplayedDay(DateTime? stamp, DateOnly? recapDate)
    {
        if (stamp is null) return null;

        var utc = stamp.Value.Kind switch
        {
            DateTimeKind.Utc => stamp.Value,
            DateTimeKind.Local => stamp.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(stamp.Value, DateTimeKind.Utc),
        };

        if (IstTime.IsMidnightIst(utc)) return null;
        if (recapDate is not null && DateOnly.FromDateTime(IstTime.ToIst(utc)) != recapDate) return null;
        return utc;
    }

    private static (bool IsRecap, DateOnly? Date) Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (false, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (false, null);

            bool isRecap = root.TryGetProperty("session", out var session)
                           && session.ValueKind == JsonValueKind.String
                           && string.Equals(session.GetString()?.Trim(), RecapSession, StringComparison.OrdinalIgnoreCase);
            if (!isRecap) return (false, null);

            DateOnly? date = root.TryGetProperty("recap_date", out var raw)
                             && raw.ValueKind == JsonValueKind.String
                             && DateOnly.TryParseExact(raw.GetString(), "yyyy-MM-dd", out var parsed)
                ? parsed
                : null;
            return (true, date);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }
}
