using AlgoTrading.Infrastructure.Smc;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Smart Money Concepts structure reader: swing points, their HH / HL / LH /
/// LL labels, breaks of structure, changes of character, the inducement, and the
/// three marks read off the same pass — order blocks, fair-value gaps and the
/// delivery runs.
///
/// The candles below are a hand-built schematic, one five-minute bar each, so
/// every mark can be checked by eye against the numbers.
/// </summary>
public class MarketStructureTests
{
    private static readonly DateTime Start = new(2026, 9, 18, 3, 45, 0, DateTimeKind.Utc);

    private static List<StructureBar> Bars(params (decimal Open, decimal High, decimal Low, decimal Close)[] rows)
        => rows.Select((r, i) => new StructureBar(Start.AddMinutes(5 * i), r.Open, r.High, r.Low, r.Close)).ToList();

    /// <summary>
    /// A rally into 109 (bar 3) that a pullback validates (bar 4), a fall to 100
    /// (bar 6), a recovery (bar 7), the first break at 109 (bar 9), the leg's
    /// inducement at 106 (bar 12) which stands until bar 16 takes it, the break
    /// of the leg's high 114 (bar 19), and a close through the protected 104
    /// (bar 23) that turns the trend.
    /// </summary>
    private static List<StructureBar> Schematic() => Bars(
        (100, 102, 99, 101),        // 0
        (101, 105, 100, 104),       // 1
        (104, 108, 103, 107),       // 2
        (107, 109, 106, 108),       // 3  swing high 109
        (108, 108, 104, 105),       // 4  takes bar 3's low: the pullback is valid
        (105, 106, 101, 102),       // 5
        (102, 103, 100, 101),       // 6  swing low 100
        (101, 104, 101, 103),       // 7  takes bar 6's high: the low stands
        (103, 107, 102, 106),       // 8
        (106, 111, 105, 110),       // 9  closes through 109: the first break
        (110, 112, 109, 111),       // 10 swing high 112
        (111, 111, 107, 108),       // 11 takes bar 10's low
        (108, 110, 106, 109),       // 12 swing low 106: the leg's inducement
        (109, 112, 108, 111),       // 13 takes bar 12's high
        (111, 114, 110, 113),       // 14 closes above 112 — but the inducement still stands
        (113, 113, 108, 109),       // 15
        (109, 110, 104, 105),       // 16 takes the inducement at 106
        (105, 109, 104, 108),       // 17
        (108, 113, 107, 112),       // 18
        (112, 116, 111, 115),       // 19 closes through the leg's high 114: BOS
        (115, 117, 113, 116),       // 20 swing high 117
        (116, 116, 110, 111),       // 21
        (111, 112, 105, 106),       // 22
        (106, 107, 101, 102));      // 23 closes through the protected 104

    private static MarketStructureResult Read() => MarketStructure.Read(Schematic());

    /// <summary>
    /// A bullish fair-value gap spanning 100 to 104, then three candles that come
    /// back for it to exactly one depth each: a touch of the near edge (bar 3),
    /// the midpoint at 102 (bar 4), and a close beyond the far edge (bar 5). Each
    /// <see cref="ZoneMitigation"/> reading therefore lands on its own candle, so
    /// a mode that silently fell back to another would show up as the wrong index
    /// rather than as no difference at all.
    /// </summary>
    private static List<StructureBar> ComingBackForTheGap() => Bars(
        (98, 100, 97, 99),          // 0  high 100: the near side of the band
        (99, 105, 99, 104),         // 1  the candle the move ran through
        (104, 106, 104, 105),       // 2  low 104: the gap stands at 100-104
        (105, 105, 103, 104),       // 3  wicks to 103: inside the gap, above its midpoint
        (104, 104, 101, 102),       // 4  wicks to 101: through the midpoint
        (102, 102, 98, 99));        // 5  closes at 99: beyond the far edge

    /// <summary>
    /// Two candles at the end of one session and two at the start of the next.
    /// The session opens 09:15 IST, which is 03:45 UTC, so bar 2 sits nearly
    /// eighteen hours after bar 0 while being its immediate neighbour in the list.
    /// </summary>
    private static List<StructureBar> AcrossTheOvernightGap() =>
    [
        new(Start.AddMinutes(360), 100, 102, 99, 101),            // 15:15 IST
        new(Start.AddMinutes(365), 101, 103, 100, 102),           // 15:20 IST
        new(Start.AddDays(1), 110, 112, 109, 111),                // 09:15 IST, gapped open
        new(Start.AddDays(1).AddMinutes(5), 111, 113, 110, 112),  // 09:20 IST
    ];

