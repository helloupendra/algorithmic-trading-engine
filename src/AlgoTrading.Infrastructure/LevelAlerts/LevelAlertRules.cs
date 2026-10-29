using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;

namespace AlgoTrading.Infrastructure.LevelAlerts;

/// <summary>What a level alert may say, checked before it is saved: pure, so the form's words are pinned by tests.</summary>
public static class LevelAlertRules
{
    public const int MaxNoteLength = 200;

    /// <summary>No index chain has had a PCR near this; a level above it is a typo.</summary>
    public const decimal MaxPcrLevel = 10m;

    public const decimal MaxPriceRearmPercent = 20m;
    public const decimal MaxPcrRearmPoints = 5m;

    /// <summary>"Price", " pcr " → "price", "pcr"; null for anything else.</summary>
    public static string? NormalizeKind(string? kind)
    {
        var k = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return LevelAlertKinds.All.Contains(k) ? k : null;
    }

    public static string? NormalizeCondition(string? condition)
    {
        var c = (condition ?? string.Empty).Trim().ToLowerInvariant();
        return LevelAlertConditions.All.Contains(c) ? c : null;
    }

    /// <summary>
    /// A price's symbol as the feeds key it ("nse:nifty50-index" → "NSE:NIFTY50-INDEX"),
    /// or a chain's underlying ("banknifty" → "BANKNIFTY"); null when it is neither.
    /// </summary>
    public static string? NormalizeTarget(string kind, string? target)
    {
        if (kind == LevelAlertKinds.Price) return CandlePatternRules.NormalizeSymbol(target);

        var u = (target ?? string.Empty).Trim().ToUpperInvariant();
        return u.Length is > 0 and <= 40 && u.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '&' or '-') ? u : null;
    }

    /// <summary>Everything wrong with a rule as submitted, in words for the form. Empty when it can be saved.</summary>
    public static IReadOnlyList<string> Validate(
        string? kind,
        string? target,
        string? condition,
        decimal? level,
        bool repeat,
        decimal? rearmBy,
        string? note,
        DateOnly? expiresOn,
        DateOnly today)
    {
        var errors = new List<string>();

        var k = NormalizeKind(kind);
        if (k is null)
        {
            errors.Add("Pick what to watch: a price or a PCR.");
            return errors;
        }

        if (NormalizeTarget(k, target) is null)
        {
            errors.Add(k == LevelAlertKinds.Price
                ? "The symbol must be EXCHANGE:NAME on NSE, BSE or MCX, for example NSE:NIFTY50-INDEX."
                : "Name the chain's underlying, for example NIFTY or BANKNIFTY.");
        }

        var c = NormalizeCondition(condition);
        if (c is null) errors.Add("Pick when it fires: above, below or touch.");
        else if (k == LevelAlertKinds.Pcr && c == LevelAlertConditions.Touch)
            errors.Add("A PCR rule fires when it goes above or below a level; touch is for prices.");

        if (level is not { } l || l <= 0) errors.Add("The level must be a number above zero.");
        else if (k == LevelAlertKinds.Pcr && l > MaxPcrLevel) errors.Add($"A PCR level is at most {MaxPcrLevel:0}.");

        if (repeat && rearmBy is { } gap)
        {
            if (k == LevelAlertKinds.Price && gap is <= 0 or > MaxPriceRearmPercent)
                errors.Add($"Re-arm after a move back of more than 0% and at most {MaxPriceRearmPercent:0}% of the level.");
            if (k == LevelAlertKinds.Pcr && gap is <= 0 or > MaxPcrRearmPoints)
                errors.Add($"Re-arm after a move back of more than 0 and at most {MaxPcrRearmPoints:0} PCR points.");
        }

        if (note is { Length: > MaxNoteLength }) errors.Add($"The note is at most {MaxNoteLength} characters.");
        if (expiresOn is { } last && last < today) errors.Add("The last day is in the past.");

        return errors;
    }
}
