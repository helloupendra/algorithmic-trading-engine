namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>A number an alert quotes: "rsi" 71.2, "rsiBefore" 68.4, "vwap" 25,040.12.</summary>
public readonly record struct IndicatorValue(string Name, double Value);

/// <summary>One rule that fired on one closed candle.</summary>
/// <param name="Index">The candle's position in the series handed to <see cref="IndicatorEvaluator.Evaluate"/>.</param>
/// <param name="Up">Which way it crossed: RSI or the fast EMA upward, Supertrend turned up, the close above VWAP.</param>
public sealed record IndicatorHit(IndicatorRule Rule, int Index, TimeframeBar Candle, bool Up, IReadOnlyList<IndicatorValue> Values);

/// <summary>A hit with its cooldown decided.</summary>
/// <param name="CoolingSinceUtc">Null when it is an alert in its own right; otherwise the close of
/// the alert (same symbol, timeframe and rule) whose cooldown it fell inside.</param>
public sealed record CooledHit(IndicatorHit Hit, DateTime? CoolingSinceUtc)
{
    public bool CooledDown => CoolingSinceUtc is not null;
}

/// <summary>Where one rule stands on one watch after the last closed candle.</summary>
/// <param name="Candles">Closed candles the rule can read (earlier sessions and today's).</param>
/// <param name="Needed">Candles it must read before it may alert (<see cref="IndicatorRule.SettleCandles"/>).</param>
public sealed record IndicatorReadiness(IndicatorRule Rule, int Candles, int Needed)
{
    public bool Ready => Candles >= Needed;
}