    [Fact]
    public void A_high_stands_as_a_swing_when_a_candle_takes_the_liquidity_of_the_candle_that_made_it()
    {
        var high = Read().Swings.First(s => s.Kind == SwingKind.High);

        Assert.Equal(109m, high.Price);
        Assert.Equal(3, high.Index);
        Assert.Equal(4, high.ConfirmedIndex);       // the candle that traded below bar 3's low
        Assert.Equal(Start.AddMinutes(20), high.ConfirmedTimeUtc);
    }

    [Fact]
    public void The_candle_the_data_starts_on_can_itself_be_a_structural_point()
    {
        // Bar 1 trades above bar 0's high, so bar 0's low is a swing low by the
        // same rule. It is an artefact of where the chart begins, not a reading
        // of a leg this data contains, so it carries no label.
        var first = Read().Swings[0];

        Assert.Equal(SwingKind.Low, first.Kind);
        Assert.Equal(0, first.Index);
        Assert.Null(first.Label);
    }

    [Fact]
    public void Swings_alternate_and_carry_their_labels()
    {
        Assert.Equal(
            [(SwingKind.Low, 99m, (SwingLabel?)null), (SwingKind.High, 109m, null),
             (SwingKind.Low, 100m, SwingLabel.HigherLow), (SwingKind.High, 112m, SwingLabel.HigherHigh),
             (SwingKind.Low, 106m, SwingLabel.HigherLow), (SwingKind.High, 114m, SwingLabel.HigherHigh),
             (SwingKind.Low, 104m, SwingLabel.LowerLow), (SwingKind.High, 117m, SwingLabel.HigherHigh)],
            Read().Swings.Select(s => (s.Kind, s.Price, s.Label)));
    }

    [Fact]
    public void The_first_high_and_low_carry_no_label_because_there_is_nothing_to_compare_them_with()
    {
        var result = Read();

        Assert.Null(result.Swings.First(s => s.Kind == SwingKind.High).Label);
        Assert.Null(result.Swings.First(s => s.Kind == SwingKind.Low).Label);
    }

    [Fact]
    public void A_lower_high_after_a_higher_low_is_named_as_such()
    {
        // Up to 110, back to 100, up to 108 (lower), back to 102 (higher).
        var bars = Bars(
            (100, 105, 99, 104), (104, 110, 103, 109), (109, 109, 102, 103),
            (103, 104, 100, 101), (101, 106, 100, 105), (105, 108, 104, 107),
            (107, 107, 103, 104), (104, 105, 102, 103), (103, 107, 102, 106));

        var result = MarketStructure.Read(bars);

        Assert.Equal([SwingLabel.LowerHigh], result.Swings.Where(s => s.Kind == SwingKind.High).Skip(1).Select(s => s.Label));
        Assert.Equal([SwingLabel.HigherLow, SwingLabel.HigherLow], result.Swings.Where(s => s.Kind == SwingKind.Low).Skip(1).Select(s => s.Label));
    }

    [Fact]
    public void The_first_level_to_give_way_sets_the_trend_and_is_recorded_as_a_break_of_structure()
    {
        var first = Read().Events[0];

        Assert.Equal(StructureEventKind.Bos, first.Kind);        // nothing was established to change from
        Assert.Equal(TrendDirection.Bullish, first.Direction);
        Assert.Equal(109m, first.Level);
        Assert.Equal(3, first.LevelIndex);                       // drawn from the swing that made the level
        Assert.Equal(9, first.BreakIndex);                       // to the candle that closed through it
    }

    [Fact]
    public void A_break_of_structure_waits_for_the_legs_inducement_to_be_taken()
    {
        var result = Read();

        // Bar 14 closes at 113, above the 112 swing high, while the inducement
        // at 106 is still standing: the market has not taken the stops of the
        // traders who bought the first break, so the leg has not proved itself.
        Assert.DoesNotContain(result.Events, e => e.BreakIndex == 14);

        var idm = result.Inducements.First(i => i.Level == 106m);
        Assert.Equal(12, idm.Index);
        Assert.Equal(16, idm.SweptIndex);

        // Once it is taken, the next close through the leg's high is a BOS.
        var bos = result.Events[1];
        Assert.Equal(StructureEventKind.Bos, bos.Kind);
        Assert.Equal(114m, bos.Level);           // the highest high since the trend turned, not the last swing
        Assert.Equal(14, bos.LevelIndex);
        Assert.Equal(19, bos.BreakIndex);
    }

