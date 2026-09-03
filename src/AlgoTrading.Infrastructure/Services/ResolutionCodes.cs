// src/AlgoTrading.Infrastructure/Services/ResolutionCodes.cs
using System.Globalization;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The one place that maps between the two resolution spellings the platform
/// uses. Canonical (candle-table / API) form: "1", "5", "15", "D" — what
/// <c>candles.Resolution</c> holds and what FYERS history expects. Strategy
/// form (keys of StrategyInput.bars, DataRequirement.resolution): "1m", "5m",
/// "15m", "1D". <c>live_bars</c> holds "1m" and is addressed via
/// <see cref="LiveBarResolution"/>. Never compare resolution strings without
/// going through <see cref="ToCandle"/>.
/// </summary>
public static class ResolutionCodes
{
    /// <summary>The resolution string stored in live_bars (1-minute bars aggregated from ticks).</summary>
    public const string LiveBarResolution = "1m";

    public const string Daily = "D";

    /// <summary>Canonical resolutions a backtest may be driven at.</summary>
    public static readonly IReadOnlyList<string> Allowed = new[] { "1", "5", "15", "D" };

    /// <summary>
    /// Any spelling ("5m", "5", "5M", "1D", "d", "day", "1m") to the canonical
    /// candle code ("5", "D", "1"). Empty input maps to "D", matching the
    /// historical default of the FYERS sync. Unknown values are returned trimmed
    /// and upper-cased so a bad input still fails loudly downstream.
    /// </summary>
    public static string ToCandle(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return Daily;

        var value = resolution.Trim().ToUpperInvariant();
        if (value is "D" or "1D" or "DAY" or "DAILY") return Daily;

        if (value.EndsWith("MIN", StringComparison.Ordinal)) value = value[..^3];
        else if (value.EndsWith('M')) value = value[..^1];

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            return minutes.ToString(CultureInfo.InvariantCulture);
        }

        return value;
    }

    /// <summary>Any spelling to the strategy-facing form: "5" → "5m", "D" → "1D".</summary>
    public static string ToStrategy(string? resolution)
    {
        var canonical = ToCandle(resolution);
        return canonical == Daily ? "1D" : canonical + "m";
    }

    /// <summary>Display label; identical to the strategy form ("5m", "1D").</summary>
    public static string Label(string? resolution) => ToStrategy(resolution);

    /// <summary>Bar length in minutes, or null for daily.</summary>
    public static int? MinutesOf(string? resolution)
    {
        var canonical = ToCandle(resolution);
        if (canonical == Daily) return null;
        return int.TryParse(canonical, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? minutes : null;
    }

    /// <summary>True when the value normalises to one of <see cref="Allowed"/>.</summary>
    public static bool IsAllowed(string? resolution)
        => Allowed.Contains(ToCandle(resolution), StringComparer.Ordinal);

    /// <summary>True when both spellings denote the same resolution.</summary>
    public static bool AreEqual(string? a, string? b)
        => string.Equals(ToCandle(a), ToCandle(b), StringComparison.Ordinal);
}
