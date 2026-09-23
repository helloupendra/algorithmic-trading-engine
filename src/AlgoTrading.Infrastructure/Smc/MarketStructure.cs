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

/// <summary>
/// An order block: the candle an impulse came from, drawn wick to wick. Known
/// only at the candle that broke structure, which is usually several candles
/// later than the block's own.
/// </summary>
/// <remarks>
/// The schools do not agree on which candle this is. ICT's written teaching and
/// LuxAlgo's concept page name the last candle that closed against the move; the
/// smart-money-concepts Python package takes the most extreme low or high between
/// the swing and the breakout, which need not be an opposing candle at all; and
/// LuxAlgo's own shipped indicator anchors blocks at swing points instead. This
/// reader takes the last opposing close, because it is the reading the teaching
/// names and the only one the break itself makes knowable. Wick to wick rather
/// than body-only, for the same reason the inducement sweep is wick-inclusive
/// here — the cost is a wider zone that is touched sooner. Mitigated and
/// invalidated are two different states in the teaching: a zone used once, versus
/// a zone that failed and is re-read as a breaker. Only mitigation is modelled;
/// breaker blocks are not built.
/// </remarks>
public sealed record OrderBlock(
    TrendDirection Direction,
    decimal Top,
    decimal Bottom,
    /// <summary>The block's own candle, which is earlier than the candle that confirmed it.</summary>
    int Index,
    DateTime TimeUtc,
    /// <summary>The candle that broke structure. Nothing is drawn before it.</summary>
    int ConfirmedIndex,
    DateTime ConfirmedTimeUtc,
    /// <summary>The candle that came back for the block, by whichever <see cref="ZoneMitigation"/> reading was asked for. Null while it still stands.</summary>
    int? MitigatedIndex,
    DateTime? MitigatedTimeUtc);

/// <summary>
/// A fair-value gap: a band of price the first and third of three candles leave
/// untouched between them, so the move ran through it and nobody traded either
/// side of it there.
/// </summary>
/// <remarks>
/// Read geometrically, as LuxAlgo's concept page reads it: the middle candle's
/// body is not required to agree, because the gap is a fact about where price did
/// not trade and a body rule is a filter laid on top of it. The
/// smart-money-concepts Python package requires the body, and so draws fewer
/// gaps. No size threshold is applied by default either — every fast three-bar
/// move prints a gap, and a number invented in here would be worse than an honest
/// crowded chart, so the chart prunes instead by drawing unfilled gaps only.
/// Inversion gaps, where a filled gap flips role and starts acting as the
/// opposite level, are not built.
/// </remarks>
public sealed record FairValueGap(
    TrendDirection Direction,
    decimal Top,
    decimal Bottom,
    /// <summary>The first of the three candles, which is where the band starts.</summary>
    int Index,
    DateTime TimeUtc,
    /// <summary>The third candle, whose extreme closes the band. Nothing is drawn before it.</summary>
    int ConfirmedIndex,
    DateTime ConfirmedTimeUtc,
    /// <summary>The candle that came back into the gap, by whichever <see cref="ZoneMitigation"/> reading was asked for. Null while it stands open.</summary>
    int? FilledIndex,
    DateTime? FilledTimeUtc);

/// <summary>
/// A delivery run: the stretch of candles one reading of the market held for,
/// from the break that started it to the break that ended it.
/// </summary>
/// <remarks>
/// This is order flow in the narrative sense the SMC and ICT material use it —
/// which way the market is being delivered, inferred from price. It is not
/// footprint order flow: bid/ask delta, cumulative delta and volume at price all
/// need trade prints classified by aggressor side, and this reader is handed
/// nothing but OHLC. Nothing new is measured here; what is new is that the
/// reading is drawn along time, so its history is visible instead of only its
/// value at the last candle. Note a narrower ICT usage in which "bullish order
/// flow" means the corrective down-close candles around a break — which are the
/// very candles <see cref="OrderBlock"/> is cut from. The two marks are drawn
/// separately, so a chart that shows both has to say which usage it means.
/// </remarks>
public sealed record OrderFlowRun(
    TrendDirection Direction,
    /// <summary>The candle that broke structure and started the run. Nothing is drawn before it.</summary>
    int FromIndex,
    DateTime FromTimeUtc,
    /// <summary>The break that ended the run. Null while it is still running, which is drawn open to the right edge.</summary>
    int? ToIndex,
    DateTime? ToTimeUtc,
    /// <summary>The candle that swept this leg's inducement, which is where a break of structure became armed. Null until then.</summary>
    int? InducedIndex,
    DateTime? InducedTimeUtc);

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

