using System.Diagnostics.CodeAnalysis;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// What an index's moment looked like and what followed it: the one definition of the AI Trader's "situation".
/// The same functions fill <c>ai_trader_situations</c> from the stored candles (<see cref="AiTraderSituationBuilder"/>)
/// and describe the moment a brief is built at (<see cref="SimilarMomentsSection"/>), so the two cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// A moment knows only the 1-minute bars that began before its minute: at 10:25 the 10:24 bar, which closed at
/// 10:25, is the last one. Bars outside the regular session (09:15–15:30 IST: the pre-open auction, a feed that
/// keeps quoting after the close, a date-only midnight stamp) are left out wherever they come from.
/// </para>
/// <para>
/// A moment cannot be described when the index has no minute of the day yet, when its last minute is more than
/// 10 minutes old (a feed gap), when the previous session's close is not recorded within a week (its last bar must be
/// from 15:00 on, or its bars stopped early), or when there are fewer than 50 five-minute bars for EMA 50. India VIX
/// missing leaves the VIX features null, not the moment.
/// </para>
/// </remarks>
public static class SituationMath
{
    /// <summary>The first moment of a day.</summary>
    public static readonly TimeOnly FirstSlot = new(9, 25);

    /// <summary>The last moment of a day.</summary>
    public static readonly TimeOnly LastSlot = new(15, 5);

    public static readonly TimeSpan SlotEvery = TimeSpan.FromMinutes(10);

    /// <summary>The day's moments: every 10 minutes from 09:25 to 15:05 IST (35).</summary>
    public static readonly IReadOnlyList<TimeOnly> Slots = BuildSlots();

    public static readonly TimeOnly SessionOpen = new(9, 15);

    public static readonly TimeOnly SessionClose = new(15, 30);

    /// <summary>The outcomes end here, five minutes before the close, where a position is still closed at a market price.</summary>
    public static readonly TimeOnly OutcomeEnd = new(15, 25);

    /// <summary>The session minutes before a moment the EMAs are computed over: a little over two and a half sessions.</summary>
    public const int LookbackMinutes = 1000;

    /// <summary>
    /// The 1-minute bars a reader fetches up to a moment (or a day's end), so that after anything outside the
    /// session is dropped at least <see cref="LookbackMinutes"/> session minutes remain. Both readers use it.
    /// </summary>
    public const int MinutesToRead = 2500;

    /// <summary>A price older than this at the moment it is read for is not known: there is a gap in the bars.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    /// <summary>The previous close must be this recent; older means a session is missing from the bars.</summary>
    public static readonly TimeSpan PreviousCloseWithin = TimeSpan.FromDays(7);

    /// <summary>The previous session's last bar must have begun this late; earlier, its bars stopped before the close.</summary>
    public static readonly TimeOnly PreviousCloseFrom = new(15, 0);

    /// <summary>No expiry within this many calendar days means the expiry calendar has a gap there, not a long wait.</summary>
    public const int ExpiryWithinDays = 45;

    /// <summary>The numeric features in a fixed order, as <see cref="Vector"/> returns them, for distances.</summary>
    public static readonly IReadOnlyList<string> VectorNames =
    [
        "move since the previous close", "move since the open", "last 30 min", "range so far", "EMA20−EMA50 gap",
        "India VIX", "VIX change", "minutes since the open", "trading days to expiry",
    ];

    public const string Above = "above";
    public const string Below = "below";
    public const string Between = "between";

    private static IReadOnlyList<TimeOnly> BuildSlots()
    {
        var slots = new List<TimeOnly>();
        for (var t = FirstSlot; t <= LastSlot; t = t.Add(SlotEvery)) slots.Add(t);
        return slots;
    }