    [Fact]
    public void A_close_through_the_protected_level_turns_the_trend()
    {
        var result = Read();

        var choch = result.Events[2];
        Assert.Equal(StructureEventKind.Choch, choch.Kind);
        Assert.Equal(TrendDirection.Bearish, choch.Direction);
        Assert.Equal(104m, choch.Level);         // the low the last rally came from
        Assert.Equal(23, choch.BreakIndex);
        Assert.Equal(TrendDirection.Bearish, result.Trend);
        Assert.Equal(117m, result.ProtectedLevel);   // breaking this would turn it back
    }

    [Fact]
    public void The_marks_are_only_these_three()
    {
        Assert.Equal([(StructureEventKind.Bos, 109m), (StructureEventKind.Bos, 114m), (StructureEventKind.Choch, 104m)],
            Read().Events.Select(e => (e.Kind, e.Level)));
    }

    [Fact]
    public void The_swings_the_structure_turned_on_are_marked_apart_from_the_pullbacks()
    {
        var result = Read();

        Assert.Equal([109m, 100m, 114m, 104m, 117m], result.Swings.Where(s => s.Major).Select(s => s.Price));
        Assert.Contains(result.Swings, s => !s.Major && s.Price == 106m);   // the inducement is not structure
    }

    [Fact]
    public void The_leg_takes_the_pullback_it_is_on_unless_it_is_told_to_keep_the_first()
    {
        // Two pullbacks inside one bullish leg: 103 first, then 109.
        var bars = Bars(
            (100, 102, 99, 101), (101, 105, 100, 104), (104, 109, 103, 108),   // swing high 109 at 2
            (108, 108, 102, 103), (103, 104, 100, 101), (101, 106, 100, 105),   // swing low 100 at 4
            (105, 111, 104, 110),                                              // closes through 109: the first break
            (110, 110, 103, 105), (105, 112, 104, 111),                        // swing low 103 at 7: the first pullback
            (111, 116, 110, 115), (115, 115, 109, 110), (110, 117, 109, 116));  // swing low 109 at 10: a later pullback

        Assert.Equal([103m, 109m], MarketStructure.Read(bars).Inducements.Select(i => i.Level));
        Assert.Equal([103m], MarketStructure.Read(bars, inducement: InducementMode.First).Inducements.Select(i => i.Level));
    }

    [Fact]
    public void A_wick_through_a_level_is_not_a_break_unless_the_reader_is_told_to_take_wicks()
    {
        // Bar 9's high pierces 109 but its close does not.
        var bars = Schematic();
        bars[9] = bars[9] with { High = 111m, Close = 108m };

        Assert.DoesNotContain(MarketStructure.Read(bars).Events, e => e.BreakIndex == 9);
        Assert.Contains(MarketStructure.Read(bars, trigger: BreakTrigger.Wick).Events, e => e.BreakIndex == 9);
    }

    [Fact]
    public void An_inducement_is_taken_by_a_wick_through_it()
    {
        // Bar 16 trades to 104 and closes at 105, above the inducement at 106.
        var result = Read();

        Assert.Equal(16, result.Inducements.First(i => i.Level == 106m).SweptIndex);
    }

    [Fact]
    public void A_fair_value_gap_is_the_band_the_first_and_third_candles_leave_between_them()
    {
        var result = Read();

        // Bar 2's low of 103 stands above bar 0's high of 102, so the band
        // between them is one bar 1 ran through and its neighbours never touched.
        var first = result.Gaps[0];

        Assert.Equal(TrendDirection.Bullish, first.Direction);
        Assert.Equal(103m, first.Top);
        Assert.Equal(102m, first.Bottom);
        Assert.Equal(0, first.Index);                // the band starts at the first of the three
        Assert.Equal(2, first.ConfirmedIndex);       // and cannot be known before the third
        Assert.Equal(Start.AddMinutes(10), first.ConfirmedTimeUtc);
        Assert.Equal(5, first.FilledIndex);          // bar 5 trades back down to 101
    }

    [Fact]
    public void A_gap_that_price_has_not_come_back_for_carries_no_fill_rather_than_a_flag()
    {
        // Unfilled is the absence of a stamp, not a boolean that could be
        // recomputed — the same shape as an inducement that was never swept.
        Assert.Equal([(20, 113m), (21, 110m)],
            Read().Gaps.Where(g => g.FilledIndex is null).Select(g => (g.Index, g.Top)));
    }

