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
/// <para>
/// Telegram reads the message as HTML and refuses the whole of it for a bare
/// <c>&lt;</c> or <c>&amp;</c>, or past 4,096 characters, and the sender has no
/// plain-text retry; the day is still marked as sent. A vendor's error can be
/// its proxy's HTML error page (FYERS puts the body of a failed answer in its
/// error), so the message is escaped, each vendor's answer is cut short, and
/// past ten days the gap days are listed by date alone. The log keeps the same
/// text unescaped, and the vendors' full answers are in the check's info line.
/// </para>
/// </remarks>
public static class VixBackfillReport
{
    public const string GapTitle = "India VIX gap not filled";
    public const string FailedTitle = "India VIX check failed";

    // The vendors' answers in a message: enough to see who refused and why.
    private const int MaxFetchLines = 6;

    // One vendor's answer in a message, cut to this: an error can carry a whole HTML page.
    private const int MaxFetchChars = 200;

    // Gap days listed with their bar counts; past these, by date alone.
    private const int DaysInDetail = 10;

    // An exception's text in the "check failed" message.
    private const int MaxErrorChars = 300;

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
                Html(message),
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

    /// <summary>
    /// The System message for gap days, as plain text: which, how short, who was
    /// asked, and what to do. Bounded whatever the window and the vendors said;
    /// <see cref="Html"/> it before it goes to Telegram.
    /// </summary>
    public static string GapMessage(VixBackfillResult result, IReadOnlyList<DateOnly> days)
    {
        var detail = days.Take(DaysInDetail).Select(day =>
        {
            var rows = result.After.Where(c => c.Day == day).OrderBy(c => c.Minutes).Select(c => $"{c.Minutes}m {c.Present}/{c.Expected}");
            return $"{Iso(day)} ({string.Join(", ", rows)})";
        }).ToList();
        if (days.Count > DaysInDetail)
        {
            detail.Add($"and {days.Count - DaysInDetail} more: {string.Join(", ", days.Skip(DaysInDetail).Select(Iso))}");
        }

        string asked = result.Fetches.Count == 0
            ? "No vendor could be asked."
            : "Asked: " + string.Join("; ", result.Fetches.Take(MaxFetchLines).Select(f => Clip(f, MaxFetchChars)))
              + (result.Fetches.Count > MaxFetchLines ? $"; and {result.Fetches.Count - MaxFetchLines} more" : string.Empty) + ".";

        return $"India VIX has a gap the nightly backfill could not fill: {string.Join("; ", detail)}. "
               + "The forecasts drop these days, so range.har-vix and the logits cannot forecast the day after each. "
               + asked + " "
               + "If a day was an exchange holiday, the holiday calendar is missing it. "
               + "The check asks again after every trading day's archive; to fill by hand, see \"India VIX\" in docs/modules/data_module.md.";
    }

    /// <summary>The System message when the check itself threw, escaped for Telegram.</summary>
    public static string FailedMessage(DateOnly through, Exception error)
        => Html($"The nightly India VIX check through {Iso(through)} stopped: {Clip(IncidentRedaction.Mask(error.Message), MaxErrorChars)}. "
                + "A VIX gap may be going unfilled; the check runs again after the next trading day's archive (23:50 IST, or 00:15 while MCX closes at 23:55). "
                + "See NightlyArchiveService in logs/api.log.");

    /// <summary>Plain text for a message Telegram reads as HTML.</summary>
    public static string Html(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Clip(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
