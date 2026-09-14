namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>A pattern found on a candle, with the direction it argues for on that candle.</summary>
public readonly record struct PatternHit(CandlePattern Pattern, PatternDirection Direction);

/// <summary>
/// Recognises candle patterns on session-aligned candles. Pure: no clock, no database.
/// </summary>
/// <remarks>
/// <para>
/// Five definitions are ports of <c>src/AlgoTrading.PythonEngine/strategies/indicators.py</c>
/// with the same thresholds, so an alert and a strategy filter never disagree
/// about what a hammer is: <c>is_bullish_engulfing</c>, <c>is_bearish_engulfing</c>,
/// <c>is_hammer</c>, <c>is_shooting_star</c> and <c>is_marubozu</c>. Python computes
/// in floats and this in decimals, so the only possible disagreement is a tie
/// that a float cannot represent exactly; ratios are compared by multiplication
/// here rather than division to keep even that to the Python side's rounding.
/// </para>
/// <para>
/// The rest are defined here, with their thresholds as named constants below and
/// in plain words in <see cref="CandlePatternCatalog"/>.
/// </para>
/// <para>
/// Every multi-candle rule reads only candles of the same session with consecutive
/// indices. Yesterday's last candle is not "the previous candle" of today's first
/// (the overnight gap is not a shape), and a candle missing entirely because the
/// feed was down breaks the chain rather than silently pairing the candles either
/// side of the hole.
/// </para>
/// <para>
/// Hammer and shooting star read shape only, exactly as the strategies do. So a
/// hammer shape after a rise is reported twice — as a hammer (the strategies'
/// word) and as a hanging man (the same shape in its context) — and the page
/// says so rather than picking one.
/// </para>
/// </remarks>
public static class CandlePatternDetector
{
    /// <summary>A doji's body is at most this share of its range.</summary>
    public const decimal DojiMaxBodyRatio = 0.10m;

    /// <summary>A dragonfly's upper wick, or a gravestone's lower wick, is at most this share of the range.</summary>
    public const decimal DojiMaxWickRatio = 0.10m;

    /// <summary><c>is_hammer(bars, wick_ratio=2.0)</c> and <c>is_shooting_star(bars, wick_ratio=2.0)</c>.</summary>
    public const decimal HammerWickRatio = 2.0m;

    /// <summary><c>is_marubozu(bars, body_ratio=0.8)</c>.</summary>
    public const decimal MarubozuMinBodyRatio = 0.8m;

    /// <summary>Candles before the pattern candle whose average close defines "after a rise" and "after a fall".</summary>
    public const int TrendLookback = 5;

    /// <summary>The first candle of a morning or evening star is decisive: body at least this share of its range.</summary>
    public const decimal StarFirstMinBodyRatio = 0.5m;

    /// <summary>The middle candle (the star) has a body at most this share of the first candle's body.</summary>
    public const decimal StarMiddleMaxBodyRatio = 0.3m;

    private readonly record struct Candle(decimal Open, decimal High, decimal Low, decimal Close)
    {
        public decimal Body => Math.Abs(Close - Open);
        public decimal Span => High - Low;
        public decimal UpperWick => High - Math.Max(Open, Close);
        public decimal LowerWick => Math.Min(Open, Close) - Low;
        public bool Bullish => Close > Open;
        public bool Bearish => Close < Open;
        public decimal BodyMid => (Open + Close) / 2m;
    }

    /// <summary>
    /// Every pattern the candle at <paramref name="position"/> completes, reading
    /// the candles before it in <paramref name="sessionBars"/> (oldest first, one session).
    /// </summary>
    public static IReadOnlyList<PatternHit> Detect(IReadOnlyList<TimeframeBar> sessionBars, int position)
    {
        if (position < 0 || position >= sessionBars.Count) throw new ArgumentOutOfRangeException(nameof(position));

        var hits = new List<PatternHit>();
        var c = ToCandle(sessionBars[position]);
        var previous = Preceding(sessionBars, position, 2);
        Candle? prev = previous.Count >= 1 ? previous[^1] : null;

        // --- one candle -----------------------------------------------------
        if (IsDoji(c))
        {
            if (c.UpperWick <= DojiMaxWickRatio * c.Span)
                hits.Add(new(CandlePattern.DragonflyDoji, PatternDirection.Bullish));
            else if (c.LowerWick <= DojiMaxWickRatio * c.Span)
                hits.Add(new(CandlePattern.GravestoneDoji, PatternDirection.Bearish));
            else
                hits.Add(new(CandlePattern.Doji, PatternDirection.Neutral));
        }

        var trend = Trend(sessionBars, position, c);
        if (IsHammer(c))
        {
            hits.Add(new(CandlePattern.Hammer, PatternDirection.Bullish));
            if (trend > 0) hits.Add(new(CandlePattern.HangingMan, PatternDirection.Bearish));
        }

        if (IsShootingStar(c))
        {
            if (trend < 0) hits.Add(new(CandlePattern.InvertedHammer, PatternDirection.Bullish));
            hits.Add(new(CandlePattern.ShootingStar, PatternDirection.Bearish));
        }

        // --- two candles ----------------------------------------------------
        if (prev is { } p)
        {
            if (IsBullishEngulfing(p, c)) hits.Add(new(CandlePattern.BullishEngulfing, PatternDirection.Bullish));
            if (IsBearishEngulfing(p, c)) hits.Add(new(CandlePattern.BearishEngulfing, PatternDirection.Bearish));
        }

        if (IsMarubozu(c))
        {
            hits.Add(new(CandlePattern.Marubozu, c.Bullish ? PatternDirection.Bullish : PatternDirection.Bearish));
        }

        if (prev is { } q && IsInsideBar(q, c))
        {
            hits.Add(new(CandlePattern.InsideBar, PatternDirection.Neutral));
        }

        // --- three candles --------------------------------------------------
        if (previous.Count == 2)
        {
            if (IsMorningStar(previous[0], previous[1], c)) hits.Add(new(CandlePattern.MorningStar, PatternDirection.Bullish));
            if (IsEveningStar(previous[0], previous[1], c)) hits.Add(new(CandlePattern.EveningStar, PatternDirection.Bearish));
        }

        return hits;
    }

