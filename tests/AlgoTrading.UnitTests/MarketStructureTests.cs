using AlgoTrading.Infrastructure.Smc;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Smart Money Concepts structure reader: swing points, their HH / HL / LH /
/// LL labels, breaks of structure, changes of character and the inducement.
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
    public void Nothing_is_marked_before_the_candle_that_confirmed_it()
    {
        // Reading a prefix of the candles gives the same marks as reading them
        // all: a mark that moved later would be a repainting chart.
        var bars = Schematic();
        var whole = MarketStructure.Read(bars);

        for (var cut = 5; cut <= bars.Count; cut++)
        {
            var prefix = MarketStructure.Read(bars.Take(cut).ToList());

            Assert.Equal(
                whole.Swings.Where(s => s.ConfirmedIndex < cut).Select(s => (s.Index, s.Price, s.Kind)),
                prefix.Swings.Select(s => (s.Index, s.Price, s.Kind)));
            Assert.Equal(
                whole.Events.Where(e => e.BreakIndex < cut).Select(e => (e.Kind, e.Level, e.BreakIndex)),
                prefix.Events.Select(e => (e.Kind, e.Level, e.BreakIndex)));
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
        Assert.Equal(TrendDirection.None, result.Trend);
    }

    [Fact]
    public void An_empty_series_is_read_without_complaint()
    {
        var result = MarketStructure.Read([]);

        Assert.Empty(result.Swings);
        Assert.Empty(result.Events);
        Assert.Empty(result.Inducements);
        Assert.Null(result.ProtectedLevel);
    }
}
