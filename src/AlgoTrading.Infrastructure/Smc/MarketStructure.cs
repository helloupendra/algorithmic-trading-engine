namespace AlgoTrading.Infrastructure.Smc;

/// <summary>One candle, as the structure reader needs it.</summary>
public readonly record struct StructureBar(DateTime TimeUtc, decimal Open, decimal High, decimal Low, decimal Close);

public enum SwingKind
{
    High,
    Low,
}

/// <summary>How a swing is decided.</summary>
public enum SwingMethod
{
    /// <summary>
    /// A high stands as a structural point once a later candle trades below the
    /// low of the candle that made it: the correction has taken that candle's
    /// liquidity, so the impulse is over. The mirror rule holds for a low. This
    /// is the "valid pullback" rule, and the default.
    /// </summary>
    ValidPullback,

    /// <summary>
    /// A high whose candle is above the highs of the <c>strength</c> candles on
    /// each side (a fractal), and the mirror for a low. Steadier on noisy
    /// one-minute charts, but it cannot mark a swing until <c>strength</c>
    /// candles have passed.
    /// </summary>
    Fractal,
}

/// <summary>A swing compared with the previous swing of its own kind.</summary>
public enum SwingLabel
{
    HigherHigh,
    LowerHigh,
    HigherLow,
    LowerLow,
}

public enum StructureEventKind
{
    /// <summary>Break of structure: the trend's own level gave way, so the trend continues.</summary>
    Bos,

    /// <summary>Change of character: the level that protected the trend gave way.</summary>
    Choch,
}

public enum TrendDirection
{
    None,
    Bullish,
    Bearish,
}

/// <summary>Which price has to pass a level for it to count as broken.</summary>
public enum BreakTrigger
{
    /// <summary>A candle has to close through the level. The stricter reading, and the default.</summary>
    Close,

    /// <summary>A wick through the level is enough.</summary>
    Wick,
}

public sealed record StructureSwing(
    int Index,
    DateTime TimeUtc,
    decimal Price,
    SwingKind Kind,
    /// <summary>Null for the first swing of its kind: there is nothing to compare it with.</summary>
    SwingLabel? Label,
    int ConfirmedIndex,
    DateTime ConfirmedTimeUtc)
{
    /// <summary>
    /// True for a swing the structure actually turned on: one whose level was
    /// broken, one left protecting the trend after a break, and the two levels
    /// live at the last candle. The others are pullbacks inside a leg, and a
    /// chart that labelled every one of them would be unreadable.
    /// </summary>
    public bool Major { get; init; }
}

/// <summary>A level breaking, drawn from the swing that made it to the candle that broke it.</summary>
public sealed record StructureEvent(
    StructureEventKind Kind,
    TrendDirection Direction,
    decimal Level,
    int LevelIndex,
    DateTime LevelTimeUtc,
    int BreakIndex,
    DateTime BreakTimeUtc,
    decimal BreakPrice);

/// <summary>
/// An inducement: the last pullback inside the current leg, whose stops the
/// market usually takes before carrying on. Drawn from that swing to the candle
/// that swept it, or to the right edge while it still stands.
/// </summary>
public sealed record Inducement(
    SwingKind Kind,
    decimal Level,
    int Index,
    DateTime TimeUtc,
    int? SweptIndex,
    DateTime? SweptTimeUtc)
{
    /// <summary>
    /// Where the leg that owned this inducement ended, for one that was never
    /// swept: the level stopped mattering there rather than standing to the
    /// right edge of the chart.
    /// </summary>
    public DateTime? EndedTimeUtc { get; init; }
}

/// <summary>Which pullback inside the leg is treated as the inducement.</summary>
public enum InducementMode
{
    /// <summary>
    /// The pullback the leg is on now — the reading the widely used indicators
    /// take, and the default here. A leg keeps working as the market moves.
    /// </summary>
    Last,

    /// <summary>
    /// The first pullback of the leg, which is how the concept is usually
    /// taught: those are the stops the market is said to be coming for. It is
    /// the stricter reading, and on a strong trend it can stall — BANKNIFTY's
    /// fall of March 2026 never traded back above its first pullback, so the
    /// structure sat unbroken for six months.
    /// </summary>
    First,
}

