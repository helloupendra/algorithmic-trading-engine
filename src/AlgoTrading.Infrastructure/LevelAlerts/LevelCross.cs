using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Infrastructure.LevelAlerts;

/// <summary>
/// One reading of what a rule watches: a price or a PCR, when it was true, and
/// the exchange session (IST trading day) it was read in.
/// </summary>
public readonly record struct LevelSample(decimal Value, DateTime AsOfUtc, DateOnly Session);

/// <summary>What one pair of readings did to one rule.</summary>
/// <param name="Fired">The pair crossed the level while the rule was armed.</param>
/// <param name="Armed">Whether the rule is armed after the pair.</param>
public readonly record struct LevelStep(bool Fired, bool Armed);

/// <summary>
/// The rules of a level alert, pure so each one is pinned by a test rather
/// than discovered on a live morning.
/// </summary>
/// <remarks>
/// <para>
/// A cross is two readings on opposite sides of the level, never one reading
/// on the far side: a rule made while NIFTY is already above 24,800 does not
/// fire until NIFTY has been at or below it and comes back up. "Above" is
/// strictly above, as the indicator alerts read RSI against 70: from at or
/// below the level to above it. A tie is not across, so a price that prints
/// the level and falls back has not crossed it; that is what "touch" is for.
/// </para>
/// <para>
/// Both readings must come from the same session. The first fresh reading of
/// a day is a baseline, so an overnight gap through the level is not a cross:
/// nothing traded at the level, and the same rule must not fire or not fire
/// depending on whether the API happened to restart overnight (it does,
/// most nights, when the desk deploys). The same goes for the first reading
/// after a restart in the session.
/// </para>
/// </remarks>
public static class LevelCross
{
    /// <summary>A price quote older than this never fires: the feed is not saying what the price is now.</summary>
    public static readonly TimeSpan PriceFreshFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A chain capture older than this never fires. The recorder captures the
    /// nearest expiry about once a minute, so a minute would refuse a good
    /// capture read just before the next; three means the recorder has stopped.
    /// </summary>
    public static readonly TimeSpan PcrFreshFor = TimeSpan.FromMinutes(3);

    /// <summary>A reading stamped this far ahead of the server clock is broken, not fresh.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(30);

    /// <summary>The re-arm distance a repeating price rule gets when none is given: a percent of the level.</summary>
    public const decimal DefaultPriceRearmPercent = 0.2m;

    /// <summary>The re-arm distance a repeating PCR rule gets when none is given, in PCR points.</summary>
    public const decimal DefaultPcrRearmPoints = 0.05m;

    /// <summary>
    /// The chain page shows PCR to two decimals, and a rule is compared at the
    /// same precision: "went above 1.30" then never arrives with the page
    /// still reading 1.30.
    /// </summary>
    public const int PcrDecimals = 2;

    public static bool TryParse(string? condition, out LevelCondition parsed)
    {
        parsed = condition switch
        {
            LevelAlertConditions.Above => LevelCondition.Above,
            LevelAlertConditions.Below => LevelCondition.Below,
            LevelAlertConditions.Touch => LevelCondition.Touch,
            _ => (LevelCondition)(-1),
        };
        return Enum.IsDefined(parsed);
    }

    /// <summary>Whether the move from <paramref name="previous"/> to <paramref name="current"/> meets the condition.</summary>
    public static bool Crossed(LevelCondition condition, decimal level, decimal previous, decimal current) => condition switch
    {
        LevelCondition.Above => previous <= level && current > level,
        LevelCondition.Below => previous >= level && current < level,
        // Reached it, or went through it between the two readings.
        LevelCondition.Touch => Math.Min(previous, current) <= level && level <= Math.Max(previous, current),
        _ => false,
    };

    /// <summary>
    /// Whether a disarmed repeating rule may fire again: the value has moved
    /// back from the level, the way it came, by at least <paramref name="gap"/>.
    /// </summary>
    public static bool Rearmed(LevelCondition condition, decimal level, decimal gap, decimal current) => condition switch
    {
        LevelCondition.Above => current <= level - gap,
        LevelCondition.Below => current >= level + gap,
        LevelCondition.Touch => Math.Abs(current - level) >= gap,
        _ => false,
    };

    /// <summary>
    /// One pair of readings for one rule. An armed rule fires on a cross and
    /// disarms; a disarmed one only checks whether it has re-armed, so the pair
    /// that re-arms it never also fires it.
    /// </summary>
    public static LevelStep Step(LevelCondition condition, decimal level, decimal gap, bool armed, decimal previous, decimal current)
    {
        if (armed) return Crossed(condition, level, previous, current) ? new LevelStep(true, false) : new LevelStep(false, true);
        return new LevelStep(false, Rearmed(condition, level, gap, current));
    }

    /// <summary>
    /// The re-arm distance in the value's own units: a price rule's percent of
    /// its level, a PCR rule's points as they are. The default when none is set.
    /// </summary>
    public static decimal GapFor(string kind, decimal level, decimal? rearmBy) => kind == LevelAlertKinds.Pcr
        ? rearmBy ?? DefaultPcrRearmPoints
        : Math.Abs(level) * (rearmBy ?? DefaultPriceRearmPercent) / 100m;

    /// <summary>Whether two readings may be compared: the same session, the later one newer.</summary>
    public static bool Comparable(LevelSample previous, LevelSample current) =>
        previous.Session == current.Session && current.AsOfUtc > previous.AsOfUtc;

    /// <summary>Fresh: no older than <paramref name="freshFor"/>, and not from the future.</summary>
    public static bool IsFresh(DateTime asOfUtc, DateTime nowUtc, TimeSpan freshFor) =>
        nowUtc - asOfUtc <= freshFor && asOfUtc - nowUtc <= FutureTolerance;

    /// <summary>
    /// When a quote was true: the exchange's stamp when the feed sent a real
    /// one, else when the API wrote it. A midnight-IST stamp is a date, not a
    /// time (BSE's pre-open replay of the previous close), and is not used.
    /// </summary>
    public static DateTime QuoteTime(DateTime updatedUtc, DateTime? exchangeUtc)
    {
        var updated = DateTime.SpecifyKind(updatedUtc, DateTimeKind.Utc);
        if (exchangeUtc is not { } stamp) return updated;
        var utc = DateTime.SpecifyKind(stamp, DateTimeKind.Utc);
        return IstTime.IsMidnightIst(utc) ? updated : utc;
    }

    /// <summary>"above", "below" or "at": where a value sits against the level, for the page.</summary>
    public static string SideOf(decimal value, decimal level) => value > level ? "above" : value < level ? "below" : "at";

    /// <summary>A PCR as rules compare it: rounded as the chain page shows it.</summary>
    public static decimal RoundPcr(decimal pcr) => Math.Round(pcr, PcrDecimals, MidpointRounding.AwayFromZero);
}

/// <summary>The conditions, as the pure rules read them.</summary>
public enum LevelCondition
{
    Above,
    Below,
    Touch,
}