    private static bool IsDoji(Candle c) => c.Span > 0 && c.Body <= DojiMaxBodyRatio * c.Span;

    /// <summary>Port of <c>is_hammer</c>: a small body at the top with a long lower wick.</summary>
    private static bool IsHammer(Candle c) =>
        c.Span > 0 && c.Body > 0 && c.LowerWick >= HammerWickRatio * c.Body && c.UpperWick <= c.Body;

    /// <summary>Port of <c>is_shooting_star</c>: the mirror of <see cref="IsHammer"/>.</summary>
    private static bool IsShootingStar(Candle c) =>
        c.Span > 0 && c.Body > 0 && c.UpperWick >= HammerWickRatio * c.Body && c.LowerWick <= c.Body;

    /// <summary>Port of <c>is_bullish_engulfing</c>.</summary>
    private static bool IsBullishEngulfing(Candle prev, Candle last) =>
        prev.Bearish && last.Bullish && last.Close >= prev.Open && last.Open <= prev.Close && last.Body > prev.Body;

    /// <summary>Port of <c>is_bearish_engulfing</c>.</summary>
    private static bool IsBearishEngulfing(Candle prev, Candle last) =>
        prev.Bullish && last.Bearish && last.Close <= prev.Open && last.Open >= prev.Close && last.Body > prev.Body;

    /// <summary>Port of <c>is_marubozu</c>: <c>span &gt; 0 and body / span &gt;= 0.8</c>.</summary>
    private static bool IsMarubozu(Candle c) => c.Span > 0 && c.Body >= MarubozuMinBodyRatio * c.Span;

    /// <remarks>
    /// A candle with no range at all is not a contraction, just a minute with one
    /// price in it (the real 08 Sep SBIN 15:15 candle had one minute of data).
    /// </remarks>
    private static bool IsInsideBar(Candle prev, Candle c) =>
        c.Span > 0 && c.High <= prev.High && c.Low >= prev.Low && c.Span < prev.Span;

    private static bool IsMorningStar(Candle a, Candle b, Candle c) =>
        a.Bearish && a.Body >= StarFirstMinBodyRatio * a.Span
        && b.Body <= StarMiddleMaxBodyRatio * a.Body
        && Math.Max(b.Open, b.Close) < a.BodyMid
        && c.Bullish && c.Body > b.Body && c.Close > a.BodyMid;

    private static bool IsEveningStar(Candle a, Candle b, Candle c) =>
        a.Bullish && a.Body >= StarFirstMinBodyRatio * a.Span
        && b.Body <= StarMiddleMaxBodyRatio * a.Body
        && Math.Min(b.Open, b.Close) > a.BodyMid
        && c.Bearish && c.Body > b.Body && c.Close < a.BodyMid;

    /// <summary>
    /// +1 after a rise, −1 after a fall, 0 when flat or when fewer than
    /// <see cref="TrendLookback"/> consecutive candles precede this one in the session.
    /// </summary>
    private static int Trend(IReadOnlyList<TimeframeBar> bars, int position, Candle c)
    {
        var before = Preceding(bars, position, TrendLookback);
        if (before.Count < TrendLookback) return 0;
        var average = before.Sum(x => x.Close) / TrendLookback;
        return c.Close > average ? 1 : c.Close < average ? -1 : 0;
    }

    /// <summary>
    /// Up to <paramref name="count"/> candles immediately before <paramref name="position"/>,
    /// oldest first, stopping at the first gap in the indices.
    /// </summary>
    private static List<Candle> Preceding(IReadOnlyList<TimeframeBar> bars, int position, int count)
    {
        var result = new List<Candle>(count);
        int expectedIndex = bars[position].Index - 1;
        for (int i = position - 1; i >= 0 && result.Count < count; i--, expectedIndex--)
        {
            if (bars[i].Index != expectedIndex) break;
            result.Add(ToCandle(bars[i]));
        }

        result.Reverse();
        return result;
    }

    private static Candle ToCandle(TimeframeBar b) => new(b.Open, b.High, b.Low, b.Close);
}