public sealed record MarketStructureResult(
    IReadOnlyList<StructureSwing> Swings,
    IReadOnlyList<StructureEvent> Events,
    IReadOnlyList<Inducement> Inducements,
    TrendDirection Trend,
    /// <summary>Breaking this level turns the trend: a change of character.</summary>
    decimal? ProtectedLevel,
    /// <summary>The last confirmed swing in the trend's direction. Breaking it, once the inducement is taken, is a break of structure.</summary>
    decimal? BreakLevel,
    /// <summary>Whether this leg's inducement has been swept, which is what a break of structure waits for.</summary>
    bool InducementTaken,
    /// <summary>The inducement still standing, if any.</summary>
    decimal? InducementLevel);

/// <summary>
/// Reads market structure the way Smart Money Concepts teaches it: swing points
/// labelled HH / HL / LH / LL, breaks of structure, changes of character, and the
/// inducement inside the current leg. Pure: no clock, no database, no I/O.
/// </summary>
/// <remarks>
/// <para>
/// SMC is taught, not specified, and its terms are used differently by different
/// teachers. This is one reading, stated exactly so a chart can be checked against
/// it:
/// </para>
/// <list type="number">
/// <item><b>Swings.</b> By default a high stands as a structural point at the
/// candle that takes the liquidity of the candle that made it: a later candle
/// trading below that candle's low ends the impulse and begins a valid pullback.
/// A low is the mirror. <see cref="SwingMethod.Fractal"/> is the other reading,
/// where a swing is the extreme of the <c>strength</c> candles on each side.
/// Either way a swing is only known at the candle that confirmed it
/// (<see cref="StructureSwing.ConfirmedIndex"/>), and this reader never acts on a
/// swing before then, so no mark depends on a price the chart had not yet
/// shown.</item>
/// <item><b>Alternation.</b> Two swings of the same kind in a row collapse into the
/// more extreme one, so highs and lows alternate.</item>
/// <item><b>Labels.</b> Each swing is compared with the previous swing of its own
/// kind: a higher high, a lower high, a higher low or a lower low. The first high
/// and the first low carry no label, because there is nothing to compare them
/// with.</item>
/// <item><b>Inducement.</b> Inside a bullish leg it is a confirmed swing low above
/// the protected low — the pullback whose stops the market is said to come for.
/// It is taken when a candle trades through it, wick included; once a leg has
/// been induced it stays induced until the next break.
/// <see cref="InducementMode"/> picks whether the leg's first pullback counts or
/// the one it is on now.</item>
/// <item><b>Break of structure.</b> In a bullish trend, a close above the last
/// confirmed swing high — but only once the leg's inducement has been swept.
/// That gate is the point of the inducement: the market takes the stops under
/// the pullback before it carries on, and without the gate a trend label flips
/// on every minor poke. After a BOS the leg needs a fresh pullback, taken again,
/// before the next one counts.</item>
/// <item><b>Change of character.</b> A close through the level that protected the
/// trend — the pullback the last break came out of. The trend turns and the leg
/// starts again. The first break of all, before any trend is known, is recorded
/// as a BOS.</item>
/// </list>
/// <para>
/// Where the schools differ, the switches say so: <see cref="SwingMethod"/> picks
/// how a swing is decided, <see cref="BreakTrigger"/> whether a wick counts, and
/// <see cref="InducementMode"/> whether the inducement is the leg's first
/// pullback (as taught) or the one it is on now (what the popular indicators
/// read, and what keeps working on a strong trend).
/// </para>
/// <para>
/// Every level here comes from a candle this reader had already seen, so the
/// marks on a chart can be recomputed from the candles alone, and none of them
/// moves once drawn.
/// </para>
/// </remarks>
public static class MarketStructure
{
    public const int DefaultStrength = 2;

    /// <summary>The most bars either side of a swing this reader accepts.</summary>
    public const int MaxStrength = 20;