    [Fact]
    public void How_far_price_has_to_come_back_into_a_zone_is_the_readers_to_choose()
    {
        var bars = ComingBackForTheGap();

        // The same gap, three readings of what using it means. Touch is the
        // default because a level is tested by a wick here while a break needs a
        // close; the cost is that the lightest tag spends the zone.
        Assert.Equal(3, MarketStructure.Read(bars).Gaps[0].FilledIndex);
        Assert.Equal(4, MarketStructure.Read(bars, zones: ZoneMitigation.Midpoint).Gaps[0].FilledIndex);
        Assert.Equal(5, MarketStructure.Read(bars, zones: ZoneMitigation.Close).Gaps[0].FilledIndex);
    }

    [Fact]
    public void A_gap_smaller_than_the_reader_was_told_to_care_about_is_not_drawn()
    {
        // No threshold by default: every fast three-bar move prints a gap, and a
        // number invented in the engine would be worse than a crowded chart. The
        // two gaps here span four points and one, so asking for five leaves none.
        // Note this is the one switch that puts arithmetic at the rule's boundary,
        // so a Python port would stop agreeing with this reader once it is used.
        Assert.Equal(2, MarketStructure.Read(ComingBackForTheGap()).Gaps.Count);
        Assert.Empty(MarketStructure.Read(ComingBackForTheGap(), fvgMinSize: 5m).Gaps);
    }

    [Fact]
    public void An_overnight_gap_is_not_a_fair_value_gap_when_the_reader_is_told_how_far_apart_its_candles_are()
    {
        var bars = AcrossTheOvernightGap();

        // Bar 2's low of 109 sits far above bar 0's high of 102, but those two
        // candles are a night apart rather than ten minutes: the API hands this
        // reader only the rows inside the 09:15-15:30 session, so the list is not
        // contiguous in time and the inequality is met trivially every morning.
        Assert.Empty(MarketStructure.Read(bars, barInterval: TimeSpan.FromMinutes(5)).Gaps);

        // Left null — the daily reading — the overnight gap IS the fair-value gap,
        // and filtering it away would delete the classic case.
        Assert.Contains(MarketStructure.Read(bars).Gaps,
            g => g.Direction == TrendDirection.Bullish && g.Index == 0 && g.Top == 109m && g.Bottom == 102m);
    }

    [Fact]
    public void An_order_block_is_the_last_candle_that_closed_against_the_move_that_broke_structure()
    {
        var result = Read();

        var first = result.OrderBlocks[0];

        Assert.Equal(TrendDirection.Bullish, first.Direction);
        Assert.Equal(6, first.Index);                // the last down close before the rally
        Assert.Equal(103m, first.Top);               // wick to wick, not body only
        Assert.Equal(100m, first.Bottom);
        Assert.Equal(9, first.ConfirmedIndex);       // the candle that closed through 109
        Assert.Equal(Start.AddMinutes(45), first.ConfirmedTimeUtc);
        Assert.Equal(23, first.MitigatedIndex);      // price is gone for fourteen candles before it comes back
    }

    [Fact]
    public void An_order_block_is_drawn_from_the_break_and_not_from_its_own_candle()
    {
        var result = Read();

        // Three breaks, three blocks, each one lagging the candle it is cut from.
        // That lag is the honest reading's whole cost and it is stated here as a
        // number: you often cannot buy the block on the candle it appears, which
        // is exactly what the popular scripts hide by drawing the box earlier.
        Assert.Equal([(6, 9), (16, 19), (20, 23)],
            result.OrderBlocks.Select(b => (b.Index, b.ConfirmedIndex)));
        Assert.Equal(result.Events.Select(e => e.BreakIndex), result.OrderBlocks.Select(b => b.ConfirmedIndex));
        Assert.Null(result.OrderBlocks[^1].MitigatedIndex);   // the last one has had no candle to be used by
    }

    [Fact]
    public void An_impulse_with_no_candle_closing_against_it_leaves_no_order_block()
    {
        // Bar 3 dips under bar 2's low — enough to stand the high at 109 up as a
        // swing — but still closes up, and so does every other candle between the
        // swing and the break. There is nothing here that the teaching would call
        // a block, so nothing is drawn: saying nothing beats inventing a zone.
        var bars = Bars(
            (100, 102, 99, 101),        // 0
            (101, 105, 100, 104),       // 1  takes bar 0's high: the low at 99 stands
            (104, 109, 103, 108),       // 2  swing high 109
            (107, 108, 102, 108),       // 3  trades below bar 2's low and still closes up
            (108, 111, 107, 110));      // 4  closes through 109: the break

        var result = MarketStructure.Read(bars);

        Assert.Single(result.Events);
        Assert.Empty(result.OrderBlocks);
    }