    /// <summary>
    /// The moment's features from the index's 1-minute bars and India VIX's, both oldest first and of any span (only
    /// what began before the moment's minute is read), with the trading days to the nearest expiry. Null with the
    /// reason when the moment cannot be described.
    /// </summary>
    public static (SituationFacts? Facts, string? Why) Features(
        DateTime momentUtc, IReadOnlyList<LiveBarResponse> minutes, IReadOnlyList<LiveBarResponse> vixMinutes, int? daysToExpiry)
    {
        var cut = Minute(momentUtc);
        var day = IstTime.DateOf(cut);
        var openUtc = IstTime.FromIst(day.ToDateTime(SessionOpen));
        var known = Session(minutes).Where(b => b.BarStartUtc < cut).ToList();
        var today = known.Where(b => b.BarStartUtc >= openUtc).ToList();
        if (today.Count == 0) return (null, "no minute of today's session recorded yet");

        var lastBar = today[^1];
        if (lastBar.BarStartUtc < cut - Stale)
        {
            return (null, $"its last minute recorded is {IstTime.ToIst(lastBar.BarStartUtc):HH:mm}, over 10 minutes old");
        }

        var previous = known.LastOrDefault(b => b.BarStartUtc < openUtc);
        if (!IsClose(previous, openUtc))
        {
            return (null, "the previous session's close is not recorded");
        }

        var window = known.Count > LookbackMinutes ? known.GetRange(known.Count - LookbackMinutes, LookbackMinutes) : known;
        var closes = MarketBriefBuilder.FiveMinute(window).Select(b => (double)b.Close).ToList();
        double? ema20 = IndicatorMath.Ema(closes, 20).LastOrDefault(v => v is not null);
        double? ema50 = IndicatorMath.Ema(closes, 50).LastOrDefault(v => v is not null);
        if (ema20 is not double e20 || ema50 is not double e50 || e50 <= 0)
        {
            return (null, $"only {closes.Count} five-minute bars, fewer than EMA 50 needs");
        }

        decimal price = lastBar.Close, open = today[0].Open, previousClose = previous.Close;
        decimal high = today.Max(b => b.High), low = today.Min(b => b.Low);
        decimal halfHourAgo = today.LastOrDefault(b => b.BarStartUtc < cut.AddMinutes(-30))?.Close ?? open;
        double last = (double)price;
        string side = last > Math.Max(e20, e50) ? Above : last < Math.Min(e20, e50) ? Below : Between;

        // India VIX: its own last minute, as fresh as the index's must be, and its own previous close.
        double? vix = null, vixChange = null;
        var vixKnown = Session(vixMinutes).Where(b => b.BarStartUtc < cut).ToList();
        var vixLast = vixKnown.LastOrDefault(b => b.BarStartUtc >= openUtc);
        if (vixLast is not null && vixLast.BarStartUtc >= cut - Stale)
        {
            vix = Round((double)vixLast.Close);
            var vixPrevious = vixKnown.LastOrDefault(b => b.BarStartUtc < openUtc);
            if (IsClose(vixPrevious, openUtc))
            {
                vixChange = Pct(vixLast.Close, vixPrevious.Close);
            }
        }

        return (new SituationFacts(
            day, TimeOnly.FromDateTime(IstTime.ToIst(cut)), price,
            Pct(price, previousClose), Pct(price, open), Pct(price, halfHourAgo), Round((double)((high - low) / previousClose) * 100),
            side, Round((e20 - e50) / e50 * 100), vix, vixChange,
            (int)(cut - openUtc).TotalMinutes, daysToExpiry, IsoWeekday(day)), null);
    }

    /// <summary>
    /// What followed a moment whose price was <paramref name="price"/>: the returns to 30 and 60 minutes later (or
    /// to 15:25 when sooner) and to 15:25, and the highest and lowest prints from the moment to 15:25. A return is
    /// null when no bar after the moment ends within 10 minutes of its horizon; the extremes are null with the return
    /// to 15:25, since the path to it is then incomplete.
    /// </summary>
    public static SituationOutcomes Outcomes(DateTime momentUtc, IReadOnlyList<LiveBarResponse> minutes, decimal price)
    {
        var cut = Minute(momentUtc);
        var day = IstTime.DateOf(cut);
        var end = IstTime.FromIst(day.ToDateTime(OutcomeEnd));
        var after = Session(minutes).Where(b => b.BarStartUtc >= cut && b.BarStartUtc < end).ToList();

        double? ReturnTo(DateTime horizon)
        {
            var at = horizon < end ? horizon : end;
            var bar = after.LastOrDefault(b => b.BarStartUtc < at);
            return bar is not null && bar.BarStartUtc >= at - Stale ? Pct(bar.Close, price) : null;
        }

        var toClose = ReturnTo(end);
        double? up = null, down = null;
        if (toClose is not null && after.Count > 0)
        {
            up = Math.Max(0, Pct(after.Max(b => b.High), price));
            down = Math.Min(0, Pct(after.Min(b => b.Low), price));
        }

        return new SituationOutcomes(ReturnTo(cut.AddMinutes(30)), ReturnTo(cut.AddMinutes(60)), toClose, up, down);
    }

    /// <summary>
    /// Trading days from <paramref name="day"/> to the nearest expiry on or after it: 0 on the expiry day, 1 the
    /// trading day before. Null when no expiry lies within <see cref="ExpiryWithinDays"/> calendar days.
    /// </summary>
    public static int? TradingDaysToExpiry(DateOnly day, IReadOnlyList<DateOnly> expiriesAscending, Func<DateOnly, bool> isTradingDay)
    {
        DateOnly? nearest = null;
        foreach (var e in expiriesAscending)
        {
            if (e < day) continue;
            nearest = e;
            break;
        }

        if (nearest is not DateOnly expiry || expiry.DayNumber - day.DayNumber > ExpiryWithinDays) return null;
        int count = 0;
        for (var d = day.AddDays(1); d <= expiry; d = d.AddDays(1))
        {
            if (isTradingDay(d)) count++;
        }

        return count;
    }

