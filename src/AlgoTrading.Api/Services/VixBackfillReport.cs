using System.Globalization;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Says what the nightly India VIX check did: one line in the API log every
/// night, and the System channel (Telegram and the console's alerts) when a day
/// is left that the forecasts cannot use.
/// </summary>
/// <remarks>
/// <para>
/// A gap is an error in the log, which is also what Sentinel's logs agent opens
/// an incident from, and one System message. It is sent once: the check asks
/// the vendors for the day again every night while the day is in its window,
/// but the same unfillable day in a message every night for a month teaches the
/// reader to skip the channel. The days already sent are kept in
/// <c>system_settings</c> (<see cref="SystemSettingKeys.VixGapsReported"/>); on
/// later nights the gap is a warning in the log.
/// </para>
/// <para>
/// A day short of a few bars but still usable (<see cref="VixDayState.Holes"/>)
/// is logged, not sent: the forecasts read it, and the vendors are asked for the
/// rest again the next night.
/// </para>
/// </remarks>
public static class VixBackfillReport
{
    public const string GapTitle = "India VIX gap not filled";
    public const string FailedTitle = "India VIX check failed";

    // The vendors' answers in a message: enough to see who refused and why.
    private const int MaxFetchLines = 6;

    public static async Task ReportAsync(
        VixBackfillResult result,
        IProcessSettingsStore settings,
        ISystemNotifier notifier,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (result.Days.Count == 0)
        {
            logger.LogWarning(
                "India VIX check through {Through}: the exchange calendar has no trading day up to it, so nothing was checked.",
                Iso(result.Through));
            return;
        }

        var gaps = result.GapDays;
        var holes = result.Holes;
        logger.LogInformation(
            "India VIX check through {Through}: {Days} trading days from {First}; {Filled} bars filled ({Fetches}); {Gaps} day(s) the forecasts cannot use, {Holes} day/resolution(s) short of a few bars.",
            Iso(result.Through), result.Days.Count, Iso(result.Days[0]), result.BarsFilled,
            result.Fetches.Count == 0 ? "nothing was missing, nothing fetched" : IncidentRedaction.Mask(string.Join("; ", result.Fetches)),
            gaps.Count, holes.Count);
        if (holes.Count > 0)
        {
            logger.LogInformation(
                "India VIX bars no vendor had, on days the forecasts can still use: {Holes}.",
                string.Join(", ", holes.Select(h => h.ToString())));
        }

        string? stored = await settings.GetAsync(SystemSettingKeys.VixGapsReported, cancellationToken);
        var reported = VixBackfillPlan.ParseDays(stored);
        var fresh = VixBackfillPlan.NewGaps(gaps, reported);

        if (fresh.Count > 0)
        {
            string message = IncidentRedaction.Mask(GapMessage(result, fresh));
            logger.LogError("{Message}", message);
            await notifier.NotifyAsync(
                NotificationCategory.System,
                NotificationSeverity.Error,
                GapTitle,
                message,
                symbol: VixBackfillPlan.Symbol,
                cancellationToken: cancellationToken);
        }
        else if (gaps.Count > 0)
        {
            logger.LogWarning(
                "India VIX still has a gap on {Days}, sent to the System channel on an earlier night; the vendors were asked again tonight.",
                string.Join(", ", gaps.Select(Iso)));
        }

        var healed = reported.Where(d => !gaps.Contains(d) && result.Days.Contains(d)).OrderBy(d => d).ToList();
        if (healed.Count > 0)
        {
            logger.LogInformation("India VIX gap now filled: {Days}.", string.Join(", ", healed.Select(Iso)));
        }

        // Every gap of tonight has now been sent, tonight or before; a day that
        // was filled or left the window is dropped.
        string value = VixBackfillPlan.FormatDays(gaps);
        if (stored is not null || value.Length > 0)
        {
            await settings.SetAsync(SystemSettingKeys.VixGapsReported, value, "NightlyArchiveService", cancellationToken);
        }
    }

    /// <summary>The System message for gap days: which, how short, who was asked, and what to do.</summary>
    public static string GapMessage(VixBackfillResult result, IReadOnlyList<DateOnly> days)
    {
        var detail = days.Select(day =>
        {
            var rows = result.After.Where(c => c.Day == day).OrderBy(c => c.Minutes).Select(c => $"{c.Minutes}m {c.Present}/{c.Expected}");
            return $"{Iso(day)} ({string.Join(", ", rows)})";
        });

        string asked = result.Fetches.Count == 0
            ? "No vendor could be asked."
            : "Asked: " + string.Join("; ", result.Fetches.Take(MaxFetchLines))
              + (result.Fetches.Count > MaxFetchLines ? $"; and {result.Fetches.Count - MaxFetchLines} more" : string.Empty) + ".";

        return $"India VIX has a gap the nightly backfill could not fill: {string.Join("; ", detail)}. "
               + "The forecasts drop these days, so range.har-vix and the logits cannot forecast the day after each. "
               + asked + " "
               + "If a day was an exchange holiday, the holiday calendar is missing it. "
               + "The check asks again after every trading day's archive; to fill by hand, see \"India VIX\" in docs/modules/data_module.md.";
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