    public static MarketStructureResult Read(
        IReadOnlyList<StructureBar> bars,
        SwingMethod method = SwingMethod.ValidPullback,
        int strength = DefaultStrength,
        BreakTrigger trigger = BreakTrigger.Close,
        InducementMode inducement = InducementMode.Last)
    {
        strength = Math.Clamp(strength, 1, MaxStrength);
        var found = method == SwingMethod.Fractal
            ? Alternate(FindSwingsByFractal(bars, strength))
            : FindSwingsByPullback(bars);
        Label(found);
        return Walk(bars, found, trigger, inducement);
    }

    /// <summary>
    /// Swings by the valid-pullback rule: the impulse's extreme stands as a
    /// structural point at the candle that takes the liquidity of the candle
    /// that made it — a later candle trading below that candle's low after a
    /// high, or above its high after a low. Highs and lows alternate by
    /// construction, and no swing is known before the candle that confirmed it.
    /// </summary>
    private static List<StructureSwing> FindSwingsByPullback(IReadOnlyList<StructureBar> bars)
    {
        var swings = new List<StructureSwing>();
        if (bars.Count < 2) return swings;

        var leg = 0;                 // +1 while an up leg's high is running, -1 for a down leg, 0 until the first break
        int highIndex = 0, lowIndex = 0;

        for (var i = 1; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (leg >= 0 && bar.High > bars[highIndex].High) highIndex = i;
            if (leg <= 0 && bar.Low < bars[lowIndex].Low) lowIndex = i;

            if (leg >= 0 && i > highIndex && bar.Low < bars[highIndex].Low)
            {
                swings.Add(new StructureSwing(highIndex, bars[highIndex].TimeUtc, bars[highIndex].High,
                    SwingKind.High, null, i, bar.TimeUtc));
                leg = -1;
                lowIndex = Extreme(bars, highIndex, i, lowest: true);
            }
            else if (leg <= 0 && i > lowIndex && bar.High > bars[lowIndex].High)
            {
                swings.Add(new StructureSwing(lowIndex, bars[lowIndex].TimeUtc, bars[lowIndex].Low,
                    SwingKind.Low, null, i, bar.TimeUtc));
                leg = 1;
                highIndex = Extreme(bars, lowIndex, i, lowest: false);
            }
        }

        return swings;
    }

    /// <summary>The lowest low (or highest high) between two bars, the later one included.</summary>
    private static int Extreme(IReadOnlyList<StructureBar> bars, int from, int to, bool lowest)
    {
        var best = from;
        for (var i = from + 1; i <= to; i++)
        {
            if (lowest ? bars[i].Low < bars[best].Low : bars[i].High > bars[best].High) best = i;
        }

        return best;
    }

    /// <summary>Swing highs and lows by the fractal rule, in bar order.</summary>
    private static List<StructureSwing> FindSwingsByFractal(IReadOnlyList<StructureBar> bars, int strength)
    {
        var found = new List<StructureSwing>();
        for (var i = strength; i < bars.Count - strength; i++)
        {
            if (IsSwing(bars, i, strength, high: true))
                found.Add(Swing(bars, i, strength, SwingKind.High, bars[i].High));
            else if (IsSwing(bars, i, strength, high: false))
                found.Add(Swing(bars, i, strength, SwingKind.Low, bars[i].Low));
        }

        return found;
    }

    private static bool IsSwing(IReadOnlyList<StructureBar> bars, int index, int strength, bool high)
    {
        var price = high ? bars[index].High : bars[index].Low;
        for (var step = 1; step <= strength; step++)
        {
            var left = high ? bars[index - step].High : bars[index - step].Low;
            var right = high ? bars[index + step].High : bars[index + step].Low;
            if (high ? left >= price || right >= price : left <= price || right <= price)
                return false;
        }

        return true;
    }

    private static StructureSwing Swing(IReadOnlyList<StructureBar> bars, int index, int strength, SwingKind kind, decimal price)
    {
        var confirmed = index + strength;
        return new StructureSwing(index, bars[index].TimeUtc, price, kind, null, confirmed, bars[confirmed].TimeUtc);
    }

