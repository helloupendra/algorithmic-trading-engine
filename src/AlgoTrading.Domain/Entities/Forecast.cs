namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One forecast for one session: what a model said about it before the open,
/// what the baseline said, and — after the close — what happened and how both
/// were scored.
/// </summary>
/// <remarks>
/// <para>
/// The Analysis module (docs/modules/analysis.md) exists to answer whether
/// anything about the market can be predicted better than a simple baseline,
/// and to prove it. The proof rests on this row being written before and
/// scored after: <see cref="IssuedUtc"/> is the API's own clock, never the
/// caller's, the API refuses a forecast for a session that has already opened,
/// and it accepts the outcome only once, after that session's close. Nothing
/// updates the prediction afterwards.
/// </para>
/// <para>
/// The prediction, baseline, inputs, outcome and scores are JSON kept as text
/// (like <see cref="Incident.EvidenceJson"/>): their shape belongs to the
/// Python models and differs by target, and the API stores them as sent. Only
/// the two losses are columns, because they are what the scoreboard adds up.
/// </para>
/// </remarks>
public class Forecast
{
    public long Id { get; set; }

    /// <summary>The model, for example <c>range.har-vix</c>.</summary>
    public string ModelKey { get; set; } = string.Empty;

    /// <summary>
    /// The model version that made it, for example <c>2026-09-27.1</c>. With
    /// <see cref="ModelKey"/> it names a registered <see cref="ForecastModel"/>.
    /// </summary>
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>One of <see cref="ForecastTargets"/>.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>One of <see cref="ForecastUnderlyings"/>: NIFTY, BANKNIFTY or SENSEX.</summary>
    public string Underlying { get; set; } = string.Empty;

    /// <summary>The IST calendar date of the session the forecast is for.</summary>
    public DateOnly SessionDate { get; set; }

    /// <summary>When the API stored it, by the API's clock. Always before that session's open.</summary>
    public DateTime IssuedUtc { get; set; }

    /// <summary>The model's forecast, as JSON: <c>{ "p": 0.38 }</c>, or the range's median, band and buckets.</summary>
    public string PredictionJson { get; set; } = "{}";

    /// <summary>The baseline's forecast for the same session, in the same shape as the prediction.</summary>
    public string BaselineJson { get; set; } = "{}";

    /// <summary>What the model saw, as JSON, shown on the page next to the forecast.</summary>
    public string InputsJson { get; set; } = "{}";

    /// <summary>The session as it happened (open, high, low, close, range, bucket…); null until scored.</summary>
    public string? OutcomeJson { get; set; }

    /// <summary>The losses, metrics and calibration pairs, as JSON; null until scored.</summary>
    public string? ScoresJson { get; set; }

    /// <summary>The target's loss for the model (lower is better); null until scored.</summary>
    public double? Loss { get; set; }

    /// <summary>The same loss for the baseline; null until scored.</summary>
    public double? BaselineLoss { get; set; }

    /// <summary>
    /// When the outcome was written; null until then. Once set it never
    /// changes: a forecast is scored exactly once.
    /// </summary>
    public DateTime? ScoredUtc { get; set; }
}

/// <summary>
/// A registered model version: what it forecasts, how it works, and what its
/// walk-forward backtest found.
/// </summary>
/// <remarks>
/// A forecast can be issued only by a registered version, so every live row
/// on the scoreboard can be traced to a description and a backtest. The
/// backtest is shown beside the live record and is never what makes a model
/// Proven; only live, scored forecasts do that.
/// </remarks>
public class ForecastModel
{
    /// <summary>The model, for example <c>range.har-vix</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The version, for example <c>2026-09-27.1</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>One of <see cref="ForecastTargets"/>. Fixed for a version once registered.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>One or two sentences a reader understands.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The walk-forward backtest (design, validation, holdout…), as JSON, as registered; null when none was sent.</summary>
    public string? BacktestJson { get; set; }

    public DateTime RegisteredUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>What a forecast is about. The spelling is the API contract's.</summary>
public static class ForecastTargets
{
    /// <summary>The session's high − low, as a percentage of the previous close.</summary>
    public const string Range = "range";

    /// <summary>A trend day: |close − open| ≥ 0.6 × (high − low).</summary>
    public const string Trend = "trend";

    /// <summary>close &gt; open.</summary>
    public const string Direction = "direction";

    /// <summary>All three, in the order the scoreboard lists them.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Range, Trend, Direction };

    /// <summary>The canonical spelling of <paramref name="value"/>, or null when it is not a target.</summary>
    public static string? Normalize(string? value)
    {
        string wanted = value?.Trim() ?? string.Empty;
        return All.FirstOrDefault(t => string.Equals(t, wanted, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The indices forecasts are made for, and the exchange whose session each one follows.</summary>
public static class ForecastUnderlyings
{
    public const string Nifty = "NIFTY";
    public const string BankNifty = "BANKNIFTY";
    public const string Sensex = "SENSEX";

    /// <summary>All three, in the order the scoreboard lists them.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Nifty, BankNifty, Sensex };

    /// <summary>
    /// The exchange whose session a forecast for this index is about. It is
    /// what "the session has opened" is checked against, and SENSEX is BSE's:
    /// on a day the two exchanges' calendars differ, NSE's would be the wrong
    /// clock.
    /// </summary>
    public static string ExchangeOf(string underlying) => underlying switch
    {
        Nifty or BankNifty => "NSE",
        Sensex => "BSE",
        _ => throw new ArgumentOutOfRangeException(nameof(underlying), underlying, "Not a forecast underlying."),
    };

    /// <summary>The canonical spelling of <paramref name="value"/>, or null when forecasts are not made for it.</summary>
    public static string? Normalize(string? value)
    {
        string wanted = value?.Trim() ?? string.Empty;
        return All.FirstOrDefault(u => string.Equals(u, wanted, StringComparison.OrdinalIgnoreCase));
    }
}