/// <summary>When price coming back into a zone counts as having used it.</summary>
public enum ZoneMitigation
{
    /// <summary>
    /// A wick into the zone's near edge, and the default. It matches the asymmetry
    /// this module already reads elsewhere: a break needs a close, but a level is
    /// tested by a wick. The cost is that a zone is spent by the lightest touch,
    /// so on a noisy chart few blocks survive their first retest.
    /// </summary>
    Touch,

    /// <summary>
    /// A wick through the zone's midpoint — ICT's consequent encroachment, and
    /// LuxAlgo's Average setting. It keeps a zone alive through a shallow tag, at
    /// the cost of calling a zone unused after price has already traded inside it.
    /// </summary>
    Midpoint,

    /// <summary>
    /// A candle closing beyond the zone's far side: the strictest reading, and the
    /// one that keeps a zone longest. A fourth is taught — a full fill to the far
    /// edge on a wick alone — and is not offered here.
    /// </summary>
    Close,
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
    decimal? InducementLevel,
    /// <summary>The order blocks, in the order the breaks that confirmed them fired.</summary>
    IReadOnlyList<OrderBlock> OrderBlocks,
    /// <summary>Fair-value gaps, in the order they were confirmed. The ones still open carry no <see cref="FairValueGap.FilledIndex"/>.</summary>
    IReadOnlyList<FairValueGap> Gaps,
    /// <summary>The delivery runs, oldest first. The last one is still running if it has no <see cref="OrderFlowRun.ToIndex"/>.</summary>
    IReadOnlyList<OrderFlowRun> OrderFlowRuns);

