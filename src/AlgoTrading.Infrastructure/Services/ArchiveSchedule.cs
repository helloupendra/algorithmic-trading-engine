using System;
using System.Collections.Generic;
using System.Globalization;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// When the daily candle archive is due. Pure, so the catch-up rule can be
/// tested without a clock or a database.
/// </summary>
public static class ArchiveSchedule
{
    public static readonly TimeSpan DefaultRunAtIst = new(23, 50, 0);
    public const int MaxCatchUpDays = 7;

    public static TimeSpan ParseRunAt(string? value)
        => TimeSpan.TryParseExact(value ?? "", @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : DefaultRunAtIst;

    /// <summary>
    /// The IST days that are due: from the day after the last archived one
    /// (or just the latest due day, when nothing was ever archived) up to today
    /// once the clock has passed the run time — capped so a long outage does
    /// not turn into a week-long query at startup.
    /// </summary>
    public static IReadOnlyList<DateOnly> DueDays(DateOnly? lastArchived, DateTime nowIst, TimeSpan runAt, int maxDays = MaxCatchUpDays)
    {
        var today = DateOnly.FromDateTime(nowIst);
        var latestDue = nowIst.TimeOfDay >= runAt ? today : today.AddDays(-1);
        var first = lastArchived is { } last ? last.AddDays(1) : latestDue;
        var floor = latestDue.AddDays(-(maxDays - 1));
        if (first < floor) first = floor;

        var days = new List<DateOnly>();
        for (var d = first; d <= latestDue; d = d.AddDays(1)) days.Add(d);
        return days;
    }
}