    [Fact]
    public void The_break_candle_is_not_the_block_even_when_it_is_the_one_that_closed_against_the_move()
    {
        // A break candle only has to get through the level, not close in the
        // break's direction: bar 5 wicks to 111, through the 109 high, and still
        // closes below its own open. By the letter of the scan it is the last
        // candle that closed against the move, but it IS the move — price has
        // just traded the whole of its range — so the block is bar 3, the last
        // opposing candle the impulse left behind.
        var bars = Bars(
            (100, 102, 99, 101),        // 0
            (101, 105, 100, 104),       // 1  takes bar 0's high: the low at 99 stands
            (104, 109, 103, 108),       // 2  swing high 109
            (108, 108, 102, 103),       // 3  takes bar 2's low, and closes down
            (103, 108, 102, 107),       // 4
            (107, 111, 105, 106));      // 5  wicks through 109 and closes below its open

        var wick = MarketStructure.Read(bars, trigger: BreakTrigger.Wick);

        Assert.Equal(5, wick.Events.Single().BreakIndex);

        var block = Assert.Single(wick.OrderBlocks);
        Assert.Equal(3, block.Index);
        Assert.Equal(108m, block.Top);               // and not 111 / 105, which is bar 5's own range
        Assert.Equal(102m, block.Bottom);
        Assert.Equal(5, block.ConfirmedIndex);

        // The same reading under the default close trigger. There it takes a gap
        // through the level and a candle that gives part of it back — the news
        // open rather than the everyday candle the wick trigger makes of it.
        bars[5] = bars[5] with { Open = 112m, High = 113m, Low = 110m, Close = 111m };

        var closed = MarketStructure.Read(bars);

        Assert.Equal(5, closed.Events.Single().BreakIndex);
        Assert.Equal(3, Assert.Single(closed.OrderBlocks).Index);
    }

    [Fact]
    public void The_delivery_runs_meet_end_to_end_and_the_last_one_is_left_open()
    {
        var result = Read();

        Assert.Equal([(TrendDirection.Bullish, 9), (TrendDirection.Bullish, 19), (TrendDirection.Bearish, 23)],
            result.OrderFlowRuns.Select(r => (r.Direction, r.FromIndex)));

        // Nothing is drawn before the first break, which is right: which way the
        // market is being delivered is genuinely not knowable until then.
        Assert.Equal(result.Events[0].BreakIndex, result.OrderFlowRuns[0].FromIndex);

        // Each run ends on the candle the next one starts on — the break belongs
        // to both readings, as the last candle of one and the first of the next.
        Assert.Equal(
            result.OrderFlowRuns.Skip(1).Select(r => (int?)r.FromIndex),
            result.OrderFlowRuns.SkipLast(1).Select(r => r.ToIndex));

        // The run in progress has no right edge, the way an unswept inducement has
        // none: the reading held to the last candle we were given and where it
        // ends is not a fact yet.
        Assert.Null(result.OrderFlowRuns[^1].ToIndex);

        // Bar 16 took the leg's inducement at 106, which is the candle the first
        // run's break of structure became something to act on rather than watch.
        Assert.Equal(16, result.OrderFlowRuns[0].InducedIndex);
        Assert.Null(result.OrderFlowRuns[1].InducedIndex);   // that leg was never induced before it ended
    }