/// <summary>
/// Reads market structure the way Smart Money Concepts teaches it: swing points
/// labelled HH / HL / LH / LL, breaks of structure, changes of character, the
/// inducement inside the current leg, and the three marks read off the same pass
/// — order blocks, fair-value gaps and the delivery runs. Pure: no clock, no
/// database, no I/O.
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
/// <item><b>Order block.</b> The last candle that closed against a move, taken
/// from between the swing that made the broken level and the candle that broke
/// it, drawn wick to wick. It is only known at the break — usually several
/// candles after the block's own candle, and often after price has already left
/// the zone. That lag is the same bargain the swing rule makes, and it is what
/// the popular scripts hide by drawing the box at its own candle.</item>
/// <item><b>Fair-value gap.</b> Three candles whose first and third do not
/// overlap, leaving a band the move ran through. Known at the third candle, and
/// only between candles that really do sit next to each other in time — see
/// <see cref="Contiguous"/> for why that has to be said out loud here.</item>
/// <item><b>Order flow.</b> The run of candles one reading held for, break to
/// break, stamped with the candle that swept the leg's inducement. Order flow in
/// the narrative sense, inferred from price; not the footprint sense, which this
/// reader has no data for.</item>
/// </list>
/// <para>
/// Where the schools differ, the switches say so: <see cref="SwingMethod"/> picks
/// how a swing is decided, <see cref="BreakTrigger"/> whether a wick counts,
/// <see cref="InducementMode"/> whether the inducement is the leg's first
/// pullback (as taught) or the one it is on now (what the popular indicators
/// read, and what keeps working on a strong trend), and
/// <see cref="ZoneMitigation"/> how far into a block or a gap price has to come
/// for the zone to count as used.
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
        InducementMode inducement = InducementMode.Last,
        ZoneMitigation zones = ZoneMitigation.Touch,
        TimeSpan? barInterval = null,
        decimal fvgMinSize = 0m)
    {
        strength = Math.Clamp(strength, 1, MaxStrength);
        var found = method == SwingMethod.Fractal
            ? Alternate(FindSwingsByFractal(bars, strength))
            : FindSwingsByPullback(bars);
        Label(found);
        return Walk(bars, found, trigger, inducement, zones, barInterval, fvgMinSize);
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
        IReadOnlyList<StructureBar> bars, List<StructureSwing> swings, BreakTrigger trigger, InducementMode mode,
        ZoneMitigation zones, TimeSpan? barInterval, decimal fvgMinSize)
    {
        var events = new List<StructureEvent>();
        var inducements = new List<Inducement>();
        var blocks = new List<OrderBlock>();
        var gaps = new List<FairValueGap>();
        var runs = new List<OrderFlowRun>();
        // Which blocks and gaps are still standing, as positions in the two lists
        // above. A zone is stamped where it lies rather than being closed and
        // re-added, because unlike the inducement a zone is not owned by a leg: an
        // order block outlives the break that made it and stays there to be come
        // back for, so a single live slot would not do.
        var liveBlocks = new List<int>();
        var liveGaps = new List<int>();
        var major = new HashSet<int>();
        var byConfirmation = swings.ToLookup(s => s.ConfirmedIndex);

        var trend = TrendDirection.None;
        StructureSwing? guard = null;              // breaking this is a change of character
        StructureSwing? target = null;             // breaking this, once the inducement is taken, is a BOS
        StructureSwing? firstHigh = null, firstLow = null;   // the two live levels before any trend
        StructureSwing? lastHigh = null, lastLow = null;
        Inducement? inducement = null;
        OrderFlowRun? run = null;                  // the delivery run the last break started
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

            // A fair-value gap: the candle two back and this one do not overlap,
            // so the middle candle ran through a band nobody traded either side
            // of. This sits in the walk rather than in either swing detector on
            // purpose — a gap is raw candle geometry and has to read the same
            // under ValidPullback and Fractal, and the walk is the only pass both
            // share. It is knowable here and at no earlier candle, because it
            // needs this candle's own extreme; the widely used Python package
            // records the gap against the middle candle while reading the next one
            // through shift(-1), so a consumer reading that row is using a price
            // the chart had not yet shown. Ours is one candle later than theirs,
            // and the cost is that you cannot act on the gap at the middle
            // candle's close, which is where those scripts appear to let you in.
            if (index >= 2 && Contiguous(bars, index, barInterval))
            {
                var first = bars[index - 2];
                // The middle candle's body is not asked to agree: the gap is a
                // fact about where price did not trade, and a body rule is a
                // filter laid on top of it. That reading draws more gaps, which
                // is why the chart prunes by hiding the filled ones.
                if (bar.Low > first.High && bar.Low - first.High >= fvgMinSize)
                {
                    gaps.Add(new FairValueGap(TrendDirection.Bullish, bar.Low, first.High,
                        index - 2, first.TimeUtc, index, bar.TimeUtc, null, null));
                    liveGaps.Add(gaps.Count - 1);
                }
                else if (bar.High < first.Low && first.Low - bar.High >= fvgMinSize)
                {
                    gaps.Add(new FairValueGap(TrendDirection.Bearish, first.Low, bar.High,
                        index - 2, first.TimeUtc, index, bar.TimeUtc, null, null));
                    liveGaps.Add(gaps.Count - 1);
                }
            }

            if (inducement is { SweptIndex: null } live && index > live.Index)
            {
                var swept = live.Kind == SwingKind.Low ? bar.Low < live.Level : bar.High > live.Level;
                if (swept)
                {
                    inducement = live with { SweptIndex = index, SweptTimeUtc = bar.TimeUtc };
                    taken = true;
                    // The same fact, stamped on the run: a break of structure is
                    // only armed once the leg's stops have been taken, so this is
                    // the moment the delivery reading becomes something to act on
                    // rather than something to watch. A latch, like Major — set
                    // once and never unset, so a reader of the first n candles
                    // sees exactly what a reader of all of them sees for those n.
                    if (run is { InducedIndex: null })
                        run = run with { InducedIndex = index, InducedTimeUtc = bar.TimeUtc };
                }
            }

            // Both kinds of zone are come back for the same way, so both lists are
            // swept here, and both are guarded on the candle that CONFIRMED the
            // zone rather than the zone's own candle: price is still inside an
            // order block's range while the impulse is leaving it, and a gap's
            // near edge is drawn by the very wick of the candle that confirms it.
            for (var i = liveBlocks.Count - 1; i >= 0; i--)
            {
                var block = blocks[liveBlocks[i]];
                if (index <= block.ConfirmedIndex || !Used(bar, block.Direction, block.Top, block.Bottom, zones)) continue;
                blocks[liveBlocks[i]] = block with { MitigatedIndex = index, MitigatedTimeUtc = bar.TimeUtc };
                liveBlocks.RemoveAt(i);
            }

            for (var i = liveGaps.Count - 1; i >= 0; i--)
            {
                var gap = gaps[liveGaps[i]];
                if (index <= gap.ConfirmedIndex || !Used(bar, gap.Direction, gap.Top, gap.Bottom, zones)) continue;
                gaps[liveGaps[i]] = gap with { FilledIndex = index, FilledTimeUtc = bar.TimeUtc };
                liveGaps.RemoveAt(i);
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
                // The first break of all is a continuation of nothing: it is a BOS.
                if (kind == StructureEventKind.Choch && trend == TrendDirection.None) kind = StructureEventKind.Bos;
                events.Add(new StructureEvent(kind, direction, level, levelIndex, levelTime, index, bar.TimeUtc,
                    direction == TrendDirection.Bullish ? up : down));
                major.Add(levelIndex);

                // The order block is knowable here and nowhere earlier: until the
                // break fired, the candles behind the impulse were only candles.
                // Scanning backwards from the break towards the swing that made
                // the level keeps this inside what the reader has already seen —
                // the same bounded backward scan Extreme() makes. The last candle
                // that closed against the move is the block, wick to wick; if the
                // range holds none, nothing is drawn, because saying nothing beats
                // inventing a zone.
                //
                // The scan starts one candle behind the break rather than at it,
                // because the teaching asks for the last opposing candle BEFORE
                // the move and the break candle is part of the move. It would
                // otherwise qualify more often than it looks: a break candle only
                // has to close — or, under BreakTrigger.Wick, wick — through the
                // level, and nothing stops it closing against the break's own
                // direction while doing so. The block would then be the break
                // candle's own range, which price has just finished trading
                // through: true to the letter of the scan and of no use to anyone
                // reading the chart. Excluding it costs nothing, since a candle
                // inside the impulse is not a candle the impulse left behind.
                for (var i = index - 1; i >= levelIndex; i--)
                {
                    var candle = bars[i];
                    if (direction == TrendDirection.Bullish ? candle.Close >= candle.Open : candle.Close <= candle.Open)
                        continue;
                    blocks.Add(new OrderBlock(direction, candle.High, candle.Low, i, candle.TimeUtc,
                        index, bar.TimeUtc, null, null));
                    liveBlocks.Add(blocks.Count - 1);
                    break;
                }

                // The break is the moment the delivery changed, so the run that
                // held until now ends on this candle and the next one starts on
                // it. Neither end is ever revised.
                if (run is not null) runs.Add(run with { ToIndex = index, ToTimeUtc = bar.TimeUtc });
                run = new OrderFlowRun(direction, index, bar.TimeUtc, null, null, null, null);

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
            }
        }

        if (inducement is not null) inducements.Add(inducement);
        // The run in progress is kept with no right edge, the way an unswept
        // inducement is: the reading held to the last candle we were given, and
        // where it ends is not a fact yet. Blocks and gaps need no flush — they go
        // into their lists at the candle that confirmed them, and the ones still
        // standing simply carry no mitigation stamp.
        if (run is not null) runs.Add(run);
        for (var i = 0; i < swings.Count; i++)
        {
            if (major.Contains(swings[i].Index)) swings[i] = swings[i] with { Major = true };
        }

        return new MarketStructureResult(swings, events, inducements, trend, guard?.Price, target?.Price, taken,
            inducement is { SweptIndex: null } ? inducement.Level : null, blocks, gaps, runs);
    }

    /// <summary>
    /// Whether the three candles ending at <paramref name="index"/> really do sit
    /// next to each other in time.
    /// </summary>
    /// <remarks>
    /// This is the one place the reader cannot take its own input at face value.
    /// The candles it is handed are not contiguous: the API drops every row
    /// stamped outside the 09:15-15:30 session, so on a five-minute chart the
    /// candle two back from 09:20 is yesterday's 15:20. Any gap open then
    /// satisfies the fair-value-gap inequality trivially, and a reader that did
    /// not check would invent a zone the width of a night every single morning.
    /// This module is pure and is handed no resolution, so it cannot work the
    /// spacing out for itself — the caller passes the interval for intraday
    /// candles and leaves it null for daily ones, where the overnight gap IS the
    /// fair-value gap and must not be filtered away.
    /// </remarks>
    private static bool Contiguous(IReadOnlyList<StructureBar> bars, int index, TimeSpan? barInterval)
        => barInterval is not { } interval || bars[index].TimeUtc - bars[index - 2].TimeUtc <= interval * 2;

    /// <summary>
    /// Whether this candle came back for a zone, by the reading asked for. A zone
    /// is entered from the side the impulse left it on — a bullish one sits below
    /// price and is come back down into, a bearish one sits above — so the near
    /// edge is the top of the first and the bottom of the second.
    /// </summary>
    private static bool Used(StructureBar bar, TrendDirection direction, decimal top, decimal bottom, ZoneMitigation mode)
    {
        if (direction == TrendDirection.Bullish)
        {
            return mode switch
            {
                ZoneMitigation.Midpoint => bar.Low <= (top + bottom) / 2m,
                ZoneMitigation.Close => bar.Close < bottom,
                _ => bar.Low <= top,
            };
        }

        return mode switch
        {
            ZoneMitigation.Midpoint => bar.High >= (top + bottom) / 2m,
            ZoneMitigation.Close => bar.Close > top,
            _ => bar.High >= bottom,
        };
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