    /// <summary>Collapses runs of same-kind swings into their most extreme one.</summary>
    private static List<StructureSwing> Alternate(List<StructureSwing> swings)
    {
        var kept = new List<StructureSwing>();
        foreach (var swing in swings)
        {
            if (kept.Count == 0 || kept[^1].Kind != swing.Kind)
            {
                kept.Add(swing);
                continue;
            }

            var previous = kept[^1];
            var replaces = swing.Kind == SwingKind.High ? swing.Price > previous.Price : swing.Price < previous.Price;
            if (replaces) kept[^1] = swing;
        }

        return kept;
    }

    /// <summary>Names each swing against the previous swing of its own kind.</summary>
    private static void Label(List<StructureSwing> swings)
    {
        decimal? lastHigh = null, lastLow = null;
        for (var i = 0; i < swings.Count; i++)
        {
            var swing = swings[i];
            if (swing.Kind == SwingKind.High)
            {
                if (lastHigh is { } previous)
                    swings[i] = swing with { Label = swing.Price > previous ? SwingLabel.HigherHigh : SwingLabel.LowerHigh };
                lastHigh = swing.Price;
            }
            else
            {
                if (lastLow is { } previous)
                    swings[i] = swing with { Label = swing.Price > previous ? SwingLabel.HigherLow : SwingLabel.LowerLow };
                lastLow = swing.Price;
            }
        }
    }


    /// <summary>
    /// Walks the candles once, in the order they printed: each swing is taken up
    /// at the candle that confirmed it, the leg's inducement is watched, and the
    /// levels are tested against the candle's own price. Nothing looks ahead.
    /// </summary>
    private static MarketStructureResult Walk(
        IReadOnlyList<StructureBar> bars, List<StructureSwing> swings, BreakTrigger trigger, InducementMode mode)
    {
        var events = new List<StructureEvent>();
        var inducements = new List<Inducement>();
        var major = new HashSet<int>();
        var byConfirmation = swings.ToLookup(s => s.ConfirmedIndex);

        var trend = TrendDirection.None;
        StructureSwing? guard = null;              // breaking this is a change of character
        StructureSwing? target = null;             // breaking this, once the inducement is taken, is a BOS
        StructureSwing? firstHigh = null, firstLow = null;   // the two live levels before any trend
        StructureSwing? lastHigh = null, lastLow = null;
        Inducement? inducement = null;
        var taken = false;                         // the leg's inducement has been swept

        for (var index = 0; index < bars.Count; index++)
        {
            foreach (var swing in byConfirmation[index])
            {
                if (swing.Kind == SwingKind.High)
                {
                    lastHigh = swing;
                    firstHigh ??= swing;
                    // In a rally the next high is the level to break; in a fall it
                    // is the pullback whose stops the market will want first.
                    if (trend == TrendDirection.Bullish) target = swing;
                    else if (trend == TrendDirection.Bearish && guard is not null && swing.Price < guard.Price)
                        inducement = Induce(inducement, inducements, swing, mode);
                }
                else
                {
                    lastLow = swing;
                    firstLow ??= swing;
                    if (trend == TrendDirection.Bearish) target = swing;
                    else if (trend == TrendDirection.Bullish && guard is not null && swing.Price > guard.Price)
                        inducement = Induce(inducement, inducements, swing, mode);
                }
            }

            var bar = bars[index];
            if (inducement is { SweptIndex: null } live && index > live.Index)
            {
                var swept = live.Kind == SwingKind.Low ? bar.Low < live.Level : bar.High > live.Level;
                if (swept)
                {
                    inducement = live with { SweptIndex = index, SweptTimeUtc = bar.TimeUtc };
                    taken = true;
                }
            }

            var up = trigger == BreakTrigger.Close ? bar.Close : bar.High;
            var down = trigger == BreakTrigger.Close ? bar.Close : bar.Low;

            // The level that turns the trend: the protected one once a trend is
            // known, and before that whichever of the first two swings gives way.
            var turnUp = trend == TrendDirection.Bullish ? null : trend == TrendDirection.Bearish ? guard : firstHigh;
            var turnDown = trend == TrendDirection.Bearish ? null : trend == TrendDirection.Bullish ? guard : firstLow;

            if (turnUp is { Kind: SwingKind.High } && up > turnUp.Price)
            {
                Break(StructureEventKind.Choch, TrendDirection.Bullish, turnUp.Price, turnUp.Index, turnUp.TimeUtc);
            }
            else if (turnDown is { Kind: SwingKind.Low } && down < turnDown.Price)
            {
                Break(StructureEventKind.Choch, TrendDirection.Bearish, turnDown.Price, turnDown.Index, turnDown.TimeUtc);
            }
            else if (trend == TrendDirection.Bullish && taken && target is { Kind: SwingKind.High } && up > target.Price)
            {
                Break(StructureEventKind.Bos, TrendDirection.Bullish, target.Price, target.Index, target.TimeUtc);
            }
            else if (trend == TrendDirection.Bearish && taken && target is { Kind: SwingKind.Low } && down < target.Price)
            {
                Break(StructureEventKind.Bos, TrendDirection.Bearish, target.Price, target.Index, target.TimeUtc);
            }

            void Break(StructureEventKind kind, TrendDirection direction, decimal level, int levelIndex, DateTime levelTime)
            {
                var turning = direction != trend;
                // The first break of all is a continuation of nothing: it is a BOS.
                if (kind == StructureEventKind.Choch && trend == TrendDirection.None) kind = StructureEventKind.Bos;
                events.Add(new StructureEvent(kind, direction, level, levelIndex, levelTime, index, bar.TimeUtc,
                    direction == TrendDirection.Bullish ? up : down));
                major.Add(levelIndex);

                trend = direction;
                // What the move came from now protects the trend: after a break it
                // is the pullback the break came out of, which is the inducement
                // the market has just taken. Breaking that turns the trend again.
                guard = direction == TrendDirection.Bullish ? lastLow ?? guard : lastHigh ?? guard;
                if (guard is not null) major.Add(guard.Index);

                // The level to break next is not known until the next swing confirms.
                target = null;
                // A turn starts a new leg; a BOS leaves the leg standing but still
                // needs a fresh pullback taken before the next one counts.
                inducement = Close(inducement, inducements, bar.TimeUtc);
                taken = false;
                _ = turning;
            }
        }

        if (inducement is not null) inducements.Add(inducement);
        for (var i = 0; i < swings.Count; i++)
        {
            if (major.Contains(swings[i].Index)) swings[i] = swings[i] with { Major = true };
        }

        return new MarketStructureResult(swings, events, inducements, trend, guard?.Price, target?.Price, taken,
            inducement is { SweptIndex: null } ? inducement.Level : null);
    }

