// src/AlgoTrading.Infrastructure/Services/PaperPnl.cs

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The arithmetic that says how much money a position made.
/// </summary>
/// <remarks>
/// Pulled out of <see cref="PaperTradingService"/> because it had quietly been
/// written twice. The service kept these as private statics, so
/// <c>LiveRunHistoryBuilder</c> — which could not reach them — hand-rolled the
/// unrealized formula inline, three lines below a correct call to the service's
/// shared <c>UsedCapitalOf</c>. The habit was right there; the P&amp;L half just
/// could not follow it.
/// <para>
/// The two copies had already drifted: one compared direction with
/// <c>== "LONG"</c> and the other with <c>OrdinalIgnoreCase</c>, so a position
/// stored as "long" would have been valued as a short by one of them and a long
/// by the other. The careful spelling wins here.
/// </para>
/// <para>
/// No DbContext, no services, nothing to mock — which is the point. This is the
/// number the whole platform is judged on, and it should be provable in
/// isolation.
/// </para>
/// </remarks>
public static class PaperPnl
{
    public const string Long = "LONG";
    public const string Short = "SHORT";

    /// <summary>True for a long position, whatever case it was stored in.</summary>
    public static bool IsLong(string? direction)
        => string.Equals(direction, Long, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// P&amp;L booked when a position is closed at <paramref name="exitPrice"/>.
    /// </summary>
    public static decimal Realized(string? direction, decimal averagePrice, decimal exitPrice, int quantity, int lotSize)
        => Value(direction, averagePrice, exitPrice, quantity, lotSize);

    /// <summary>
    /// P&amp;L an open position currently shows, marked at <paramref name="markPrice"/>.
    /// </summary>
    public static decimal Unrealized(string? direction, decimal averagePrice, decimal markPrice, int quantity, int lotSize)
        => Value(direction, averagePrice, markPrice, quantity, lotSize);

    /// <summary>
    /// Realized and unrealized are the same sum against a different price, so
    /// they are one function. Keeping them apart is how the copies drifted.
    /// </summary>
    private static decimal Value(string? direction, decimal averagePrice, decimal price, int quantity, int lotSize)
    {
        int multiplier = Math.Max(1, lotSize);
        return IsLong(direction)
            ? (price - averagePrice) * quantity * multiplier
            : (averagePrice - price) * quantity * multiplier;
    }
}