    /// <summary>The numeric features in <see cref="VectorNames"/> order; NaN where one is not known.</summary>
    public static double[] Vector(SituationFacts f) =>
    [
        f.MovePrevClosePct, f.MoveOpenPct, f.Last30MinPct, f.RangePct, f.EmaGapPct,
        f.Vix ?? double.NaN, f.VixChangePct ?? double.NaN, f.MinutesSinceOpen, f.DaysToExpiry ?? double.NaN,
    ];

    /// <summary>A stored row's features.</summary>
    public static SituationFacts Facts(AiTraderSituation s) => new(
        s.Day, s.Slot, s.Price, s.MovePrevClosePct, s.MoveOpenPct, s.Last30MinPct, s.RangePct, s.EmaSide, s.EmaGapPct,
        s.Vix, s.VixChangePct, s.MinutesSinceOpen, s.DaysToExpiry, s.Weekday);

    /// <summary>A row from a moment's features and what followed.</summary>
    public static AiTraderSituation Row(string underlying, SituationFacts f, SituationOutcomes o, DateTime builtUtc) => new()
    {
        Underlying = underlying, Day = f.Day, Slot = f.Time, BuiltUtc = builtUtc, Price = f.Price,
        MovePrevClosePct = f.MovePrevClosePct, MoveOpenPct = f.MoveOpenPct, Last30MinPct = f.Last30MinPct, RangePct = f.RangePct,
        EmaSide = f.EmaSide, EmaGapPct = f.EmaGapPct, Vix = f.Vix, VixChangePct = f.VixChangePct,
        MinutesSinceOpen = f.MinutesSinceOpen, DaysToExpiry = f.DaysToExpiry, Weekday = f.Weekday,
        Return30MinPct = o.Return30MinPct, Return60MinPct = o.Return60MinPct, ReturnToClosePct = o.ReturnToClosePct,
        MaxUpPct = o.MaxUpPct, MaxDownPct = o.MaxDownPct,
    };

    /// <summary>The bars of the regular session (09:15–15:30 IST) that carry prices, oldest first.</summary>
    /// <remarks>
    /// IST has no daylight saving, so the fixed offset gives the IST time: a backfill asks this millions of times. A bar
    /// with a zero or negative price is a broken print, not a price, and would divide by zero further on.
    /// </remarks>
    public static List<LiveBarResponse> Session(IEnumerable<LiveBarResponse> minutes)
    {
        var kept = minutes
            .Where(b =>
            {
                var t = TimeOnly.FromTimeSpan((b.BarStartUtc + IstTime.Offset).TimeOfDay);
                return t >= SessionOpen && t < SessionClose && b.Open > 0 && b.High > 0 && b.Low > 0 && b.Close > 0;
            })
            .ToList();

        // Usually already in order: sorted only when it is not.
        for (int i = 1; i < kept.Count; i++)
        {
            if (kept[i].BarStartUtc < kept[i - 1].BarStartUtc) return kept.OrderBy(b => b.BarStartUtc).ToList();
        }

        return kept;
    }

    /// <summary>A previous session's last bar that is its close: within a week of today's open and from 15:00 on.</summary>
    private static bool IsClose([NotNullWhen(true)] LiveBarResponse? bar, DateTime openUtc) =>
        bar is not null && bar.BarStartUtc >= openUtc - PreviousCloseWithin
        && TimeOnly.FromTimeSpan((bar.BarStartUtc + IstTime.Offset).TimeOfDay) >= PreviousCloseFrom;

    private static DateTime Minute(DateTime utc)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTime(u.Ticks - u.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }

    private static int IsoWeekday(DateOnly day) => day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;

    private static double Pct(decimal now, decimal then) => Round((double)((now - then) / then) * 100);

    private static double Round(double value) => Math.Round(value, 4);
}

/// <summary>A moment's features (<see cref="SituationMath.Features"/>); percentages in percent.</summary>
public sealed record SituationFacts(
    DateOnly Day,
    TimeOnly Time,
    decimal Price,
    double MovePrevClosePct,
    double MoveOpenPct,
    double Last30MinPct,
    double RangePct,
    string EmaSide,
    double EmaGapPct,
    double? Vix,
    double? VixChangePct,
    int MinutesSinceOpen,
    int? DaysToExpiry,
    int Weekday);

/// <summary>What followed a moment (<see cref="SituationMath.Outcomes"/>); percentages in percent.</summary>
public sealed record SituationOutcomes(
    double? Return30MinPct,
    double? Return60MinPct,
    double? ReturnToClosePct,
    double? MaxUpPct,
    double? MaxDownPct);