/// <summary>
/// Finds the candles on which a rule fired. Pure: the scanner hands it closed
/// candles only, and everything here reads a candle and the ones before it.
/// </summary>
/// <remarks>
/// <para>
/// No repainting. A candle still forming is never in the series (the scanner
/// filters on <see cref="TimeframeBar.IsClosed"/>), and every indicator value
/// at candle <c>i</c> is computed from candles <c>0..i</c>. So a cross, once
/// found, is never withdrawn by a later tick, and the scan at 10:45:20 finds
/// what the scan at 11:00 finds for the 10:30 candle.
/// </para>
/// <para>
/// A cross is a change of side between the previous candle and this one. For
/// RSI the level is the boundary (from at-or-below to above, from at-or-above
/// to below). For the EMAs and VWAP a tie is no side at all: the cross is
/// measured against the last candle that was on one side, so two EMAs that
/// touch and part the same way have not crossed. Exact ties are rare with
/// prices, but a flat stretch makes both EMAs equal, and floating point then
/// leaves a difference of one unit in the last place that must not count as
/// a side (<see cref="SideOf"/>).
/// </para>
/// </remarks>
public static class IndicatorEvaluator
{
    /// <summary>
    /// Every hit of every rule on candles <paramref name="firstIndex"/> onward
    /// whose indicators have settled. Hits on earlier candles (the warm-up,
    /// earlier sessions) are never returned; their values still feed today's.
    /// </summary>
    /// <param name="candles">Closed candles, oldest first: earlier sessions, then today's.</param>
    /// <param name="firstIndex">The first candle alerts may fire on: today's first.</param>
    /// <param name="vwap">Session VWAP per candle (<see cref="IndicatorMath.SessionVwap"/>); null skips vwap-cross.</param>
    public static IReadOnlyList<IndicatorHit> Evaluate(
        IReadOnlyList<TimeframeBar> candles,
        int firstIndex,
        IEnumerable<IndicatorRule> rules,
        IReadOnlyList<double?>? vwap = null)
    {
        var hits = new List<IndicatorHit>();
        if (candles.Count == 0) return hits;
        var closes = candles.Select(c => (double)c.Close).ToList();
        int start = Math.Max(firstIndex, 1);

        foreach (var rule in rules)
        {
            bool Settled(int i) => i + 1 >= rule.SettleCandles;

            switch (rule.Kind)
            {
                case IndicatorRuleKind.RsiAbove:
                case IndicatorRuleKind.RsiBelow:
                {
                    var rsi = IndicatorMath.Rsi(closes, rule.Period);
                    double level = (double)rule.Level;
                    for (int i = start; i < candles.Count; i++)
                    {
                        if (!Settled(i) || rsi[i - 1] is not { } before || rsi[i] is not { } now) continue;
                        bool crossed = rule.Kind == IndicatorRuleKind.RsiAbove
                            ? before <= level && now > level
                            : before >= level && now < level;
                        if (crossed)
                            hits.Add(new IndicatorHit(rule, i, candles[i], rule.Kind == IndicatorRuleKind.RsiAbove,
                                [new("rsiBefore", before), new("rsi", now)]));
                    }

                    break;
                }
                case IndicatorRuleKind.EmaCross:
                {
                    var fast = IndicatorMath.Ema(closes, rule.Period);
                    var slow = IndicatorMath.Ema(closes, rule.SlowPeriod);
                    int lastSide = 0;
                    for (int i = 0; i < candles.Count; i++)
                    {
                        if (fast[i] is not { } f || slow[i] is not { } s) continue;
                        int side = SideOf(f - s, s);
                        if (side == 0) continue;
                        if (lastSide != 0 && side != lastSide && i >= start && Settled(i))
                            hits.Add(new IndicatorHit(rule, i, candles[i], side > 0, [new("emaFast", f), new("emaSlow", s)]));
                        lastSide = side;
                    }

                    break;
                }
                case IndicatorRuleKind.SupertrendFlip:
                {
                    var st = IndicatorMath.Supertrend(candles, rule.Period, (double)rule.Multiplier);
                    for (int i = start; i < candles.Count; i++)
                    {
                        if (!Settled(i) || st[i - 1] is not { } before || st[i] is not { } now || before.Up == now.Up) continue;
                        // The band the close went through, and where the new line starts.
                        hits.Add(new IndicatorHit(rule, i, candles[i], now.Up, [new("through", now.Through ?? before.Line), new("line", now.Line)]));
                    }

                    break;
                }
                case IndicatorRuleKind.VwapCross:
                {
                    if (vwap is null) break;
                    int lastSide = 0;
                    for (int i = 0; i < candles.Count && i < vwap.Count; i++)
                    {
                        if (vwap[i] is not { } v)
                        {
                            // A candle of another session: VWAP starts again.
                            lastSide = 0;
                            continue;
                        }

                        int side = SideOf(closes[i] - v, v);
                        if (side == 0) continue;
                        if (lastSide != 0 && side != lastSide && i >= start && Settled(i))
                            hits.Add(new IndicatorHit(rule, i, candles[i], side > 0, [new("vwap", v)]));
                        lastSide = side;
                    }

                    break;
                }
            }
        }

        return hits.OrderBy(h => h.Index).ThenBy(h => h.Rule.Kind).ThenBy(h => h.Rule.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>How many candles each rule has to read, against what it needs.</summary>
    public static IReadOnlyList<IndicatorReadiness> Readiness(int candles, IEnumerable<IndicatorRule> rules) =>
        rules.Select(r => new IndicatorReadiness(r, candles, r.SettleCandles)).ToList();

    /// <summary>
    /// +1, −1, or 0 for a difference too small to be a side: a billionth of the
    /// price, far below a tick and far above floating-point noise.
    /// </summary>
    public static int SideOf(double difference, double scale)
    {
        double tolerance = 1e-9 * Math.Max(1.0, Math.Abs(scale));
        return difference > tolerance ? 1 : difference < -tolerance ? -1 : 0;
    }

    /// <summary>
    /// The cooldown, per rule: a hit within <paramref name="cooldown"/> of the
    /// last hit that was an alert (by candle close) is cooled down; the next
    /// one after the window is an alert again and starts a new window. Measured
    /// from the last alert, not the last hit, so a market that keeps crossing
    /// still gets one alert per window rather than none.
    /// </summary>
    /// <remarks>
    /// Recomputed from the day's candles on every scan rather than remembered,
    /// so a restart decides every hit exactly as the first run did.
    /// </remarks>
    /// <param name="hits">Hits of one symbol and timeframe.</param>
    public static IReadOnlyList<CooledHit> ApplyCooldown(IEnumerable<IndicatorHit> hits, TimeSpan cooldown)
    {
        var result = new List<CooledHit>();
        foreach (var rule in hits.GroupBy(h => h.Rule.Key, StringComparer.Ordinal))
        {
            DateTime? lastAlert = null;
            foreach (var hit in rule.OrderBy(h => h.Candle.EndUtc))
            {
                if (lastAlert is { } since && hit.Candle.EndUtc - since < cooldown)
                {
                    result.Add(new CooledHit(hit, since));
                    continue;
                }

                result.Add(new CooledHit(hit, null));
                lastAlert = hit.Candle.EndUtc;
            }
        }

        return result.OrderBy(c => c.Hit.Index).ThenBy(c => c.Hit.Rule.Kind).ThenBy(c => c.Hit.Rule.Key, StringComparer.Ordinal).ToList();
    }
}