    [Fact]
    public void Nothing_is_marked_before_the_candle_that_confirmed_it()
    {
        // Reading a prefix of the candles gives the same marks as reading them
        // all: a mark that moved later would be a repainting chart.
        var bars = Schematic();
        var whole = MarketStructure.Read(bars);
        // An inducement names the swing it was cut from but not the candle that
        // confirmed that swing, so the filter for it has to go back to the swing.
        var confirmedAt = whole.Swings.ToDictionary(s => s.Index, s => s.ConfirmedIndex);

        // From the very first candle, not from the fifth: a gap is confirmed at
        // bar 2, earlier than any swing here, and the cheap prefixes are where an
        // off-by-one in a new mark's guard would show.
        for (var cut = 1; cut <= bars.Count; cut++)
        {
            var prefix = MarketStructure.Read(bars.Take(cut).ToList());

            Assert.Equal(
                whole.Swings.Where(s => s.ConfirmedIndex < cut).Select(s => (s.Index, s.Price, s.Kind)),
                prefix.Swings.Select(s => (s.Index, s.Price, s.Kind)));
            Assert.Equal(
                whole.Events.Where(e => e.BreakIndex < cut).Select(e => (e.Kind, e.Level, e.BreakIndex)),
                prefix.Events.Select(e => (e.Kind, e.Level, e.BreakIndex)));
            Assert.Equal(
                whole.Inducements.Where(i => confirmedAt[i.Index] < cut)
                    .Select(i => (i.Kind, i.Level, i.Index, Swept: i.SweptIndex < cut ? i.SweptIndex : null)),
                prefix.Inducements.Select(i => (i.Kind, i.Level, i.Index, Swept: i.SweptIndex)));

            // The three new marks are filtered on the candle that CONFIRMED them,
            // not on the candle they are drawn from. An order block's own candle
            // is several candles behind its break, so a filter on Index would fail
            // here for the wrong reason and tempt someone into anchoring the block
            // at its own candle — which is the repaint this whole rule forbids.
            Assert.Equal(
                whole.OrderBlocks.Where(b => b.ConfirmedIndex < cut)
                    .Select(b => (b.Direction, b.Top, b.Bottom, b.Index, b.ConfirmedIndex,
                        Mitigated: b.MitigatedIndex < cut ? b.MitigatedIndex : null)),
                prefix.OrderBlocks.Select(b => (b.Direction, b.Top, b.Bottom, b.Index, b.ConfirmedIndex,
                    Mitigated: b.MitigatedIndex)));
            Assert.Equal(
                whole.Gaps.Where(g => g.ConfirmedIndex < cut)
                    .Select(g => (g.Direction, g.Top, g.Bottom, g.Index, g.ConfirmedIndex,
                        Filled: g.FilledIndex < cut ? g.FilledIndex : null)),
                prefix.Gaps.Select(g => (g.Direction, g.Top, g.Bottom, g.Index, g.ConfirmedIndex,
                    Filled: g.FilledIndex)));
            // A run is confirmed by the break that started it, so FromIndex is the
            // filter. Its right edge advancing to the last candle is not a repaint:
            // where a live run ends is not a fact yet, which is why it is null here
            // and stamped only once the next break has actually printed.
            Assert.Equal(
                whole.OrderFlowRuns.Where(r => r.FromIndex < cut)
                    .Select(r => (r.Direction, r.FromIndex, To: r.ToIndex < cut ? r.ToIndex : null,
                        Induced: r.InducedIndex < cut ? r.InducedIndex : null)),
                prefix.OrderFlowRuns.Select(r => (r.Direction, r.FromIndex, To: r.ToIndex, Induced: r.InducedIndex)));
        }
    }

    [Fact]
    public void The_fractal_reading_finds_the_same_turning_points_later()
    {
        var result = MarketStructure.Read(Schematic(), SwingMethod.Fractal, strength: 2);

        // The same turning points, without the artefact at the left edge, and
        // each one confirmed two candles after the swing rather than at the
        // candle that took its liquidity.
        Assert.Equal([109m, 100m, 112m, 106m, 117m], result.Swings.Select(s => s.Price));
        Assert.Equal(5, result.Swings[0].ConfirmedIndex);
        Assert.Equal(4, Read().Swings.First(s => s.Kind == SwingKind.High).ConfirmedIndex);
    }

    [Fact]
    public void Candles_that_only_rise_break_nothing()
    {
        var bars = Bars((100, 101, 99, 100), (100, 102, 100, 101), (101, 103, 101, 102));

        var result = MarketStructure.Read(bars);

        Assert.Equal([SwingKind.Low], result.Swings.Select(s => s.Kind));   // only where the chart begins
        Assert.Empty(result.Events);
        Assert.Empty(result.Inducements);
        Assert.Empty(result.OrderBlocks);
        Assert.Empty(result.Gaps);
        Assert.Empty(result.OrderFlowRuns);   // a run starts at a break, and there has been none
        Assert.Equal(TrendDirection.None, result.Trend);
    }

    [Fact]
    public void An_empty_series_is_read_without_complaint()
    {
        var result = MarketStructure.Read([]);

        Assert.Empty(result.Swings);
        Assert.Empty(result.Events);
        Assert.Empty(result.Inducements);
        Assert.Empty(result.OrderBlocks);
        Assert.Empty(result.Gaps);
        Assert.Empty(result.OrderFlowRuns);
        Assert.Null(result.ProtectedLevel);
    }
}