    /// <summary>
    /// The leg's inducement. Taught as the <em>first</em> pullback of the leg —
    /// the one whose stops everyone who traded the break is sitting on — while
    /// the popular indicators use whichever pullback is current. Once it has been
    /// In <see cref="InducementMode.First"/> the leg keeps it until the leg ends;
    /// in <see cref="InducementMode.Last"/> each new pullback takes its place, and
    /// the leg waits for that one to be swept instead.
    /// </summary>
    private static Inducement Induce(
        Inducement? current, List<Inducement> kept, StructureSwing swing, InducementMode mode)
    {
        if (mode == InducementMode.First && current is not null) return current;
        // The leg has moved on to a newer pullback, so the old level stops
        // there rather than standing open to the right edge of the chart.
        if (current is not null)
            kept.Add(current with { EndedTimeUtc = current.SweptTimeUtc is null ? swing.TimeUtc : null });
        // Whether the leg has been induced is not forgotten because a newer
        // pullback formed: the stops were taken, and the leg only starts over
        // at a break.
        return new Inducement(swing.Kind, swing.Price, swing.Index, swing.TimeUtc, null, null);
    }

    /// <summary>The leg ended: whatever inducement it had is kept as it stood, and stops there.</summary>
    private static Inducement? Close(Inducement? current, List<Inducement> kept, DateTime endedAt)
    {
        if (current is not null) kept.Add(current with { EndedTimeUtc = current.SweptTimeUtc is null ? endedAt : null });
        return null;
    }
}
